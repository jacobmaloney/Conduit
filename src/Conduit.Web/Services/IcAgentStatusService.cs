namespace Conduit.Web.Services;

/// <summary>
/// In-memory snapshot of this Conduit's enrollment against each configured
/// IdentityCenter endpoint, written by <see cref="IcAgentCommandPollerService"/>
/// and read by the Configuration page. The enrolled identity (agentId, name,
/// location) is SERVER-assigned — it comes back from IC's heartbeat response and
/// is display-only here; the per-agent API key is the actual identity.
/// </summary>
public sealed class IcAgentStatusService
{
    public sealed class EndpointStatus
    {
        public string BaseUrl { get; set; } = "";
        public bool KeyConfigured { get; set; }

        /// <summary>"Claim" (per-agent key, V140 channel), "Legacy" (shared key, untargeted GET/ack), or "Unknown".</summary>
        public string Mode { get; set; } = "Unknown";

        /// <summary>Which credential field drives claim + heartbeat: "AgentApiKey" (dedicated per-agent key) or "ApiKey" (shared key, legacy mode).</summary>
        public string? KeySource { get; set; }

        public DateTime? LastHeartbeatUtc { get; set; }
        public bool LastHeartbeatOk { get; set; }
        public string? LastHeartbeatError { get; set; }

        public Guid? AgentId { get; set; }
        public string? AgentName { get; set; }
        public string? AgentLocation { get; set; }

        // ── Job-queue executor (SYNC-SERVICE-06) ────────────────────────────
        // Written by IcSyncJobExecutorService/IcSyncJobPump. The command channel above
        // and this one are separate: the command channel can be healthy while the job
        // queue refuses the same key, and that must be visible rather than look idle.

        /// <summary>True once the executor loop is running for this endpoint (IcSyncJobs:Enabled).</summary>
        public bool ExecutorEnabled { get; set; }

        public DateTime? LastClaimUtc { get; set; }

        /// <summary>Outcome of the last POST /api/jobs/claim in this endpoint's own words — never a blank "idle".</summary>
        public string? LastClaimOutcome { get; set; }

        public DateTime? LastJobUtc { get; set; }

        /// <summary>Outcome reported to IdentityCenter for the last job: executed, or the named refusal.</summary>
        public string? LastJobOutcome { get; set; }

        /// <summary>The agent row's declared SupportedJobTypes as IdentityCenter last reported them.</summary>
        public string? SupportedJobTypes { get; set; }

        /// <summary>
        /// Null until the agent row has been read. False means IdentityCenter will never
        /// hand this installation a SyncProject job — "no work, ever", not "no work today".
        /// </summary>
        public bool? ExecutesSyncProjects { get; set; }

        /// <summary>
        /// True when the agent id IdentityCenter enrolled this key as differs from this
        /// installation's own provenance id. The jobs it then claims were pinned to that other
        /// identity, and its writes stamp a different SourceJobServerId.
        /// </summary>
        public bool AgentIdentityDrift { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, EndpointStatus> _endpoints = new(StringComparer.OrdinalIgnoreCase);

    public List<EndpointStatus> Snapshot()
    {
        lock (_gate)
        {
            return _endpoints.Values.Select(Clone).OrderBy(e => e.BaseUrl, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>
    /// The enrolled agent id IdentityCenter assigned this installation for an endpoint, as
    /// learned from the heartbeat response. Null until the first successful heartbeat — the
    /// job executor has no identity to claim with until then, and must say so rather than guess.
    /// </summary>
    public Guid? TryGetAgentId(string baseUrl)
    {
        lock (_gate)
        {
            return _endpoints.TryGetValue(baseUrl, out var status) ? status.AgentId : null;
        }
    }

    public void Update(string baseUrl, Action<EndpointStatus> mutate)
    {
        lock (_gate)
        {
            if (!_endpoints.TryGetValue(baseUrl, out var status))
            {
                status = new EndpointStatus { BaseUrl = baseUrl };
                _endpoints[baseUrl] = status;
            }
            mutate(status);
        }
    }

    private static EndpointStatus Clone(EndpointStatus s) => new()
    {
        BaseUrl = s.BaseUrl,
        KeyConfigured = s.KeyConfigured,
        Mode = s.Mode,
        KeySource = s.KeySource,
        LastHeartbeatUtc = s.LastHeartbeatUtc,
        LastHeartbeatOk = s.LastHeartbeatOk,
        LastHeartbeatError = s.LastHeartbeatError,
        AgentId = s.AgentId,
        AgentName = s.AgentName,
        AgentLocation = s.AgentLocation,
        ExecutorEnabled = s.ExecutorEnabled,
        LastClaimUtc = s.LastClaimUtc,
        LastClaimOutcome = s.LastClaimOutcome,
        LastJobUtc = s.LastJobUtc,
        LastJobOutcome = s.LastJobOutcome,
        SupportedJobTypes = s.SupportedJobTypes,
        ExecutesSyncProjects = s.ExecutesSyncProjects,
        AgentIdentityDrift = s.AgentIdentityDrift
    };
}
