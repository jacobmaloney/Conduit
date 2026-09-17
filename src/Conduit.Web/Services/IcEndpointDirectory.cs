using System.Text.Json;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Security;

namespace Conduit.Web.Services;

/// <summary>
/// One configured IdentityCenter endpoint, as discovered from a connected system's
/// stored credential blob.
///
/// Key selection is a product rule, not a caller's choice: the OPTIONAL per-agent
/// <see cref="AgentApiKey"/> drives every per-agent channel (commands claim, heartbeat,
/// and the job queue); the shared <see cref="ApiKey"/> is the fallback and is ALWAYS
/// what the sync source/sink and the legacy pending/ack path use, because IC's
/// TenantDataPolicy denies per-agent keys on the data endpoints.
/// </summary>
public sealed record IcEndpoint(string BaseUrl, string ApiKey, string? AgentApiKey)
{
    /// <summary>The key that drives the per-agent channels.</summary>
    public string ChannelKey => AgentApiKey ?? ApiKey;

    /// <summary>Which credential field <see cref="ChannelKey"/> came from — display/diagnostics only.</summary>
    public string KeySource => AgentApiKey is not null ? "AgentApiKey" : "ApiKey";

    /// <summary>
    /// A record's generated ToString prints every member, so one careless "{Endpoint}" in a log
    /// template would print the API keys. This one prints the URL and which key drives the channel.
    /// </summary>
    public override string ToString() => $"{BaseUrl} ({KeySource})";
}

/// <summary>
/// The one reader of this installation's IdentityCenter endpoints: which connected
/// systems point at an IC, which key drives their per-agent channels, and how two
/// connections that name the same BaseUrl collapse into one endpoint.
///
/// Extracted from <see cref="IcAgentCommandPollerService"/> (SYNC-SERVICE-06) so the
/// command channel and <see cref="IcSyncJobExecutorService"/> discover endpoints through
/// one implementation instead of two copies that can drift apart. Owns no scope of its
/// own — it creates one per call, so it is safe as a singleton beside the hosted services.
///
/// The enrolled AGENT IDENTITY is deliberately NOT discovered here: it is server-assigned
/// and arrives on the heartbeat response, so <see cref="IcAgentStatusService.TryGetAgentId"/>
/// is the one place both channels read it from.
/// </summary>
public sealed class IcEndpointDirectory
{
    /// <summary>Credential name under which an IdentityCenter connection stores its blob.</summary>
    public const string CredentialName = "identitycenter";

    /// <summary>Tenant.SystemType of an IdentityCenter connected system.</summary>
    public const string IdentityCenterSystemType = "IdentityCenter";

    private readonly IServiceScopeFactory _scopeFactory;

    public IcEndpointDirectory(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    /// <summary>
    /// Every IdentityCenter endpoint this installation can reach, deduplicated by BaseUrl.
    /// An unreadable or malformed credential blob is skipped, never thrown out of.
    /// </summary>
    public async Task<List<IcEndpoint>> DiscoverAsync()
    {
        // Fresh scope per call — the tenant repository and the credential protector are scoped.
        using var scope = _scopeFactory.CreateScope();
        var tenants = scope.ServiceProvider.GetRequiredService<TenantRepository>();
        var protector = scope.ServiceProvider.GetRequiredService<CredentialProtector>();

        var endpoints = new List<IcEndpoint>();
        foreach (var tenant in await tenants.GetAllAsync())
        {
            if (!string.Equals(tenant.SystemType, IdentityCenterSystemType, StringComparison.OrdinalIgnoreCase))
                continue;

            string? raw = null;
            try { raw = await protector.RetrieveAsync(tenant.Id, CredentialName); }
            catch { /* unreadable credential — skip this connection */ }
            if (string.IsNullOrEmpty(raw)) continue;

            var candidate = Parse(raw);
            if (candidate is not null) Merge(endpoints, candidate);
        }
        return endpoints;
    }

    /// <summary>
    /// Reads BaseUrl / ApiKey / optional AgentApiKey out of a credential blob and normalizes
    /// the BaseUrl. Returns null for a malformed blob or one missing the required fields.
    /// </summary>
    public static IcEndpoint? Parse(string credentialJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(credentialJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var baseUrl = doc.RootElement.TryGetProperty("BaseUrl", out var uEl) ? uEl.GetString() : null;
            var apiKey = doc.RootElement.TryGetProperty("ApiKey", out var kEl) ? kEl.GetString() : null;
            var agentApiKey = doc.RootElement.TryGetProperty("AgentApiKey", out var aEl) ? aEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(agentApiKey)) agentApiKey = null;
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey)) return null;
            return new IcEndpoint(baseUrl!.TrimEnd('/'), apiKey!, agentApiKey);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Dedup by BaseUrl, deterministically preferring the connection that carries an
    /// AgentApiKey: enumeration order of the connected systems must not decide which
    /// key drives the per-agent channels.
    /// </summary>
    public static void Merge(List<IcEndpoint> endpoints, IcEndpoint candidate)
    {
        var existing = endpoints.FindIndex(e => string.Equals(e.BaseUrl, candidate.BaseUrl, StringComparison.OrdinalIgnoreCase));
        if (existing < 0)
            endpoints.Add(candidate);
        else if (endpoints[existing].AgentApiKey is null && candidate.AgentApiKey is not null)
            endpoints[existing] = candidate;
    }
}
