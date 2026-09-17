using Conduit.Sync.Connectors;
using Conduit.Sync.Orchestration;
using Xunit;

namespace Conduit.Connectors.IdentityCenter.Tests;

public class SyncRunIntegrityTests
{
    [Theory]
    [InlineData(false, "Failed")]
    [InlineData(true, "PartialSuccess")]
    public void Required_step_exception_is_counted_and_cannot_finish_green(bool priorSuccess, string expected)
    {
        var progress = new SyncRunProgress();
        if (priorSuccess) progress.Add(new(3, 0, 3, 0, 0, 0), true, true, null);
        progress.Add(SyncStepDelta.Exception, false, false, "Required lookup threw.");
        var result = progress.Complete();
        Assert.Equal(expected, result.Status);
        Assert.Equal("Required lookup threw.", result.ErrorMessage);
        Assert.Equal(1, progress.Failed);
        Assert.Equal(1, progress.FailedSteps);
    }

    [Theory]
    [InlineData(false, "Failed")]
    [InlineData(true, "Succeeded")]
    public void Empty_delta_is_success_but_empty_full_read_retains_existing_failure_policy(bool incremental, string expected)
    {
        var progress = new SyncRunProgress();
        progress.Add(default, true, incremental, null);
        Assert.Equal(expected, progress.Complete().Status);
    }

    [Theory]
    [InlineData(false, 0, 0)] // capped/incomplete delta
    [InlineData(true, 1, 0)]  // failed tombstone or membership
    [InlineData(true, 0, 1)]  // submitted async effect has not completed
    public async Task Unfinished_mapping_keeps_all_prior_checkpoints(bool complete, int failed, int pending)
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var saved = new Dictionary<Guid, string> { [first] = "old-1", [second] = "old-2" };
        var checkpoints = new SyncCheckpointBuffer();
        Assert.True(checkpoints.Offer(first, new() { Token = "new-1" }, true, 0));
        Assert.False(checkpoints.Offer(second, new() { Token = "new-2" }, complete, failed, pending));
        await checkpoints.CommitAsync(true, Persist, default);
        Assert.Equal("old-1", saved[first]); Assert.Equal("old-2", saved[second]);
        Task Persist(Guid id, SyncCursor cursor) { saved[id] = cursor.Token; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Dependent_failure_keeps_mapping_cursor_and_successful_replay_commits()
    {
        var step = Guid.NewGuid(); var saved = "prior"; var writes = 0;
        Task Persist(Guid _, SyncCursor cursor) { saved = cursor.Token; writes++; return Task.CompletedTask; }
        var firstAttempt = new SyncCheckpointBuffer();
        Assert.True(firstAttempt.Offer(step, new() { Token = "after-read" }, true, 0));
        Assert.Equal("prior", saved); // completing Mapping alone never persists
        var progress = new SyncRunProgress();
        progress.Add(new(1, 0, 1, 0, 0, 0), true, true, null);
        progress.Add(SyncStepDelta.Exception, false, false, "Dependent creation failed.");
        await firstAttempt.CommitAsync(progress.Complete().Status == "Succeeded", Persist, default);
        Assert.Equal("prior", saved); Assert.Equal(0, writes);

        // A new attempt begins from the still-durable prior cursor.
        var replay = new SyncCheckpointBuffer();
        Assert.True(replay.Offer(step, new() { Token = "after-read" }, true, 0));
        await replay.CommitAsync(true, Persist, default);
        Assert.Equal("after-read", saved); Assert.Equal(1, writes);
    }

    [Fact]
    public async Task Cancellation_before_commit_leaves_the_durable_cursor_unchanged()
    {
        var checkpoints = new SyncCheckpointBuffer(); var writes = 0;
        checkpoints.Offer(Guid.NewGuid(), new() { Token = "new" }, true, 0);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checkpoints.CommitAsync(true,
            (_, _) => { writes++; return Task.CompletedTask; }, cancelled.Token));
        Assert.Equal(0, writes);
    }
}
