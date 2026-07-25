using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conduit.Connectors.ActiveDirectory;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;

namespace Conduit.Web.Services;

/// <summary>
/// Executes an IdentityCenter "BrowseContainers" agent command: a READ-ONLY, one-level enumeration of
/// OUs/containers under a requested base DN, so the IC account-definition editor can pick a target OU
/// from the live tree. It never writes.
///
/// SECURITY: the browse is SCOPED TO THE PERMITTED CREATION BASE DNs (same deny-all allow-list as the
/// create path) — an operator may only browse where they could actually create, which also avoids
/// exposing the whole directory tree. A requested base outside the allow-list is refused. The result is
/// size-bounded (the browser is one-level so depth is inherently 1; the count is capped). Never throws.
/// </summary>
public sealed class AdAgentBrowseExecutor
{
    private const int MaxPayloadBytes = 64 * 1024;
    /// <summary>Cap on the number of containers returned — a huge OU can't blow up the response.</summary>
    public const int MaxContainers = 500;
    private const string ExpectedOperation = "BrowseContainers";

    private readonly IEnumerable<IConnectorAdapter> _adapters;
    private readonly SinkConnectionCredentialMapRepository _credentialMap;
    private readonly TenantRepository _tenants;
    private readonly ICreationBaseDnPolicy _baseDnPolicy;
    private readonly ILogger<AdAgentBrowseExecutor> _logger;

    public AdAgentBrowseExecutor(
        IEnumerable<IConnectorAdapter> adapters,
        SinkConnectionCredentialMapRepository credentialMap,
        TenantRepository tenants,
        ICreationBaseDnPolicy baseDnPolicy,
        ILogger<AdAgentBrowseExecutor> logger)
    {
        _adapters = adapters;
        _credentialMap = credentialMap;
        _tenants = tenants;
        _baseDnPolicy = baseDnPolicy;
        _logger = logger;
    }

    public async Task<(bool Success, string Message, string? ResultJson)> ExecuteAsync(Guid commandId, string? payloadJson, CancellationToken ct)
    {
        // ── (1) Strict, size-bounded parse ───────────────────────────────────
        if (string.IsNullOrWhiteSpace(payloadJson))
            return Fail("BrowseContainers: empty payload.");
        if (Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadBytes)
            return Fail($"BrowseContainers: payload exceeds {MaxPayloadBytes / 1024} KB cap.");

        BrowseContainersPayload? p;
        try
        {
            p = JsonSerializer.Deserialize<BrowseContainersPayload>(payloadJson, StrictJson);
        }
        catch (JsonException)
        {
            return Fail("BrowseContainers: malformed payload JSON.");
        }
        if (p is null)
            return Fail("BrowseContainers: payload deserialized to null.");
        if (p.SchemaVersion != 1)
            return Fail($"BrowseContainers: unsupported schemaVersion {p.SchemaVersion}.");

        // ── (2) operation + required fields ───────────────────────────────────
        if (!string.Equals(p.Operation?.Trim(), ExpectedOperation, StringComparison.Ordinal))
            return Fail($"BrowseContainers: operation '{p.Operation}' is not allowed.");

        var sourceConnectionName = p.SourceConnectionName?.Trim();
        if (string.IsNullOrEmpty(sourceConnectionName))
            return Fail("BrowseContainers: sourceConnectionName is missing.");

        var baseDn = p.BaseDn?.Trim();
        if (string.IsNullOrEmpty(baseDn))
            return Fail("BrowseContainers: baseDn is required (browse from a permitted creation base DN).");

        // ── (3) Well-formedness + ★ scope to the permitted creation base DNs ──
        if (!BaseDnContainment.IsWellFormedDn(baseDn))
            return Fail("BrowseContainers: baseDn is not a well-formed distinguished name.");

        var permitted = await _baseDnPolicy.GetPermittedBaseDnsAsync(sourceConnectionName);
        if (!BaseDnContainment.IsContained(baseDn, permitted))
        {
            _logger.LogWarning("BrowseContainers {CommandId}: baseDn refused by the creation base-DN allow-list.", commandId);
            return Fail("BrowseContainers: baseDn is not within any permitted creation base DN for this connection. You can only browse where you could create.");
        }

        // ── (4)-(5) Resolve the browser + browse (READ-ONLY). Wrapped so any DB/LDAP error returns a
        //           clean failure — never throws to the caller. ──
        try
        {
            var resolvedTenantId = await _credentialMap.GetTenantIdByNameAsync(sourceConnectionName);
            if (resolvedTenantId is null || resolvedTenantId.Value == Guid.Empty)
                return Fail($"BrowseContainers: No Conduit credential mapping for source connection '{sourceConnectionName}'. Run a sync from this connection to register it.");
            var tenant = await _tenants.GetByIdAsync(resolvedTenantId.Value);
            if (tenant is null || !tenant.IsActive)
                return Fail($"BrowseContainers: No Conduit credential mapping for source connection '{sourceConnectionName}'. Run a sync from this connection to register it.");

            var adapter = _adapters.FirstOrDefault(a =>
                string.Equals(a.SystemType, "ActiveDirectory", StringComparison.OrdinalIgnoreCase));
            if (adapter is null)
                return Fail("BrowseContainers: Active Directory connector is not available on this agent.");
            var browser = adapter.CreateContainerBrowser(resolvedTenantId.Value);
            if (browser is null)
                return Fail("BrowseContainers: this connector does not support container browsing.");

            var result = await browser.BrowseContainersAsync(baseDn, ct);
            if (result.ErrorMessage is not null)
                return Fail("BrowseContainers: " + AdAgentCreateExecutor.SanitizeLdapError(result.ErrorMessage));

            var resultJson = BuildResultJson(result.ResolvedBaseDn ?? baseDn, result.Nodes);
            var count = System.Math.Min(result.Nodes.Count, MaxContainers);
            var truncated = result.Nodes.Count > MaxContainers;
            var message = truncated
                ? $"Browsed {count} of {result.Nodes.Count} containers under {baseDn} (capped)."
                : $"Browsed {count} container(s) under {baseDn}.";
            return (true, message, resultJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BrowseContainers {CommandId} threw.", commandId);
            return Fail("BrowseContainers: " + AdAgentCreateExecutor.SanitizeLdapError(ex.Message));
        }
    }

    /// <summary>Build the ResultJson container list, capped at <see cref="MaxContainers"/>.</summary>
    public static string BuildResultJson(string? baseDn, IReadOnlyList<DirectoryContainerNode> nodes)
    {
        var capped = nodes.Take(MaxContainers)
            .Select(n => new { distinguishedName = n.DistinguishedName, name = n.Name, hasChildren = n.HasChildren })
            .ToList();
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            baseDn,
            truncated = nodes.Count > MaxContainers,
            containers = capped
        });
    }

    private (bool, string, string?) Fail(string message)
    {
        var sanitized = AdAgentCreateExecutor.SanitizeLdapError(message);
        return (false, sanitized, JsonSerializer.Serialize(new { schemaVersion = 1, ldapError = sanitized }));
    }

    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    public sealed class BrowseContainersPayload
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("operation")] public string? Operation { get; set; }
        [JsonPropertyName("sourceConnectionName")] public string? SourceConnectionName { get; set; }
        [JsonPropertyName("baseDn")] public string? BaseDn { get; set; }
    }
}
