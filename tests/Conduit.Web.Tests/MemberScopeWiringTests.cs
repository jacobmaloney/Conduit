using System.Runtime.CompilerServices;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// The orchestrator asks the store whether another scope in the project is narrowed, and only then tells the
/// sink that members outside the read scope are expected. The store counts a base DN, included paths or
/// blocked paths, on another step or on the project, never the running step's own scope.
/// </summary>
public class MemberScopeWiringTests
{
    [Fact]
    public void The_orchestrator_decides_from_the_other_scopes_and_logs_what_was_left_out()
    {
        var code = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Sync", "Orchestration", "SyncProjectOrchestrator.cs")));
        Assert.Contains("_workflowRepo.CountOtherNarrowedScopesAsync(project.Id, step.Id)", code);
        Assert.Contains("MemberScopeRule.MembersOutsideReadScopeExpected(otherNarrowedScopes)", code);
        Assert.Contains("MemberScopeRule.LeftOutLine(membersNotHeld)", code);
    }

    [Fact]
    public void The_store_counts_other_narrowed_scopes_only()
    {
        var code = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.DataAccess", "Repositories", "WorkflowRepository.cs")));
        var query = code.Substring(code.IndexOf("CountOtherNarrowedScopesAsync", StringComparison.Ordinal));
        query = query.Substring(0, query.IndexOf("new { P = syncProjectId", StringComparison.Ordinal));
        Assert.Contains("WorkflowStepId IS NULL OR WorkflowStepId <> @S", query);
        Assert.Contains("BaseDN", query);
        Assert.Contains("IncludedBaseDNs", query);
        Assert.Contains("ExcludedBaseDNs", query);
    }

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));
}
