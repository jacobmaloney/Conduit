using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;

namespace Conduit.Web.Services;

/// <summary>
/// DYNAMIC-GROUPS-01 (UC034-D12): executes an IdentityCenter "EvaluateDynamicGroup" agent command, READ-ONLY. The
/// agent, which holds the LDAP bind, reads the group's Active Roles style membership rules off the group itself, runs
/// them by LDAP search with the rule precedence, and reports what a rebuild would write: the rules as resolved, the
/// current members, the desired members, the adds and the removes. Nothing is written; the rebuild is a later verb.
/// Same trust boundary as the other verbs: strict size-bounded parse, operation check, the tenant resolved from the
/// server-trusted sourceConnectionName (never from a caller id), the group re-read by objectGUID. Never throws.
/// </summary>
public sealed class AdAgentDynamicGroupExecutor
{
    private const int MaxPayloadBytes = 64 * 1024;
    /// <summary>Cap on the names listed per list in the result (current, desired, adds, removes); the counts stay exact.</summary>
    public const int MaxListed = 2000;
    private const string ExpectedOperation = "EvaluateDynamicGroup";

    private readonly IEnumerable<IConnectorAdapter> _adapters;
    private readonly SinkConnectionCredentialMapRepository _credentialMap;
    private readonly TenantRepository _tenants;
    private readonly ILogger<AdAgentDynamicGroupExecutor> _logger;

    public AdAgentDynamicGroupExecutor(
        IEnumerable<IConnectorAdapter> adapters,
        SinkConnectionCredentialMapRepository credentialMap,
        TenantRepository tenants,
        ILogger<AdAgentDynamicGroupExecutor> logger)
    {
        _adapters = adapters;
        _credentialMap = credentialMap;
        _tenants = tenants;
        _logger = logger;
    }

    public async Task<(bool Success, string Message, string? ResultJson)> ExecuteAsync(Guid commandId, string? payloadJson, CancellationToken ct)
    {
        // ── (1) Strict, size-bounded parse ───────────────────────────────────
        if (string.IsNullOrWhiteSpace(payloadJson))
            return Fail("EvaluateDynamicGroup: empty payload.");
        if (Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadBytes)
            return Fail($"EvaluateDynamicGroup: payload exceeds {MaxPayloadBytes / 1024} KB cap.");

        EvaluateDynamicGroupPayload? p;
        try
        {
            p = JsonSerializer.Deserialize<EvaluateDynamicGroupPayload>(payloadJson, StrictJson);
        }
        catch (JsonException)
        {
            return Fail("EvaluateDynamicGroup: malformed payload JSON.");
        }
        if (p is null)
            return Fail("EvaluateDynamicGroup: payload deserialized to null.");
        if (p.SchemaVersion != 1)
            return Fail($"EvaluateDynamicGroup: unsupported schemaVersion {p.SchemaVersion}.");

        // ── (2) operation + required fields ───────────────────────────────────
        if (!string.Equals(p.Operation?.Trim(), ExpectedOperation, StringComparison.Ordinal))
            return Fail($"EvaluateDynamicGroup: operation '{p.Operation}' is not allowed.");

        var sourceConnectionName = p.SourceConnectionName?.Trim();
        if (string.IsNullOrEmpty(sourceConnectionName))
            return Fail("EvaluateDynamicGroup: sourceConnectionName is missing.");

        if (!Guid.TryParse(p.ObjectGuid, out var groupGuid) || groupGuid == Guid.Empty)
            return Fail("EvaluateDynamicGroup: objectGuid is missing or not a GUID.");

        // ── (3) Resolve the tenant and the evaluator; evaluate (READ-ONLY). Never throws to the caller. ──
        try
        {
            var resolvedTenantId = await _credentialMap.GetTenantIdByNameAsync(sourceConnectionName);
            if (resolvedTenantId is null || resolvedTenantId.Value == Guid.Empty)
                return Fail($"EvaluateDynamicGroup: No Conduit credential mapping for source connection '{sourceConnectionName}'. Run a sync from this connection to register it.");
            var tenant = await _tenants.GetByIdAsync(resolvedTenantId.Value);
            if (tenant is null || !tenant.IsActive)
                return Fail($"EvaluateDynamicGroup: No Conduit credential mapping for source connection '{sourceConnectionName}'. Run a sync from this connection to register it.");

            var adapter = _adapters.FirstOrDefault(a =>
                string.Equals(a.SystemType, "ActiveDirectory", StringComparison.OrdinalIgnoreCase));
            if (adapter is null)
                return Fail("EvaluateDynamicGroup: Active Directory connector is not available on this agent.");
            var evaluator = adapter.CreateDynamicGroupEvaluator(resolvedTenantId.Value);
            if (evaluator is null)
                return Fail("EvaluateDynamicGroup: this connector does not evaluate dynamic groups.");

            var evaluation = await evaluator.EvaluateAsync(groupGuid.ToString(), ct);
            if (evaluation.ErrorMessage is not null)
                return Fail("EvaluateDynamicGroup: " + AdAgentCreateExecutor.SanitizeLdapError(evaluation.ErrorMessage));

            return (true, Describe(evaluation), BuildResultJson(evaluation));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EvaluateDynamicGroup {CommandId} threw.", commandId);
            return Fail("EvaluateDynamicGroup: " + AdAgentCreateExecutor.SanitizeLdapError(ex.Message));
        }
    }

    /// <summary>One sentence for the command's completion message; counts, never names.</summary>
    public static string Describe(DynamicGroupEvaluation e)
    {
        if (!e.IsDynamic)
            return $"'{e.GroupName}' carries no membership rules (a static group with {e.CurrentMembers.Count} member(s)).";
        var failed = e.Rules.Count(r => r.Error is not null);
        var note = e.Truncated ? " A rule's search was capped; the difference is a lower bound." : "";
        var errors = failed > 0 ? $" {failed} rule(s) could not be evaluated." : "";
        return $"'{e.GroupName}': {e.Rules.Count} rule(s); {e.CurrentMembers.Count} current, {e.DesiredMembers.Count} desired; +{e.Adds.Count} / -{e.Removes.Count}.{errors}{note}";
    }

    /// <summary>The ResultJson: the rules as resolved and the four name lists, each capped at <see cref="MaxListed"/> with its exact count beside it.</summary>
    public static string BuildResultJson(DynamicGroupEvaluation e)
    {
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            groupDn = e.GroupDistinguishedName,
            groupName = e.GroupName,
            isDynamic = e.IsDynamic,
            originatingService = e.OriginatingService,
            rules = e.Rules.Select(r => new
            {
                typeCode = r.TypeCode,
                type = r.Type,
                objectGuid = r.ObjectGuid,
                objectDn = r.ObjectDistinguishedName,
                filter = r.Filter,
                matchCount = r.MatchCount,
                truncated = r.Truncated,
                error = r.Error,
            }).ToList(),
            currentCount = e.CurrentMembers.Count,
            desiredCount = e.DesiredMembers.Count,
            addCount = e.Adds.Count,
            removeCount = e.Removes.Count,
            current = e.CurrentMembers.Take(MaxListed).ToList(),
            desired = e.DesiredMembers.Take(MaxListed).ToList(),
            adds = e.Adds.Take(MaxListed).ToList(),
            removes = e.Removes.Take(MaxListed).ToList(),
            listsTruncated = e.CurrentMembers.Count > MaxListed || e.DesiredMembers.Count > MaxListed || e.Adds.Count > MaxListed || e.Removes.Count > MaxListed,
            searchTruncated = e.Truncated,
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

    public sealed class EvaluateDynamicGroupPayload
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("operation")] public string? Operation { get; set; }
        [JsonPropertyName("sourceConnectionName")] public string? SourceConnectionName { get; set; }
        [JsonPropertyName("objectGuid")] public string? ObjectGuid { get; set; }
    }
}
