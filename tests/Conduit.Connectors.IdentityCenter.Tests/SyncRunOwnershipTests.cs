using Conduit.Core.SyncModels;
using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Orchestration;
using Xunit;

namespace Conduit.Connectors.IdentityCenter.Tests;

/// <summary>Offline production-policy and local cancellation tests, not SQL Server locking tests.</summary>
public class SyncRunOwnershipTests
{
    [Fact]
    public void Wrong_preclaim_is_refused_by_execution_policy_without_changing_owner()
    {
        var actual = Guid.NewGuid();
        var project = new SyncProject { IsRunning = true, LastRunId = actual };
        var error = Assert.Throws<SyncRunOwnershipException>(() =>
            SyncRunOwnership.RequireCurrentOwner(project, Guid.NewGuid()));
        Assert.Equal("RunOwnershipLost", error.Code);
        Assert.True(project.IsRunning);
        Assert.Equal(actual, project.LastRunId);
        SyncRunOwnership.RequireCurrentOwner(project, actual);
        project.IsRunning = false;
        Assert.Equal("RunOwnershipLost", Assert.Throws<SyncRunOwnershipException>(() =>
            SyncRunOwnership.RequireCurrentOwner(project, actual)).Code);
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Pending")]
    [InlineData("")]
    public void Nonterminal_owner_is_not_recoverable_and_no_state_is_rewritten(string status)
    {
        var (project, run) = Owned(status);
        var error = Assert.Throws<SyncRunOwnershipException>(() =>
            SyncRunOwnership.RequireRecoverable(project, run, run.Id));
        Assert.Equal("RunOwnershipUnverified", error.Code);
        Assert.True(project.IsRunning);
        Assert.Equal(run.Id, project.LastRunId);
        Assert.Equal(status, run.Status);
        Assert.Null(run.CompletedAt);
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("PartialSuccess")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("Interrupted")]
    [InlineData("Skipped")]
    public void Only_exact_terminal_owner_passes_recovery_policy(string status)
    {
        var (project, run) = Owned(status);
        SyncRunOwnership.RequireRecoverable(project, run, run.Id);
        var persistedOwner = project.LastRunId;
        AssertRefused(null, run.Id);
        AssertRefused(run, Guid.NewGuid());
        AssertRefused(run, Guid.Empty);
        run.SyncProjectId = Guid.NewGuid();
        AssertRefused(run, run.Id);
        Assert.Equal(persistedOwner, project.LastRunId);
        Assert.True(project.IsRunning);
        Assert.Equal(status, run.Status);

        void AssertRefused(SyncRun? owner, Guid expected) => Assert.Equal("RunOwnershipUnverified",
            Assert.Throws<SyncRunOwnershipException>(() =>
                SyncRunOwnership.RequireRecoverable(project, owner, expected)).Code);
    }

    [Fact]
    public void A_late_unregister_and_stale_history_cancel_cannot_touch_successor_B()
    {
        var registry = new SyncCancellationRegistry();
        var project = Guid.NewGuid(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var tokenA = registry.Register(project, a, default);
        var tokenB = registry.Register(project, b, default);
        Assert.True(tokenA.IsCancellationRequested);
        registry.Unregister(project, a);
        Assert.True(registry.IsRunningHere(project, b));
        Assert.False(registry.IsRunningHere(project, a));
        Assert.False(registry.RequestCancel(project, a));
        Assert.False(tokenB.IsCancellationRequested);
        Assert.True(registry.RequestCancel(project, b));
        Assert.True(tokenB.IsCancellationRequested);
        registry.Unregister(project, b);
        Assert.False(registry.IsRunningHere(project));
        Assert.False(registry.RequestCancel(project, b));
    }

    [Fact]
    public void Cancellation_still_honors_external_token_and_rejects_unidentified_owner()
    {
        var registry = new SyncCancellationRegistry(); var project = Guid.NewGuid(); var run = Guid.NewGuid();
        using var external = new CancellationTokenSource();
        var token = registry.Register(project, run, external.Token);
        Assert.Equal("RunOwnerRequired", Assert.Throws<SyncRunOwnershipException>(() =>
            registry.RequestCancel(project, Guid.Empty)).Code);
        Assert.False(token.IsCancellationRequested);
        external.Cancel();
        Assert.True(token.IsCancellationRequested);
        registry.Unregister(project, run);
    }

    [Fact]
    public async Task Repositories_reject_empty_owner_before_attempting_database_access()
    {
        // Empty configuration deliberately cannot open a DB; named owner failures prove the guard runs first.
        var repo = new SyncProjectRepository(new DatabaseConfig());
        var workflow = new WorkflowRepository(new DatabaseConfig());
        var runs = new SyncRunRepository(new DatabaseConfig());
        var project = Guid.NewGuid();
        await Refuses(() => repo.SetRunningAsync(project, Guid.Empty));
        await Refuses(() => repo.ClearRunningAsync(project, Guid.Empty));
        await Refuses(() => repo.FinishRunAsync(project, Guid.Empty, "Failed"));
        await Refuses(() => repo.ResetForFullSyncAsync(project, Guid.NewGuid(), null, Guid.Empty));
        await Refuses(() => workflow.SetStepCursorAsync(Guid.NewGuid(), project, Guid.Empty, "new"));
        await Refuses(() => runs.SetCursorAsync(Guid.Empty, project, "new", true));
        var recovery = await Assert.ThrowsAsync<SyncRunOwnershipException>(() =>
            repo.RecoverCompletedRunAsync(project, Guid.Empty));
        Assert.Equal("RunOwnershipUnverified", recovery.Code);

        static async Task Refuses(Func<Task> action) => Assert.Equal("RunOwnerRequired",
            (await Assert.ThrowsAsync<SyncRunOwnershipException>(action)).Code);
    }

    private static (SyncProject, SyncRun) Owned(string status)
    {
        var project = new SyncProject { Id = Guid.NewGuid(), IsRunning = true };
        var run = new SyncRun { SyncProjectId = project.Id, Status = status };
        project.LastRunId = run.Id;
        return (project, run);
    }
}
