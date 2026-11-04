using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conduit.Connectors.ActiveDirectory;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;

namespace Conduit.Web.Services;

/// <summary>
/// DYNAMIC-GROUPS-01 (UC034-D12): executes an IdentityCenter "EvaluateDynamicGroup" agent command, READ-ONLY. The
/// agent, which holds the LDAP bind, reads the group's Active Roles style membership rules off the group itself, runs
/// them by LDAP search with the rule precedence, and reports what a rebuild would write: the rules as resolved, the
/// current members, the desired members, the adds and the removes. Nothing is written by that command.
/// DYNAMIC-GROUPS-03: "RebuildDynamicGroup" is the write: the same evaluation, then the difference written to 'member'
/// one delta change per object, refused outright for a privileged group (the ApplyObjectWrite rule), each change's
/// outcome reported. The payload's operation must match the command the poller is running.
/// Same trust boundary as the other verbs: strict size-bounded parse, operation check, the tenant resolved from the
/// server-trusted sourceConnectionName (never from a caller id), the group re-read by objectGUID. Never throws.
/// DYNAMIC-GROUPS-05: the agent's clock (<see cref="DynamicGroupUpdaterJob"/>) rebuilds through the same evaluator and
/// the same write guard, as <see cref="IDynamicGroupClockWork"/>.
/// </summary>
public sealed class AdAgentDynamicGroupExecutor : IDynamicGroupClockWork
{
    private const int MaxPayloadBytes = 64 * 1024;
    /// <summary>Cap on the names listed per list in the result (current, desired, adds, removes); the counts stay exact.</summary>
    public const int MaxListed = 2000;
    public const string OpEvaluate = "EvaluateDynamicGroup";
    public const string OpRebuild = "RebuildDynamicGroup";
    public const string OpSetRules = "SetDynamicGroupRules";
    public const string OpConvertToStatic = "ConvertDynamicGroupToStatic";
    /// <summary>Cap on the rules one definition may carry through this verb.</summary>
    public const int MaxRules = 64;
    private static readonly HashSet<string> Operations = new(StringComparer.Ordinal) { OpEvaluate, OpRebuild, OpSetRules, OpConvertToStatic };

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

    /// <param name="expectedOperation">The command the poller is running (<see cref="OpEvaluate"/> or <see cref="OpRebuild"/>); the payload must say the same.</param>
    public async Task<(bool Success, string Message, string? ResultJson)> ExecuteAsync(Guid commandId, string? payloadJson, CancellationToken ct, string expectedOperation = OpEvaluate)
    {
        var verb = Operations.Contains(expectedOperation) ? expectedOperation : OpEvaluate;

        // ── (1) Strict, size-bounded parse ───────────────────────────────────
        if (string.IsNullOrWhiteSpace(payloadJson))
            return Fail(verb, "empty payload.");
        if (Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadBytes)
            return Fail(verb, $"payload exceeds {MaxPayloadBytes / 1024} KB cap.");

        EvaluateDynamicGroupPayload? p;
        try
        {
            p = JsonSerializer.Deserialize<EvaluateDynamicGroupPayload>(payloadJson, StrictJson);
        }
        catch (JsonException)
        {
            return Fail(verb, "malformed payload JSON.");
        }
        if (p is null)
            return Fail(verb, "payload deserialized to null.");
        if (p.SchemaVersion != 1)
            return Fail(verb, $"unsupported schemaVersion {p.SchemaVersion}.");

        // ── (2) operation + required fields: the payload must name the command being run, and nothing else ──
        if (!string.Equals(p.Operation?.Trim(), verb, StringComparison.Ordinal))
            return Fail(verb, $"operation '{p.Operation}' is not allowed.");

        var sourceConnectionName = p.SourceConnectionName?.Trim();
        if (string.IsNullOrEmpty(sourceConnectionName))
            return Fail(verb, "sourceConnectionName is missing.");

        if (!Guid.TryParse(p.ObjectGuid, out var groupGuid) || groupGuid == Guid.Empty)
            return Fail(verb, "objectGuid is missing or not a GUID.");

        // SetDynamicGroupRules: a bounded rule list, each rule checked before any LDAP call (the connector checks again).
        List<DynamicGroupRuleSpec>? rules = null;
        if (verb == OpSetRules)
        {
            if (p.Rules is null)
                return Fail(verb, "rules are missing.");
            if (p.Rules.Count > MaxRules)
                return Fail(verb, $"too many rules (>{MaxRules}).");
            rules = new List<DynamicGroupRuleSpec>(p.Rules.Count);
            foreach (var r in p.Rules)
            {
                if (r is null || r.TypeCode is < 1 or > 6)
                    return Fail(verb, "a rule's type is not one of the six membership rule types of a group.");
                if (!Guid.TryParse(r.ObjectGuid, out var ruleObject) || ruleObject == Guid.Empty)
                    return Fail(verb, "a rule names no objectGUID.");
                if (r.TypeCode is 1 or 2 && string.IsNullOrWhiteSpace(r.Filter))
                    return Fail(verb, "a query rule needs an LDAP filter.");
                rules.Add(new DynamicGroupRuleSpec(r.TypeCode, ruleObject.ToString(), r.Filter));
            }
        }

        // ── (3) Resolve the tenant and the evaluator; evaluate, rebuild, set the rules or convert back. Never throws to the caller. ──
        try
        {
            var resolvedTenantId = await _credentialMap.GetTenantIdByNameAsync(sourceConnectionName);
            if (resolvedTenantId is null || resolvedTenantId.Value == Guid.Empty)
                return Fail(verb, $"No Conduit credential mapping for source connection '{sourceConnectionName}'. Run a sync from this connection to register it.");
            var tenant = await _tenants.GetByIdAsync(resolvedTenantId.Value);
            if (tenant is null || !tenant.IsActive)
                return Fail(verb, $"No Conduit credential mapping for source connection '{sourceConnectionName}'. Run a sync from this connection to register it.");

            var (adapter, evaluator, unavailable) = ResolveEvaluator(resolvedTenantId.Value);
            if (evaluator is null)
                return Fail(verb, unavailable!);

            if (verb == OpEvaluate)
            {
                var evaluation = await evaluator.EvaluateAsync(groupGuid.ToString(), ct);
                if (evaluation.ErrorMessage is not null)
                    return Fail(verb, AdAgentCreateExecutor.SanitizeLdapError(evaluation.ErrorMessage));
                return (true, Describe(evaluation), BuildResultJson(evaluation));
            }

            // The writes: the group must be a group, and never one of the privileged groups ApplyObjectWrite refuses.
            var refusal = await RefuseWriteAsync(adapter!, resolvedTenantId.Value, groupGuid, ct);
            if (refusal is not null)
                return Fail(verb, refusal);

            var write = verb switch
            {
                OpRebuild => await evaluator.RebuildAsync(groupGuid.ToString(), ct),
                OpSetRules => await evaluator.SetRulesAsync(groupGuid.ToString(), rules!, p.ClearMembers, p.Notes, ct),
                _ => await evaluator.ConvertToStaticAsync(groupGuid.ToString(), p.Notes, ct),
            };
            if (write.ErrorMessage is not null)
                return Fail(verb, AdAgentCreateExecutor.SanitizeLdapError(write.ErrorMessage));
            return (true, DescribeWrite(verb, write), BuildRebuildResultJson(write, verb));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Verb} {CommandId} threw.", verb, commandId);
            return Fail(verb, AdAgentCreateExecutor.SanitizeLdapError(ex.Message));
        }
    }

    /// <summary>One sentence for a rebuild's completion message; counts, never names.</summary>
    public static string DescribeRebuild(DynamicGroupRebuild r)
    {
        var failed = r.Failed > 0 ? $" {r.Failed} change(s) failed." : "";
        var skipped = r.Skipped > 0 ? $" {r.Skipped} already so." : "";
        return $"'{r.Evaluation.GroupName}' rebuilt: {r.AppliedAdds} added, {r.AppliedRemoves} removed of {r.Evaluation.DesiredMembers.Count} by the rules.{failed}{skipped}";
    }

    /// <summary>One sentence for any of the three writes' completion message; counts, never names.</summary>
    public static string DescribeWrite(string verb, DynamicGroupRebuild r) => verb switch
    {
        OpSetRules => $"'{r.Evaluation.GroupName}': {r.RulesWritten ?? 0} rule(s) written{(r.Converted ? $"; converted to a dynamic group, {r.AppliedRemoves} member(s) removed{(r.Failed > 0 ? $", {r.Failed} could not be removed" : "")}" : "")}.",
        OpConvertToStatic => $"'{r.Evaluation.GroupName}': converted to a static group; its members stay as last computed.",
        _ => DescribeRebuild(r),
    };

    /// <summary>The write's ResultJson: every change with its outcome and the member's objectGUID (for the catalog's settlement), the counts, the evaluation's counts; for a rules write, how many rules and whether it converted.</summary>
    public static string BuildRebuildResultJson(DynamicGroupRebuild r, string operation = OpRebuild)
    {
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            operation,
            rulesWritten = r.RulesWritten,
            converted = r.Converted,
            groupDn = r.Evaluation.GroupDistinguishedName,
            groupName = r.Evaluation.GroupName,
            changes = r.Changes.Select(c => new
            {
                operation = c.Operation,
                dn = c.DistinguishedName,
                objectGuid = c.ObjectGuid,
                outcome = c.Outcome,
                error = c.Error,
            }).ToList(),
            appliedAdds = r.AppliedAdds,
            appliedRemoves = r.AppliedRemoves,
            skipped = r.Skipped,
            failed = r.Failed,
            currentCount = r.Evaluation.CurrentMembers.Count,
            desiredCount = r.Evaluation.DesiredMembers.Count,
        });
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

    // ── The clock's view (IDynamicGroupClockWork): the same evaluator, the same guard, no payload ──────────────

    public async Task<IReadOnlyList<DynamicGroupClockTenant>> ActiveDirectoryTenantsAsync()
    {
        var tenants = await _tenants.GetAllAsync();
        return tenants
            .Where(t => t.IsActive && string.Equals(t.SystemType, "ActiveDirectory", StringComparison.OrdinalIgnoreCase))
            .Select(t => new DynamicGroupClockTenant(t.Id, t.Name))
            .ToList();
    }

    public async Task<string?> SourceConnectionNameAsync(Guid tenantId)
    {
        var names = await _credentialMap.GetSourceConnectionNamesByTenantAsync(tenantId);
        return names.FirstOrDefault();
    }

    public async Task<(IReadOnlyList<DynamicGroupListing>? Groups, string? Error)> ListDynamicGroupsAsync(Guid tenantId, CancellationToken ct)
    {
        var (_, evaluator, unavailable) = ResolveEvaluator(tenantId);
        if (evaluator is null) return (null, unavailable);
        try { return (await evaluator.ListAsync(ct), null); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (null, AdAgentCreateExecutor.SanitizeLdapError(ex.Message)); }
    }

    public async Task<bool?> AnyDirectoryChangeSinceAsync(Guid tenantId, DateTime sinceUtc, CancellationToken ct)
    {
        var (_, evaluator, _) = ResolveEvaluator(tenantId);
        if (evaluator is null) return null;
        try { return await evaluator.AnyChangeSinceAsync(sinceUtc, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The change watch could not ask connection {TenantId}.", tenantId);
            return null;
        }
    }

    public async Task<(DynamicGroupRebuild? Rebuild, string? Refusal)> RebuildOnClockAsync(Guid tenantId, Guid groupGuid, CancellationToken ct)
    {
        var (adapter, evaluator, unavailable) = ResolveEvaluator(tenantId);
        if (evaluator is null) return (null, unavailable);
        try
        {
            var refusal = await RefuseWriteAsync(adapter!, tenantId, groupGuid, ct);
            if (refusal is not null) return (null, refusal);
            return (await evaluator.RebuildAsync(groupGuid.ToString(), ct), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (null, AdAgentCreateExecutor.SanitizeLdapError(ex.Message)); }
    }

    /// <summary>The Active Directory adapter's evaluator for a tenant, or why there is none.</summary>
    private (IConnectorAdapter? Adapter, IConnectorDynamicGroupEvaluator? Evaluator, string? Error) ResolveEvaluator(Guid tenantId)
    {
        var adapter = _adapters.FirstOrDefault(a =>
            string.Equals(a.SystemType, "ActiveDirectory", StringComparison.OrdinalIgnoreCase));
        if (adapter is null)
            return (null, null, "Active Directory connector is not available on this agent.");
        var evaluator = adapter.CreateDynamicGroupEvaluator(tenantId);
        return evaluator is null
            ? (adapter, null, "this connector does not evaluate dynamic groups.")
            : (adapter, evaluator, null);
    }

    /// <summary>THE write guard of the dynamic-group verbs: the target must resolve, be a group, and never a privileged group (the ApplyObjectWrite rule). Null when the write may go.</summary>
    private static async Task<string?> RefuseWriteAsync(IConnectorAdapter adapter, Guid tenantId, Guid groupGuid, CancellationToken ct)
    {
        if (adapter.CreateSink(tenantId) is not ActiveDirectorySink sink)
            return "could not create an AD sink for the requested connection.";
        var target = await sink.ResolveByGuidAsync(groupGuid.ToString(), ct);
        if (target is null)
            return $"objectGuid {groupGuid} resolved to no AD object.";
        if (!target.IsClass("group"))
            return "the target is not a group object.";
        if (AdAgentWriteExecutor.IsPrivilegedGroup(target, out var why))
            return $"refusing to change a privileged group ({why}).";
        return null;
    }

    private (bool, string, string?) Fail(string verb, string message)
    {
        var sanitized = AdAgentCreateExecutor.SanitizeLdapError(string.Concat(verb, ": ", message));
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
        /// <summary>SetDynamicGroupRules: the rule list to write.</summary>
        [JsonPropertyName("rules")] public List<RulePayload>? Rules { get; set; }
        /// <summary>SetDynamicGroupRules: a conversion removes every current member first.</summary>
        [JsonPropertyName("clearMembers")] public bool ClearMembers { get; set; }
        /// <summary>The sentence for the group's Notes on a conversion; on a conversion back, the sentence to clear.</summary>
        [JsonPropertyName("notes")] public string? Notes { get; set; }
    }

    public sealed class RulePayload
    {
        [JsonPropertyName("type")] public int TypeCode { get; set; }
        [JsonPropertyName("objectGuid")] public string? ObjectGuid { get; set; }
        [JsonPropertyName("filter")] public string? Filter { get; set; }
    }
}
