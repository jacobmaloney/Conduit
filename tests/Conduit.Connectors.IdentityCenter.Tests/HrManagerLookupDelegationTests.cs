using System.Runtime.CompilerServices;
using Xunit;

namespace Conduit.Connectors.IdentityCenter.Tests;

/// <summary>
/// HR-MANAGER-01 (the Granite walk, 2026-11-04: an HR file's 42 managers, Lookup "resolved=0"): for an IdentityCenter
/// Identities-table sink, a row that names its manager by employee ID (ManagerEmployeeId) is linked by IdentityCenter
/// after each bulk batch, so the Lookup step counts it as delegated and makes no per-object call for it. Rows with
/// another manager reference (email, UPN) still resolve per object.
/// </summary>
public class HrManagerLookupDelegationTests
{
    private static string Orchestrator([CallerFilePath] string thisFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..",
            "src", "Conduit.Sync", "Orchestration", "SyncProjectOrchestrator.cs"))).Replace("\r\n", "\n");

    [Fact]
    public void A_manager_employee_id_on_an_Identities_sink_is_left_to_IdentityCenter()
    {
        var source = Orchestrator();
        var lookup = source[source.IndexOf("private async Task<StepResult> ExecuteLookupStepAsync(", StringComparison.Ordinal)..];

        Assert.Contains("private const string IcManagerEmployeeIdKey = \"ManagerEmployeeId\";", source);
        Assert.Contains("&& string.Equals(ctx.Project.SinkTable, \"Identities\", StringComparison.OrdinalIgnoreCase);", lookup);

        // The delegation is decided before any reference is read, so no per-object call is made for that row.
        var delegated = lookup.IndexOf("delegatedToIc++;", StringComparison.Ordinal);
        var perObject = lookup.IndexOf("await ctx.Sink.AssignManagerAsync(obj.SourceId, reference!, ct)", StringComparison.Ordinal);
        Assert.True(delegated > 0 && perObject > delegated);
        Assert.Contains("named by manager employee ID, linked by IdentityCenter after each batch.", lookup);
    }
}
