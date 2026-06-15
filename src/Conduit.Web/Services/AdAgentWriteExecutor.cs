using System.Text.Json;
using System.Text.Json.Serialization;
using Conduit.Connectors.ActiveDirectory;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;

namespace Conduit.Web.Services;

/// <summary>
/// Executes an IdentityCenter "ApplyObjectWrite" agent command: a single,
/// GUID-addressed Active Directory write routed through this Conduit job server
/// (which holds DC line-of-sight + bind creds). IC enqueues the command over its
/// agent-command HTTP API; the poller hands the raw PayloadJson here.
///
/// This class owns ALL trust-boundary enforcement. IC is a SEPARATE trust domain:
/// nothing in the payload is taken on faith — not the operation, not the attribute
/// names, not the object class, and above all NOT the dnHint. The write target is
/// always RE-RESOLVED by objectGUID on the live connection before any modify, and
/// the object's real AD class + identity are validated against that read.
///
/// Security constraints enforced here (numbers map to the task spec):
///   1  Strict typed parse; >64 KB or >reasonable element count → hard failure.
///   2  operation allow-list (closed set); anything else → fail, no LDAP call.
///   3  attribute-NAME allow-list (fixed AD writable set); reject outside it.
///   4  re-resolve target by objectGuid; never write to dnHint; no object → fail.
///   5  confirm re-resolved class matches the operation (AD-read class, not payload).
///   6  membership is DELTA add/remove (sink uses Add/Delete on 'member').
///   7  privileged-group guard (well-known names + RID suffixes 512/519/518/544/548).
///   8  filters/DNs built from objectGuid only, via the sink's Escape helpers.
///   9  Enable/Disable → SetUacFlag; SetManager/SetAttributes → Replace (null = clear).
///  10  clear, non-sensitive success/failure message returned to the caller.
/// </summary>
public sealed class AdAgentWriteExecutor
{
    /// <summary>Hard cap on the PayloadJson size we will even attempt to parse.</summary>
    private const int MaxPayloadBytes = 64 * 1024;

    /// <summary>Hard cap on the number of attribute entries in a SetAttributes payload.</summary>
    private const int MaxAttributeCount = 64;

    private const string OpSetAttributes = "SetAttributes";
    private const string OpEnable = "Enable";
    private const string OpDisable = "Disable";
    private const string OpSetManager = "SetManager";
    private const string OpAddGroupMember = "AddGroupMember";
    private const string OpRemoveGroupMember = "RemoveGroupMember";

    private static readonly HashSet<string> AllowedOperations = new(StringComparer.Ordinal)
    {
        OpSetAttributes, OpEnable, OpDisable, OpSetManager, OpAddGroupMember, OpRemoveGroupMember
    };

    /// <summary>
    /// Fixed AD attribute allow-list — mirrors IC's writable set. Case-insensitive
    /// match; the canonical-cased name from this set is what we send to AD.
    /// 'manager' is included because SetManager writes it as a DN.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["displayName"] = "displayName",
        ["description"] = "description",
        ["physicalDeliveryOfficeName"] = "physicalDeliveryOfficeName",
        ["mail"] = "mail",
        ["wWWHomePage"] = "wWWHomePage",
        ["telephoneNumber"] = "telephoneNumber",
        ["title"] = "title",
        ["department"] = "department",
        ["company"] = "company",
        ["manager"] = "manager",
        ["streetAddress"] = "streetAddress",
        ["l"] = "l",
        ["st"] = "st",
        ["postalCode"] = "postalCode",
        ["co"] = "co",
        ["homePhone"] = "homePhone",
        ["mobile"] = "mobile",
        ["facsimileTelephoneNumber"] = "facsimileTelephoneNumber",
        ["userPrincipalName"] = "userPrincipalName",
        ["givenName"] = "givenName",
        ["sn"] = "sn",
    };

    /// <summary>Well-known privileged-group RIDs (objectSid suffix).</summary>
    private static readonly HashSet<uint> PrivilegedRids = new() { 512, 519, 518, 544, 548 };

    /// <summary>Well-known privileged-group names (defense in depth alongside RIDs).</summary>
    private static readonly HashSet<string> PrivilegedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Domain Admins", "Enterprise Admins", "Schema Admins", "Administrators", "Account Operators"
    };

    private readonly IEnumerable<IConnectorAdapter> _adapters;
    private readonly SinkConnectionCredentialMapRepository _credentialMap;
    private readonly TenantRepository _tenants;
    private readonly ILogger<AdAgentWriteExecutor> _logger;

    public AdAgentWriteExecutor(
        IEnumerable<IConnectorAdapter> adapters,
        SinkConnectionCredentialMapRepository credentialMap,
        TenantRepository tenants,
        ILogger<AdAgentWriteExecutor> logger)
    {
        _adapters = adapters;
        _credentialMap = credentialMap;
        _tenants = tenants;
        _logger = logger;
    }

    /// <summary>
    /// Validate + execute an ApplyObjectWrite payload. Returns (success, message)
    /// for the poller's complete callback. NEVER throws to the caller and NEVER
    /// logs the raw payload body — only command-shaped facts (operation + result).
    /// </summary>
    public async Task<(bool Success, string Message)> ExecuteAsync(Guid commandId, string? payloadJson, CancellationToken ct)
    {
        // ── (1) Strict, size-bounded parse ───────────────────────────────────
        if (string.IsNullOrWhiteSpace(payloadJson))
            return (false, "ApplyObjectWrite: empty payload.");
        if (System.Text.Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadBytes)
            return (false, $"ApplyObjectWrite: payload exceeds {MaxPayloadBytes / 1024} KB cap.");

        ApplyObjectWritePayload? p;
        try
        {
            p = JsonSerializer.Deserialize<ApplyObjectWritePayload>(payloadJson, StrictJson);
        }
        catch (JsonException)
        {
            // Do not echo the body — malformed JSON is a HARD failure, not a fall-through.
            return (false, "ApplyObjectWrite: malformed payload JSON.");
        }
        if (p is null)
            return (false, "ApplyObjectWrite: payload deserialized to null.");

        if (p.SchemaVersion != 1)
            return (false, $"ApplyObjectWrite: unsupported schemaVersion {p.SchemaVersion}.");

        // ── (2) operation allow-list (closed set) — before any LDAP call ─────
        var operation = p.Operation?.Trim();
        if (string.IsNullOrEmpty(operation) || !AllowedOperations.Contains(operation))
            return (false, $"ApplyObjectWrite: operation '{p.Operation}' is not allowed.");

        if (!Guid.TryParse(p.ObjectGuid, out var objGuid) || objGuid == Guid.Empty)
            return (false, "ApplyObjectWrite: objectGuid is missing or not a GUID.");

        // ── Credential resolution (Conduit owns it) ─────────────────────────────
        // The caller-supplied connectionId is IGNORED for credential selection.
        // We resolve the Conduit tenant whose 'ldap' credential backs this write
        // from the server-trusted sourceConnectionName via the orchestrator-owned
        // mapping. No mapping / disabled tenant / missing credential → fail closed;
        // never fall back to connectionId or a default tenant.
        var sourceConnectionName = p.SourceConnectionName?.Trim();
        if (string.IsNullOrEmpty(sourceConnectionName))
            return (false, "ApplyObjectWrite: sourceConnectionName is missing.");

        var resolvedTenantId = await _credentialMap.GetTenantIdByNameAsync(sourceConnectionName);
        if (resolvedTenantId is null || resolvedTenantId.Value == Guid.Empty)
            return (false, $"ApplyObjectWrite: No Conduit credential mapping for source connection '{sourceConnectionName}'. Run a sync from this connection to register it.");

        var tenant = await _tenants.GetByIdAsync(resolvedTenantId.Value);
        if (tenant is null || !tenant.IsActive)
            return (false, $"ApplyObjectWrite: No Conduit credential mapping for source connection '{sourceConnectionName}'. Run a sync from this connection to register it.");

        // Bounded element count for SetAttributes — refuse pathological payloads.
        if (string.Equals(operation, OpSetAttributes, StringComparison.Ordinal)
            && p.Attributes is { Count: > MaxAttributeCount })
            return (false, $"ApplyObjectWrite: too many attributes (>{MaxAttributeCount}).");

        // ── Resolve the AD connector + a sink bound to the resolved tenant ──────
        var adapter = _adapters.FirstOrDefault(a =>
            string.Equals(a.SystemType, "ActiveDirectory", StringComparison.OrdinalIgnoreCase));
        if (adapter is null)
            return (false, "ApplyObjectWrite: Active Directory connector is not available on this agent.");
        if (adapter.CreateSink(resolvedTenantId.Value) is not ActiveDirectorySink sink)
            return (false, "ApplyObjectWrite: could not create an AD sink for the requested connection.");

        try
        {
            // ── (4) Re-resolve the write target by objectGuid. The dnHint is
            //        advisory ONLY and is never used as a write target. ────────
            var target = await sink.ResolveByGuidAsync(p.ObjectGuid!, ct);
            if (target is null)
                return (false, $"ApplyObjectWrite: objectGuid {p.ObjectGuid} resolved to no AD object.");

            return operation switch
            {
                OpSetAttributes => await DoSetAttributesAsync(sink, target, p, ct),
                OpSetManager => await DoSetManagerAsync(sink, target, p, ct),
                OpEnable => await DoEnableAsync(sink, target, p.ObjectGuid!, enabled: true, ct),
                OpDisable => await DoEnableAsync(sink, target, p.ObjectGuid!, enabled: false, ct),
                OpAddGroupMember => await DoMembershipAsync(sink, target, p, add: true, ct),
                OpRemoveGroupMember => await DoMembershipAsync(sink, target, p, add: false, ct),
                _ => (false, $"ApplyObjectWrite: operation '{operation}' is not allowed.")
            };
        }
        catch (Exception ex)
        {
            // Never leak payload content; log command-shaped facts only.
            _logger.LogError(ex, "ApplyObjectWrite {CommandId} ({Operation}) threw.", commandId, operation);
            return (false, $"ApplyObjectWrite: {operation} failed: {ex.Message}");
        }
    }

    // ── SetAttributes ────────────────────────────────────────────────────────
    private async Task<(bool, string)> DoSetAttributesAsync(
        ActiveDirectorySink sink, AdResolvedObject target, ApplyObjectWritePayload p, CancellationToken ct)
    {
        // (5) class check — SetAttributes targets a user (per the payload contract,
        // attributes are user-shaped). Validate against the AD-read class.
        if (!target.IsClass("user"))
            return (false, "ApplyObjectWrite: SetAttributes target is not a user object.");

        if (p.Attributes is null || p.Attributes.Count == 0)
            return (false, "ApplyObjectWrite: SetAttributes has no attributes.");

        // (3) attribute-NAME allow-list — validate the WHOLE set first; reject the
        // entire command if any name is outside the list (no partial application).
        var resolved = new List<(string Name, string? Value)>(p.Attributes.Count);
        foreach (var kvp in p.Attributes)
        {
            if (!AllowedAttributes.TryGetValue(kvp.Key, out var canonical))
                return (false, $"ApplyObjectWrite: attribute '{kvp.Key}' is not in the writable allow-list.");
            resolved.Add((canonical, JsonValueToString(kvp.Value)));
        }

        int applied = 0;
        foreach (var (name, value) in resolved)
        {
            // (9) Replace; null/empty value = clear. (8) target is the GUID-resolved
            // DN inside the sink; we never pass dnHint.
            var r = await sink.SetAttributeByGuidAsync(p.ObjectGuid!, name, value, ct);
            if (r.Outcome == SinkWriteOutcome.Failed)
                return (false, $"ApplyObjectWrite: SetAttributes('{name}') failed: {r.ErrorMessage}");
            applied++;
        }
        return (true, $"SetAttributes applied {applied} attribute(s) to {SafeDn(target)}.");
    }

    // ── SetManager ─────────────────────────────────────────────────────────
    private async Task<(bool, string)> DoSetManagerAsync(
        ActiveDirectorySink sink, AdResolvedObject target, ApplyObjectWritePayload p, CancellationToken ct)
    {
        // (5) manager is a user attribute.
        if (!target.IsClass("user"))
            return (false, "ApplyObjectWrite: SetManager target is not a user object.");

        // The manager DN arrives under attributes["manager"]; null clears it.
        string? managerDn = null;
        if (p.Attributes is not null && p.Attributes.TryGetValue("manager", out var mv))
            managerDn = JsonValueToString(mv);

        // (3) only 'manager' is permitted for this operation.
        if (p.Attributes is not null)
            foreach (var k in p.Attributes.Keys)
                if (!string.Equals(k, "manager", StringComparison.OrdinalIgnoreCase))
                    return (false, $"ApplyObjectWrite: SetManager only writes 'manager', not '{k}'.");

        var r = await sink.SetAttributeByGuidAsync(p.ObjectGuid!, "manager", managerDn, ct);
        if (r.Outcome == SinkWriteOutcome.Failed)
            return (false, $"ApplyObjectWrite: SetManager failed: {r.ErrorMessage}");
        return (true, managerDn is null
            ? $"SetManager cleared manager on {SafeDn(target)}."
            : $"SetManager updated manager on {SafeDn(target)}.");
    }

    // ── Enable / Disable ─────────────────────────────────────────────────────
    private async Task<(bool, string)> DoEnableAsync(
        ActiveDirectorySink sink, AdResolvedObject target, string objectGuid, bool enabled, CancellationToken ct)
    {
        // (5) Enable/Disable only apply to a user account, never a group.
        if (!target.IsClass("user"))
            return (false, $"ApplyObjectWrite: {(enabled ? "Enable" : "Disable")} target is not a user object.");

        // (9) the sink re-resolves the GUID again and flips ACCOUNTDISABLE.
        var r = await sink.SetEnabledByGuidAsync(objectGuid, enabled, ct);
        return ResultToTuple(r, enabled ? $"Enabled {SafeDn(target)}." : $"Disabled {SafeDn(target)}.");
    }

    // ── Add / Remove group member (DELTA) ────────────────────────────────────
    private async Task<(bool, string)> DoMembershipAsync(
        ActiveDirectorySink sink, AdResolvedObject groupTarget, ApplyObjectWritePayload p, bool add, CancellationToken ct)
    {
        // (5) the GUID-resolved target MUST actually be a group.
        if (!groupTarget.IsClass("group"))
            return (false, $"ApplyObjectWrite: {(add ? "AddGroupMember" : "RemoveGroupMember")} target is not a group object.");

        // (7) privileged-group guard — refuse membership writes to admin groups.
        if (IsPrivilegedGroup(groupTarget, out var why))
            return (false, $"ApplyObjectWrite: refusing membership write to privileged group ({why}).");

        if (!Guid.TryParse(p.MemberObjectGuid, out var memberGuid) || memberGuid == Guid.Empty)
            return (false, "ApplyObjectWrite: memberObjectGuid is missing or not a GUID.");

        // (6) DELTA add/remove — sink uses Add/Delete on 'member', re-resolving
        // BOTH the group (payload objectGuid) and the member by GUID. Never the
        // full-replace path.
        var r = add
            ? await sink.AddGroupMemberAsync(p.ObjectGuid!, p.MemberObjectGuid!, ct)
            : await sink.RemoveGroupMemberAsync(p.ObjectGuid!, p.MemberObjectGuid!, ct);

        var verb = add ? "Added member to" : "Removed member from";
        return ResultToTuple(r, $"{verb} {SafeDn(groupTarget)}.");
    }

    // ── (7) privileged-group identity check, against the AD-read object ───────
    private static bool IsPrivilegedGroup(AdResolvedObject group, out string reason)
    {
        var rid = group.Rid;
        if (rid is not null && PrivilegedRids.Contains(rid.Value))
        {
            reason = $"well-known RID {rid.Value}";
            return true;
        }
        if (!string.IsNullOrEmpty(group.SamAccountName) && PrivilegedNames.Contains(group.SamAccountName!))
        {
            reason = $"name '{group.SamAccountName}'";
            return true;
        }
        if (!string.IsNullOrEmpty(group.Name) && PrivilegedNames.Contains(group.Name!))
        {
            reason = $"name '{group.Name}'";
            return true;
        }
        reason = string.Empty;
        return false;
    }

    private static (bool, string) ResultToTuple(SinkWriteResult r, string okMessage) =>
        r.Outcome == SinkWriteOutcome.Failed
            ? (false, $"ApplyObjectWrite failed: {r.ErrorMessage}")
            : (true, okMessage);

    private static string SafeDn(AdResolvedObject o) => o.DistinguishedName ?? "<resolved object>";

    private static string? JsonValueToString(JsonElement? el)
    {
        if (el is null) return null;
        var v = el.Value;
        return v.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True => "TRUE",
            JsonValueKind.False => "FALSE",
            _ => v.ToString()
        };
    }

    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = true,
        // Reject trailing commas / comments — keep the parse strict.
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    /// <summary>
    /// Strict typed model for the IC ApplyObjectWrite payload (schemaVersion 1).
    /// Extra/unknown JSON properties are ignored by default; the fields we trust
    /// are exactly these. NO password / secret / token / filter / searchbase field
    /// exists here by design — password operations are deferred.
    /// </summary>
    private sealed class ApplyObjectWritePayload
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("objectGuid")] public string? ObjectGuid { get; set; }
        [JsonPropertyName("dnHint")] public string? DnHint { get; set; }            // advisory only — never a write target
        [JsonPropertyName("connectionId")] public string? ConnectionId { get; set; }  // transition only — IGNORED for credential selection
        [JsonPropertyName("sourceConnectionName")] public string? SourceConnectionName { get; set; }  // server-resolved IC DirectoryConnections.Name; the credential selector
        [JsonPropertyName("objectClass")] public string? ObjectClass { get; set; }   // advisory — AD-read class wins
        [JsonPropertyName("operation")] public string? Operation { get; set; }
        [JsonPropertyName("attributes")] public Dictionary<string, JsonElement?>? Attributes { get; set; }
        [JsonPropertyName("memberObjectGuid")] public string? MemberObjectGuid { get; set; }
    }
}
