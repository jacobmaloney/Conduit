using Conduit.Connectors.IdentityCenter;

namespace Conduit.Web.Services;

/// <summary>
/// Claims and executes the sync jobs IdentityCenter pinned to THIS installation (SYNC-SERVICE-06).
///
/// IdentityCenter's SYNC-SERVICE-05 pins a sync project to one execution server: submission stamps
/// <c>JobQueue.TargetServerId</c> from the project and one claim predicate means only that server
/// can take the job. This loop is the Conduit half — it asks for its own work and nothing else:
///
///   per endpoint → POST /api/jobs/claim { agentId = this installation's enrolled id,
///                                         supportedJobTypes = ["SyncProject"], maxJobs = 1 }
///   → IcSyncJobPump resolves the IdentityCenter project through the local binding and either runs
///     the one Conduit project bound to it or completes the job with a named refusal.
///
/// Identity: the agent id is NEVER configured or guessed here. IdentityCenter assigns it and echoes
/// it on the heartbeat, which <see cref="IcAgentCommandPollerService"/> records; until that first
/// heartbeat lands this loop has no identity to claim with and says so instead of claiming.
///
/// Concurrency: this slice runs at most ONE job at a time for the whole installation. Endpoints are
/// visited sequentially and each job is awaited, so a second job cannot start while one is running —
/// IdentityCenter's per-agent <c>MaxConcurrentJobs</c> is the server's view of the same limit and is
/// not honoured beyond 1 here.
///
/// Nothing in here may crash the host: a tick that throws is logged and retried.
/// </summary>
public sealed class IcSyncJobExecutorService : BackgroundService
{
    private readonly IcEndpointDirectory _endpoints;
    private readonly IcAgentStatusService _status;
    private readonly IcSyncJobPump _pump;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<IcSyncJobExecutorService> _logger;

    /// <summary>Endpoints already told "no enrolled agent id yet" — logged once each.</summary>
    private readonly HashSet<string> _awaitingIdentityLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Endpoints whose enrolled agent id differs from this instance's — logged once each.</summary>
    private readonly HashSet<string> _identityDriftLogged = new(StringComparer.OrdinalIgnoreCase);

    private readonly Random _jitter = new();

    public IcSyncJobExecutorService(
        IcEndpointDirectory endpoints,
        IcAgentStatusService status,
        IcSyncJobPump pump,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<IcSyncJobExecutorService> logger)
    {
        _endpoints = endpoints;
        _status = status;
        _pump = pump;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("IcSyncJobs:Enabled", true))
        {
            _logger.LogInformation("IdentityCenter sync job executor disabled via IcSyncJobs:Enabled=false.");
            return;
        }

        var busySeconds = Math.Clamp(_config.GetValue("IcSyncJobs:BusyIntervalSeconds", 3), 1, 60);
        var idleSeconds = Math.Clamp(_config.GetValue("IcSyncJobs:IdleIntervalSeconds", 20), 5, 3600);
        _logger.LogInformation("IdentityCenter sync job executor started (busy {Busy}s, idle {Idle}s, one job at a time).",
            busySeconds, idleSeconds);

        // Let the app finish booting, and let the command poller's first heartbeat land so
        // the enrolled agent id is known before the first claim.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var foundWork = false;
            try
            {
                foundWork = await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never crash the host — but never quietly, either: a tick that escapes here may
                // have left a claimed job unreported, which is exactly what must not go unnoticed.
                _logger.LogError(ex, "IdentityCenter sync job tick failed; retrying next interval.");
            }

            try { await Task.Delay(NextDelay(foundWork ? busySeconds : idleSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Returns true when at least one endpoint had work this tick, so the next tick comes sooner.</summary>
    private async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        var foundWork = false;
        foreach (var endpoint in await _endpoints.DiscoverAsync())
        {
            ct.ThrowIfCancellationRequested();

            var agentId = _status.TryGetAgentId(endpoint.BaseUrl);
            if (agentId is null || agentId == Guid.Empty)
            {
                _status.Update(endpoint.BaseUrl, s =>
                {
                    s.ExecutorEnabled = true;
                    s.LastClaimOutcome = "No enrolled agent id yet — waiting for the first IdentityCenter heartbeat.";
                });
                if (_awaitingIdentityLogged.Add(endpoint.BaseUrl))
                    _logger.LogInformation(
                        "IdentityCenter sync job executor at {BaseUrl}: no enrolled agent id yet (the heartbeat assigns it); not claiming.",
                        endpoint.BaseUrl);
                continue;
            }
            _awaitingIdentityLogged.Remove(endpoint.BaseUrl);

            // The key drives which agent IdentityCenter thinks this is, and jobs are pinned to that
            // agent. When it is not this installation's provenance id, the work claimed here was
            // pinned to a different identity than the one this installation stamps on what it writes.
            // The command poller warns on the same drift; this leg records it too, because the whole
            // point of the pin is that a job runs on the server it was pinned to.
            var drift = agentId.Value != ConduitInstanceIdentity.InstanceId;
            _status.Update(endpoint.BaseUrl, s =>
            {
                s.ExecutorEnabled = true;
                s.AgentIdentityDrift = drift;
            });
            if (drift && _identityDriftLogged.Add(endpoint.BaseUrl))
                _logger.LogWarning(
                    "IdentityCenter sync job executor at {BaseUrl} is enrolled as agent {EnrolledId}, which differs from this instance's provenance id {InstanceId}. Jobs claimed here were pinned to {EnrolledId}. Re-enroll in IdentityCenter using this instance's Instance ID if that is not intended.",
                    endpoint.BaseUrl, agentId.Value, ConduitInstanceIdentity.InstanceId);
            else if (!drift)
                _identityDriftLogged.Remove(endpoint.BaseUrl);

            // One job at a time for the whole installation: this await is the single-flight.
            var channel = new IcSyncJobChannel(CreateClient(endpoint.ChannelKey));
            var result = await _pump.RunOnceAsync(channel, endpoint.BaseUrl, agentId.Value, ct);
            if (result is IcSyncJobTickResult.Executed or IcSyncJobTickResult.Refused)
                foundWork = true;
        }
        return foundWork;
    }

    private TimeSpan NextDelay(int seconds)
    {
        // ±20% jitter so several installations against one IdentityCenter do not claim in lockstep.
        var span = seconds * 1000.0;
        return TimeSpan.FromMilliseconds(span * (0.8 + _jitter.NextDouble() * 0.4));
    }

    private HttpClient CreateClient(string apiKey)
    {
        var client = _httpFactory.CreateClient("IcSyncJobExecutor");
        // A sync run outlives any per-request timeout; the orchestrator owns run duration.
        // These are short control-plane calls only.
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Remove("X-API-Key");
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        return client;
    }
}
