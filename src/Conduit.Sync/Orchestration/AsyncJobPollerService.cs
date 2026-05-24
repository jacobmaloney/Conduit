using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Conduit.Core.SyncModels;
using Conduit.DataAccess.Repositories;
using Conduit.Scheduling;
using Conduit.Sync.Connectors;

namespace Conduit.Sync.Orchestration;

/// <summary>
/// Phase 4 background poller. Every 30 seconds, walks SyncRunAsyncJobs rows
/// in 'Pending' state, groups by SystemType, asks each connector adapter's
/// IConnectorAsyncJobResolver to advance them. Terminal outcomes flip the row
/// to Succeeded / Failed; intermediate "still pending" calls just bump
/// LastPolledAt + PollAttempts so an operator can see the row is being worked.
///
/// Per-tick row cap (200) keeps the worst-case cycle bounded. Single-process
/// today; the State+SubmittedAt index supports cheap fan-out later.
/// </summary>
public sealed class AsyncJobPollerService : IScheduledJob
{
    public string JobName => "AsyncJobPoller";
    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    private const int MaxRowsPerTick = 200;

    private readonly SyncRunAsyncJobRepository _repo;
    private readonly SyncRunRepository _runRepo;
    private readonly ConnectorRegistry _connectors;
    private readonly ILogger<AsyncJobPollerService> _logger;

    public AsyncJobPollerService(
        SyncRunAsyncJobRepository repo,
        SyncRunRepository runRepo,
        ConnectorRegistry connectors,
        ILogger<AsyncJobPollerService> logger)
    {
        _repo = repo;
        _runRepo = runRepo;
        _connectors = connectors;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        List<SyncRunAsyncJob> pending;
        try
        {
            pending = await _repo.GetPendingAsync(MaxRowsPerTick);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AsyncJobPoller could not load pending rows.");
            return;
        }

        if (pending.Count == 0) return;

        // Resolver cache keyed by (SystemType, TenantId) — different tenants
        // need different credentials but adapters are stateless. We rebuild
        // resolvers per tick (cheap; SDK clients are recreated by adapters).
        var resolverCache = new Dictionary<(string, Guid), IConnectorAsyncJobResolver?>();

        foreach (var row in pending)
        {
            if (cancellationToken.IsCancellationRequested) return;

            IConnectorAsyncJobResolver? resolver;
            var key = (row.SystemType, row.TenantId);
            if (!resolverCache.TryGetValue(key, out resolver))
            {
                try
                {
                    var adapter = _connectors.Get(row.SystemType);
                    resolver = adapter?.CreateAsyncJobResolver(row.TenantId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AsyncJobPoller could not build resolver for {SystemType}/{TenantId}", row.SystemType, row.TenantId);
                    resolver = null;
                }
                resolverCache[key] = resolver;
            }

            if (resolver is null)
            {
                // Adapter doesn't expose a resolver — the row will sit Pending
                // forever unless an operator clears it. Log once per tick per
                // (system,tenant) at warning level.
                _logger.LogWarning("AsyncJobPoller: no resolver for SystemType={SystemType} tenant={TenantId}; row {Id} stays Pending.", row.SystemType, row.TenantId, row.Id);
                continue;
            }
            if (!resolver.CanResolve(row.JobType))
            {
                _logger.LogWarning("AsyncJobPoller: resolver for {SystemType} can't handle JobType={JobType}; row {Id} stays Pending.", row.SystemType, row.JobType, row.Id);
                continue;
            }

            AsyncJobStatus status;
            try
            {
                status = await resolver.PollAsync(row.JobId, row.JobType, row.PayloadJson, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Adapter blew up — bump poll attempts but leave Pending so we re-try.
                try { await _repo.MarkPolledAsync(row.Id); } catch { /* swallow */ }
                _logger.LogWarning(ex, "AsyncJobPoller: resolver threw for JobId={JobId} ({JobType}); will retry.", row.JobId, row.JobType);
                continue;
            }

            switch (status.State)
            {
                case AsyncJobState.Succeeded:
                    await _repo.MarkSucceededAsync(row.Id, status.ResultJson);
                    await SafeLog(row.SyncRunId, "Info", $"Async job succeeded: {row.JobType} ({row.JobId}) for {row.ObjectExternalId}.");
                    break;
                case AsyncJobState.Failed:
                    var msg = status.ErrorMessage ?? "(no message)";
                    await _repo.MarkFailedAsync(row.Id, msg);
                    await SafeLog(row.SyncRunId, "Error", $"Async job FAILED: {row.JobType} ({row.JobId}) for {row.ObjectExternalId}: {msg}");
                    break;
                case AsyncJobState.Pending:
                default:
                    await _repo.MarkPolledAsync(row.Id);
                    break;
            }
        }
    }

    private async Task SafeLog(Guid runId, string level, string message)
    {
        try { await _runRepo.AppendLogAsync(runId, level, message); }
        catch (Exception ex) { _logger.LogWarning(ex, "AsyncJobPoller failed to append log row for run {RunId}.", runId); }
    }
}
