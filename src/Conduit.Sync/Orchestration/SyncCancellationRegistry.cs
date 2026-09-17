using System;
using System.Collections.Generic;
using System.Threading;
using Conduit.Core.SyncModels;

namespace Conduit.Sync.Orchestration;

/// <summary>
/// Process-local cancellation for an exact admitted run. A missing registration
/// does not prove that a persisted owner is dead; another process may own it.
/// </summary>
public sealed class SyncCancellationRegistry
{
    private sealed record Registration(Guid RunId, CancellationTokenSource Source);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Registration> _running = new();

    public CancellationToken Register(Guid projectId, Guid runId, CancellationToken external)
    {
        SyncRunOwnership.RequireOwner(runId);
        var source = CancellationTokenSource.CreateLinkedTokenSource(external);
        var token = source.Token;
        Registration? previous;
        lock (_gate)
        {
            _running.TryGetValue(projectId, out previous);
            _running[projectId] = new Registration(runId, source);
        }
        // A completed run may not yet have unregistered when its successor starts.
        if (previous is not null)
        {
            try { previous.Source.Cancel(); } catch { /* prior callbacks must not orphan the new registration */ }
            previous.Source.Dispose();
        }
        return token;
    }

    /// <summary>A late finalizer can remove only its own registration.</summary>
    public void Unregister(Guid projectId, Guid runId)
    {
        Registration? entry;
        lock (_gate)
        {
            if (!_running.TryGetValue(projectId, out entry) || entry.RunId != runId) return;
            _running.Remove(projectId);
        }
        entry.Source.Dispose();
    }

    /// <summary>Signals only the exact requested owner in this process.</summary>
    public bool RequestCancel(Guid projectId, Guid runId)
    {
        SyncRunOwnership.RequireOwner(runId);
        Registration? entry;
        lock (_gate)
        {
            if (!_running.TryGetValue(projectId, out entry) || entry.RunId != runId) return false;
        }
        try
        {
            entry.Source.Cancel();
            return true;
        }
        catch (ObjectDisposedException) { return false; }
    }

    public bool IsRunningHere(Guid projectId, Guid runId)
    {
        lock (_gate) return _running.TryGetValue(projectId, out var entry) && entry.RunId == runId;
    }

    public bool IsRunningHere(Guid projectId)
    {
        lock (_gate) return _running.ContainsKey(projectId);
    }
}
