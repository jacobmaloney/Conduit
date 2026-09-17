using System.Runtime.CompilerServices;
using Conduit.Core.SyncModels;
using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// SYNC-SERVICE-05 (Conduit side, D7): the IdentityCenter project binding. Source/SQL contract pins
/// plus the migrator's own list; SQL Server behavior of the filtered unique index is separate
/// acceptance. Also pins that the execution leg (D6) was NOT built: IdentityCenter's AgentPolicy
/// requires scope=agent, which no enrolled Conduit key carries.
/// </summary>
public class SyncExecutionServerBindingContractTests
{
    [Fact]
    public void Migration_36_adds_the_guarded_column_and_filtered_unique_index_once()
    {
        var migrator = new DatabaseMigrator("Server=offline;Database=offline;", NullLogger<DatabaseMigrator>.Instance);

        var pending = migrator.GetRequiredMigrations(new SchemaAnalysisResult { CurrentVersion = 35 });
        var m36 = Assert.Single(pending);
        Assert.Equal(36, m36.Version);
        Assert.Contains("IF COL_LENGTH('dbo.SyncProjects','IdentityCenterProjectId') IS NULL", m36.SqlScript);
        Assert.Contains("ALTER TABLE [dbo].[SyncProjects] ADD [IdentityCenterProjectId] UNIQUEIDENTIFIER NULL;", m36.SqlScript);
        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_SyncProjects_IdentityCenterProjectId'", m36.SqlScript);
        Assert.Contains("CREATE UNIQUE NONCLUSTERED INDEX [UX_SyncProjects_IdentityCenterProjectId]", m36.SqlScript);
        Assert.Contains("WHERE [IdentityCenterProjectId] IS NOT NULL", m36.SqlScript);

        // Idempotent at the list level: an applied 36 is not offered again; versions are unique.
        Assert.Empty(migrator.GetRequiredMigrations(new SchemaAnalysisResult { CurrentVersion = 36 }));
        var all = migrator.GetRequiredMigrations(new SchemaAnalysisResult { CurrentVersion = 0 });
        Assert.Equal(all.Count, all.Select(m => m.Version).Distinct().Count());
        Assert.Equal(36, all.Max(m => m.Version));
    }

    [Fact]
    public void Model_carries_the_binding_and_only_the_dedicated_writer_touches_it()
    {
        Assert.Equal(typeof(Guid?), typeof(SyncProject).GetProperty("IdentityCenterProjectId")!.PropertyType);

        var source = Read("src/Conduit.DataAccess/Repositories/SyncProjectRepository.cs");
        var create = Between(source, "public async Task<SyncProject> CreateAsync", "public async Task<bool> UpdateAsync");
        var update = Between(source, "public async Task<bool> UpdateAsync", "public async Task ResetForFullSyncAsync");
        var clone = Between(source, "public async Task<Guid> CloneAsync", "public Task<int> DeleteAsync");
        foreach (var region in new[] { create, update, clone })
            Assert.DoesNotContain("IdentityCenterProjectId", region);   // a clone must never inherit a binding
    }

    [Fact]
    public void Binding_write_is_one_predicated_update_that_refuses_a_second_holder()
    {
        var sql = Normalize(SyncProjectRepository.SetIdentityCenterBindingSql);
        Assert.Contains("UPDATE SyncProjects SET IdentityCenterProjectId = @IdentityCenterProjectId, LastModified = @LastModified WHERE Id = @Id", sql);
        Assert.Contains("AND (@IdentityCenterProjectId IS NULL OR NOT EXISTS (SELECT 1 FROM SyncProjects o WHERE o.IdentityCenterProjectId = @IdentityCenterProjectId AND o.Id <> @Id))", sql);

        var source = Read("src/Conduit.DataAccess/Repositories/SyncProjectRepository.cs");
        var writer = Between(source, "public async Task<bool> SetIdentityCenterBindingAsync", "public static string BindingRefusedReason");
        Assert.Contains("identityCenterProjectId == Guid.Empty", writer);
        Assert.Contains("return rows == 1;", writer);
        Assert.Contains("WHERE IdentityCenterProjectId = @IdentityCenterProjectId", Between(source, "public Task<SyncProject?> GetByIdentityCenterProjectIdAsync", "public async Task<bool> SetIdentityCenterBindingAsync"));
    }

    [Fact]
    public void Duplicate_binding_reason_names_the_holder()
    {
        var ic = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"); var holder = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        Assert.Equal(
            "Binding refused: IdentityCenter project aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa is already bound to Conduit project 'HR feed' (bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb). Unbind it there first.",
            SyncProjectRepository.BindingRefusedReason(ic, "HR feed", holder));
    }

    [Fact]
    public void Endpoint_sits_on_the_ApiV1_controller_behind_the_sibling_tenant_guard_and_refuses_with_409()
    {
        var source = Read("src/Conduit.Web/Controllers/ApiV1SyncRunsController.cs");
        var endpoint = Between(source, "[HttpPut(\"sync-projects/{projectId:guid}/identity-center-binding\")]", "internal static string SanitizeTriggeredBy");
        var guard = endpoint.IndexOf("AuthorizeForProject(project)", StringComparison.Ordinal);
        var write = endpoint.IndexOf("_projects.SetIdentityCenterBindingAsync(projectId, identityCenterProjectId)", StringComparison.Ordinal);
        Assert.True(guard >= 0 && write > guard, "the tenant-scope guard must run before the write");
        Assert.Contains("Guid.Empty", endpoint);
        Assert.Contains("Conflict(new { error = SyncProjectRepository.BindingRefusedReason(wanted, holder.Name, holder.Id) })", endpoint);
        Assert.Contains("holder.Id != projectId", endpoint);
        // Class-level auth is unchanged for the whole controller.
        Assert.Contains("[Authorize]", Between(source, "[ApiController]", "public class ApiV1SyncRunsController"));
    }

    /// <summary>
    /// The execution leg landed in SYNC-SERVICE-06 as its OWN service; the V140 command channel is
    /// unchanged and must stay that way. The IdentityCenter-side key gap is still open — AgentPolicy
    /// requires scope=agent and Conduit's enrolled keys carry agent:commands + agent:heartbeat — so the
    /// executor must report a refused key as a permanent condition rather than as an empty queue.
    /// </summary>
    [Fact]
    public void Command_poller_stays_the_command_channel_and_the_job_queue_lives_in_its_own_service()
    {
        var poller = Read("src/Conduit.Web/Services/IcAgentCommandPollerService.cs");
        Assert.DoesNotContain("api/jobs/claim", poller);
        Assert.DoesNotContain("api/agents/register", poller);
        Assert.DoesNotContain("ExecutionServerApiKey", poller);
        Assert.Contains("/api/agent/commands/claim", poller);   // the V140 channel still runs

        var channel = Read("src/Conduit.Web/Services/IcSyncJobChannel.cs");
        Assert.Contains("/api/jobs/claim", channel);
        Assert.DoesNotContain("api/agents/register", channel);   // registration is still enrollment's job, not the executor's

        var pump = Read("src/Conduit.Web/Services/IcSyncJobPump.cs");
        Assert.Contains("AgentPolicy requires a scope=agent claim", pump);
    }

    private static string Normalize(string sql) => System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ").Trim();

    private static string Between(string source, string start, string end)
    {
        var first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, start);
        var last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, end);
        return source[first..last];
    }

    private static string Read(string relative, [CallerFilePath] string file = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", relative));
}
