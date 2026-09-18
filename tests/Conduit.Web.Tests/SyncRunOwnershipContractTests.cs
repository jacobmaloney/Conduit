using System.Runtime.CompilerServices;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>Source/SQL contract pins. Execution and SQL Server concurrency are separate acceptance work.</summary>
public class SyncRunOwnershipContractTests
{
    [Fact]
    public void Startup_observation_cannot_rewrite_running_owners()
    {
        var source = Read("src/Conduit.DataAccess/DatabaseInitializer.cs");
        var observe = Between(source, "private async Task ObserveRunningOwnershipAsync()", "private async Task EnsureDatabaseExistsAsync()");
        Assert.Contains("RunOwnershipUnverified", observe);
        Assert.Contains("SELECT @Running", observe);
        Assert.DoesNotContain("UPDATE ", observe);
        Assert.DoesNotContain("DELETE ", observe);
        Assert.DoesNotContain("SweepInterruptedRunsAsync", source);
    }

    [Fact]
    public void Admission_cleanup_completion_and_recovery_keep_exact_owner_predicates()
    {
        var source = Read("src/Conduit.DataAccess/Repositories/SyncProjectRepository.cs");
        var admission = Between(source, "public async Task<bool> SetRunningAsync", "public Task ClearRunningAsync");
        Assert.Contains("SyncRunOwnership.RequireOwner(runId)", admission);
        Assert.Contains("LastRunId = @RunId", admission);
        Assert.Contains("WHERE Id = @ProjectId AND IsRunning = 0", admission);
        Assert.Contains("(@RequireEnabled = 0 OR IsEnabled = 1)", admission);
        var clear = Between(source, "public Task ClearRunningAsync", "public Task FinishRunAsync");
        var finish = Between(source, "public Task FinishRunAsync", "public async Task RecoverCompletedRunAsync");
        foreach (var write in new[] { clear, finish })
        {
            Assert.Contains("WHERE Id = @ProjectId AND IsRunning = 1 AND LastRunId = @OwnerRunId", write);
            Assert.DoesNotContain("LastRunId = NULL", write);
        }
        var recover = Between(source, "public async Task RecoverCompletedRunAsync", "public Task<SyncProjectScope?> GetScopeAsync");
        Assert.Contains("SyncRunOwnership.RequireRecoverable", recover);
        Assert.Contains("JOIN SyncRuns r WITH (UPDLOCK,HOLDLOCK)", recover);
        Assert.Contains("r.Id = p.LastRunId AND r.SyncProjectId = p.Id", recover);
        Assert.Contains("p.IsRunning = 1 AND p.LastRunId = @RunId", recover);
        Assert.Contains("r.Status IN @TerminalStatuses", recover);
        Assert.DoesNotContain("UPDATE SyncRuns", recover);
    }

    [Fact]
    public void Full_reset_and_checkpoint_require_owner_at_the_write_boundary()
    {
        var reset = Between(Read("src/Conduit.DataAccess/Repositories/SyncProjectRepository.cs"),
            "public async Task ResetForFullSyncAsync", "public async Task<Guid> CloneAsync");
        Assert.Contains("SyncProjects WITH (UPDLOCK,HOLDLOCK)", reset);
        Assert.Contains("IsRunning = 1 AND LastRunId = @OwnerRunId", reset);
        Assert.True(reset.IndexOf("if (owns != 1)", StringComparison.Ordinal) < reset.IndexOf("UPDATE s", StringComparison.Ordinal));
        var checkpoint = Between(Read("src/Conduit.DataAccess/Repositories/WorkflowRepository.cs"),
            "public async Task SetStepCursorAsync", "public async Task ReorderStepsAsync");
        Assert.Contains("p.IsRunning = 1 AND p.LastRunId = @OwnerRunId", checkpoint);
        Assert.Contains("s.Id = @StepId AND p.Id = @ProjectId", checkpoint);
        Assert.Contains("RunOwnershipLost", checkpoint);
        var legacy = Between(Read("src/Conduit.DataAccess/Repositories/SyncRunRepository.cs"),
            "public async Task SetCursorAsync", "public Task<SyncRun?> GetByIdAsync");
        Assert.Contains("r.Id = @RunId AND p.Id = @ProjectId", legacy);
        Assert.Contains("p.IsRunning = 1 AND p.LastRunId = @RunId", legacy);
        Assert.Contains("RunOwnershipLost", legacy);
    }

    [Fact]
    public void Preclaimed_execution_checks_persisted_owner_before_run_creation_or_connector_execution()
    {
        var execute = Between(Read("src/Conduit.Sync/Orchestration/SyncProjectOrchestrator.cs"),
            "public async Task<Guid> ExecuteAsync", "private async Task<Guid> RunCoreAsync");
        var guard = execute.IndexOf("SyncRunOwnership.RequireCurrentOwner(project, runId)", StringComparison.Ordinal);
        Assert.True(guard > execute.IndexOf("_projectRepo.GetByIdAsync", StringComparison.Ordinal));
        Assert.True(guard < execute.IndexOf("_runRepo.CreateAsync", StringComparison.Ordinal));
        Assert.True(guard < execute.IndexOf("RunCoreAsync(project, run, runToken)", StringComparison.Ordinal));
        Assert.Contains("Id = runId", execute);
        Assert.DoesNotContain("StampLastRunIdAsync", execute);
        Assert.Contains("_cancellation.Unregister(project.Id, run.Id)", execute);
    }

    [Fact]
    public void History_cancellation_uses_run_identity_and_recovery_does_not_rewrite_history()
    {
        var history = Read("src/Conduit.Web/Pages/Sync/ProcessingCenter.razor");
        Assert.Contains("IsRunningHere(r.SyncProjectId, r.Id)", history);
        Assert.Contains("RequestCancel(run.SyncProjectId, run.Id)", history);
        var page = Read("src/Conduit.Web/Pages/Sync/SyncProjects.razor");
        var recovery = Between(page, "private async Task ForceReleaseConfirmed()", "private void ShowCopyTemplateModal()");
        Assert.Contains("RecoverCompletedRunAsync(projectId, expectedOwner)", recovery);
        Assert.DoesNotContain("RunRepo.FinishAsync", recovery);
        Assert.Contains("selectedProject.LastRunId == ownerRunId", page);
    }

    [Fact]
    public void Automatic_admission_rechecks_enabled_while_manual_runs_may_still_run_a_disabled_project()
    {
        // Renamed from "...without_changing_manual_policy". Manual policy DID change, deliberately:
        // every surface now refuses a deactivated Connected System. What has NOT changed, and is
        // what this still guards, is that a manual caller may run a DISABLED project — a disabled
        // schedule means "not on a cadence", which an operator is allowed to override by hand.
        var automatic = new[]
        {
            "src/Conduit.Sync/Orchestration/ScheduledSyncRunnerJob.cs",
            "src/Conduit.Web/Services/SqlDiscoveryRunner.cs",
            "src/Conduit.Web/Services/IcSyncProjectJobRunner.cs",
        };
        foreach (var path in automatic)
            Assert.Contains("requireEnabled: true", Read(path));

        foreach (var path in new[] { "src/Conduit.Web/Controllers/ApiV1SyncRunsController.cs",
            "src/Conduit.Web/Pages/Sync/SyncProjects.razor", "src/Conduit.Web/Pages/Sync/ScheduleManager.razor" })
        {
            var source = Read(path);
            // No requireEnabled on the manual surfaces: running a disabled project by hand is allowed.
            Assert.DoesNotContain("requireEnabled: true", source);
            // Owner-fenced dispatch and release are unchanged by the admission rework.
            Assert.Contains("claimedRunId: ownerRunId", source);
            Assert.Contains("ClearRunningAsync(projectId, ownerRunId)", source);
            Assert.DoesNotContain("SetRunningAsync(projectId, Guid.Empty)", source);
        }
    }

    private static string Between(string source, string start, string end)
    {
        var first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, start);
        var last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, end);
        return source[first..last];
    }

    private static string Read(string relative, [CallerFilePath] string file = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../..", relative)));
}
