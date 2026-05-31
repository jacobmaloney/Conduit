using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Conduit.Sync.Orchestration;

/// <summary>
/// In-process registry of cancellation sources for currently-executing sync
/// runs, keyed by SyncProject.Id. This is the ONE cancellation mechanism for
/// Run-Now / Stop Sync — it does NOT invent a parallel orchestration. The
/// <see cref="SyncProjectOrchestrator"/> registers a linked CTS at the start of
/// every run and disposes it in its finally block; the UI's "Stop Sync" button
/// signals that CTS via <see cref="RequestCancel"/>. The orchestrator's existing
/// <c>OperationCanceledException</c> handler then stamps the run "Cancelled" and
/// releases the IsRunning flag — same code path a host shutdown would take.
///
/// Registered as a singleton so the controller's fire-and-forget Run-Now task,
/// the scheduler, and the Blazor circuit all share the same view of what's
/// in-flight. Single-node only (matches the Processing Center decision):
/// cancellation is honored only for runs executing in THIS process. A run that
/// is in-flight on another node would not be registered here, so
/// <see cref="RequestCancel"/> returns false for it — the caller surfaces that
/// as "no cancellable run in this process" rather than a silent no-op.
/// </summary>
public sealed class SyncCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    /// <summary>
    /// Registers a run for the given project and returns a token that trips when
    /// EITHER the supplied <paramref name="external"/> token (host shutdown,
    /// scheduler stop) trips OR <see cref="RequestCancel"/> is called for this
    /// project. The caller MUST pair every Register with an <see cref="Unregister"/>
    /// (the orchestrator does this in its finally). If a stale entry exists for
    /// the project (a previous run that never unregistered), it is cancelled and
    /// replaced so the new run owns the slot cleanly.
    /// </summary>
    public CancellationToken Register(Guid projectId, CancellationToken external)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        var prior = _running.AddOrUpdate(projectId, cts, (_, old) =>
        {
            try { old.Cancel(); } catch { /* best effort */ }
            old.Dispose();
            return cts;
        });
        // AddOrUpdate returns the value now stored (== cts in both branches).
        return prior.Token;
    }

    /// <summary>
    /// Removes and disposes the registration for a project. Safe to call even if
    /// the run was never registered or was already cancelled. Only removes the
    /// entry when it is still the same CTS we hold, so a fast re-run that already
    /// re-registered the slot is not clobbered.
    /// </summary>
    public void Unregister(Guid projectId)
    {
        if (_running.TryRemove(projectId, out var cts))
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// Signals cancellation for the project's in-flight run. Returns <c>true</c>
    /// when a run was registered in this process and was signalled, <c>false</c>
    /// when nothing was in-flight here. Does not remove the entry — the
    /// orchestrator's finally owns the <see cref="Unregister"/> so the run's
    /// own cancellation handler observes the trip first.
    /// </summary>
    public bool RequestCancel(Guid projectId)
    {
        if (!_running.TryGetValue(projectId, out var cts)) return false;
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // Run finished and disposed between the lookup and the cancel.
            return false;
        }
    }

    /// <summary>True when a cancellable run for this project is registered in this process.</summary>
    public bool IsRunningHere(Guid projectId) => _running.ContainsKey(projectId);
}
