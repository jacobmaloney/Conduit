using Conduit.Sync.Connectors;

namespace Conduit.Sync.Orchestration;

/// <summary>
/// Run-local candidates, committed only after all dependent steps have succeeded.
/// Losing this buffer on a crash deliberately replays from the prior durable cursor.
/// </summary>
public sealed class SyncCheckpointBuffer
{
    private readonly Dictionary<Guid, SyncCursor> _pending = new();
    private bool _blocked;

    public static bool CanAdvance(SyncCursor? cursor, bool readWasComplete, int failedEffects, int pendingEffects = 0) =>
        cursor is not null && readWasComplete && failedEffects == 0 && pendingEffects == 0;

    public bool Offer(Guid stepId, SyncCursor? cursor, bool readWasComplete, int failedEffects, int pendingEffects = 0)
    {
        if (!CanAdvance(cursor, readWasComplete, failedEffects, pendingEffects))
        {
            _blocked |= cursor is not null || failedEffects > 0 || pendingEffects > 0;
            return false;
        }
        _pending[stepId] = cursor!;
        return true;
    }

    public async Task CommitAsync(bool runSucceeded, Func<Guid, SyncCursor, Task> persist, CancellationToken ct)
    {
        if (!runSucceeded || _blocked) return;
        ct.ThrowIfCancellationRequested();
        foreach (var (stepId, cursor) in _pending)
        {
            ct.ThrowIfCancellationRequested();
            await persist(stepId, cursor);
        }
        _pending.Clear();
    }
}
