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
        AgentLocation = s.AgentLocation
    };
}
