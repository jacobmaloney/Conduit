using System.Runtime.CompilerServices;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// Deactivating a Connected System is the operator saying "this system is off". The scheduler gated
/// only on IsEnabled and a cron expression and never looked at the connection, so it kept firing into
/// systems the UI showed as switched off, writing to a target the admin believed was unreachable.
///
/// <para>These are source and SQL contract pins, matching SyncRunOwnershipContractTests. Behaviour
/// against a real SQL Server, and the concurrency the atomic claim defends, remain acceptance work.</para>
/// </summary>
public class ScheduledConnectionStateTests
{
    [Fact]
    public void The_admission_statement_can_refuse_a_deactivated_or_missing_connection()
    {
        var admission = Between(Read("src/Conduit.DataAccess/Repositories/SyncProjectRepository.cs"),
            "public async Task<bool> SetRunningAsync", "public Task ClearRunningAsync");

        // Both endpoints, checked inside the same UPDATE as the running and enabled guards. A filter
        // on the candidate read alone would still admit a connection deactivated in the seconds
        // between that read and this statement.
        Assert.Contains("@RequireActiveConnections = 0 OR", admission);
        Assert.Contains("src.Id = SyncProjects.SourceTenantId AND src.IsActive = 1", admission);
        Assert.Contains("snk.Id = SyncProjects.SinkTenantId", admission);
        Assert.Contains("snk.IsActive = 1", admission);

        // EXISTS, so a project pointing at a DELETED connection refuses too rather than being
        // admitted against nothing. A NOT IN / <> form would have let the missing row through.
        Assert.Contains("EXISTS (SELECT 1 FROM Tenants src", admission);
        Assert.Contains("EXISTS (SELECT 1 FROM Tenants snk", admission);

        // The pre-existing enabled guard is untouched.
        Assert.Contains("(@RequireEnabled = 0 OR IsEnabled = 1)", admission);
    }

    [Fact]
    public void The_connection_gate_is_opt_in_so_manual_runs_are_unchanged()
    {
        var source = Read("src/Conduit.DataAccess/Repositories/SyncProjectRepository.cs");
        var signature = Between(source, "public async Task<bool> SetRunningAsync", "{");

        // Defaults false. Disabling a project means "not on a cadence", which manual callers are
        // deliberately allowed to override; deactivating a connection means "this system is off".
        // They are separate flags because they mean different things, and because silently gating
        // every caller would make Run Now fail with the claim's generic already-running message.
        Assert.Contains("bool requireActiveConnections = false", signature);
        Assert.Contains("bool requireEnabled = false", signature);
    }

    [Fact]
    public void The_scheduler_skips_an_inactive_connection_and_names_the_side()
    {
        var job = Read("src/Conduit.Sync/Orchestration/ScheduledSyncRunnerJob.cs");

        Assert.Contains("GetEnabledScheduledWithConnectionStateAsync()", job);
        Assert.Contains("if (!candidate.BothActive)", job);

        // Named and at Warning. A silent gap in the run history is what made this expensive to find;
        // an operator who switches a connection off needs something to search the log for.
        Assert.Contains("LogWarning(", job);
        Assert.Contains("{InactiveSide} connection is deactivated or missing", job);

        // And the claim re-checks it, so the skip above is not the only thing standing in the way.
        Assert.Contains("requireEnabled: true, requireActiveConnections: true", job);
    }

    [Fact]
    public void A_refused_claim_no_longer_blames_a_run_that_is_not_there()
    {
        var job = Read("src/Conduit.Sync/Orchestration/ScheduledSyncRunnerJob.cs");

        // The claim can now refuse for three reasons. Reporting only "already running" would send an
        // operator hunting a phantom run when a connection had just been switched off.
        Assert.Contains("or the project was disabled or one of its connections deactivated", job);
    }

    [Fact]
    public void A_deleted_connection_is_reported_inactive_rather_than_hiding_the_project()
    {
        var repo = Read("src/Conduit.DataAccess/Repositories/SyncProjectRepository.cs");
        var read = Between(repo, "GetEnabledScheduledWithConnectionStateAsync", "public async Task<List<SyncProject>> GetEnabledScheduledAsync");

        // LEFT JOIN, not INNER. An INNER JOIN would drop a project whose connection row was deleted,
        // and the scheduler would skip it silently with nothing logged — the same invisible failure
        // this change exists to remove.
        Assert.Contains("LEFT JOIN Tenants src", read);
        Assert.Contains("LEFT JOIN Tenants snk", read);
        Assert.DoesNotContain("INNER JOIN", read);

        // The candidate read keeps the enabled and cron predicates it always had.
        Assert.Contains("p.IsEnabled = 1", read);
        Assert.Contains("LEN(p.CronSchedule) > 0", read);
    }

    [Fact]
    public void The_inactive_side_label_distinguishes_source_sink_and_both()
    {
        var model = Read("src/Conduit.Core/SyncModels/SyncProject.cs");
        var state = Between(model, "public sealed record ScheduledProjectConnectionState", "}");

        Assert.Contains("SourceActive && SinkActive", state);
        Assert.Contains("\"source and sink\"", state);
        Assert.Contains("\"source\"", state);
        Assert.Contains("\"sink\"", state);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(RepoFile(relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Start marker not found: {start}");
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"End marker not found after start: {end}");
        return source[from..to];
    }
}
