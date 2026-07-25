using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conduit.Connectors.ActiveDirectory;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Microsoft.Extensions.Configuration;

namespace Conduit.Web.Services;

/// <summary>
/// Executes an IdentityCenter "CreateAdAccount" agent command: create a NEW Active Directory user
/// through this Conduit agent, which holds DC line-of-sight + bind creds. IC is a SEPARATE trust
/// domain — nothing in the payload is taken on faith.
///
/// SECURITY MODEL:
///   - The account is created DISABLED and with NO password. No credential material comes from the
///     cloud; the shipped <see cref="ActiveDirectorySink.CreateAsync"/> creates disabled and its
///     password/enable branch is unreachable when no password is supplied. We send none.
///   - Defensively HARD-REJECT any password / enable / UAC key in the attribute bag — we do not trust
///     that IC stripped them.
///   - ★ The real containment control is the deny-all base-DN allow-list (<see cref="BaseDnContainment"/>):
///     the customer-owned config names the permitted creation base DNs, and targetOu must be
///     component-wise contained within one BEFORE any LDAP add. Empty/missing/unreadable/malformed
///     config, or a null/blank targetOu, refuses everything (create nothing).
///   - Result is reported as ResultJson: { objectGuid, distinguishedName } on success, the degraded
///     { distinguishedName, objectGuid:null } when the post-create read-back fails (the account exists —
///     IC stamps ProvisionedUnverified and reconciles on next sync), or { ldapError } on a real failure.
/// </summary>
public sealed class AdAgentCreateExecutor
{
    private const int MaxPayloadBytes = 64 * 1024;
    private const int MaxAttributeCount = 64;
    private const int MaxLdapErrorChars = 2000;

    private const string ExpectedOperation = "CreateUser";

    // Keys that must NEVER be honoured in the attribute bag — password material and enable/UAC shapes.
    private static readonly HashSet<string> ForbiddenAttributeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "userPassword", "unicodePwd",
        "active", "enabled", "userAccountControl", "accountExpires"
    };

    // Location / identity / structural keys the SINK reads to place and name the object
    // (ActiveDirectorySink.ResolveBaseDn: targetOU/ou; CreateAsync: sAMAccountName/userName for the sam,
    // userPrincipalName, cn?/displayName? for the RDN, manager/managerExternalId). These are AUTHORITATIVE
    // from the validated top-level request fields, NEVER from the free-form attribute bag — a passthrough
    // attempt to set any of them would redirect the create OU or forge the identity AFTER containment has
    // reasoned about the validated targetOu. Hard-rejected, case-insensitive. 'name'/'dn'/'distinguishedName'
    // are reserved defensively even though the current sink does not read them.
    private static readonly HashSet<string> ReservedStructuralKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "targetOU", "ou",
        "sAMAccountName", "sam", "userName",
        "userPrincipalName",
        "cn", "name", "dn", "distinguishedName",
        "displayName", "manager", "managerExternalId"
    };

    private static bool IsRejectedAttributeKey(string key) =>
        ForbiddenAttributeKeys.Contains(key) || ReservedStructuralKeys.Contains(key);

    // Config section holding the customer-owned permitted creation base DNs, keyed by connection name:
    //   "AdProvisioning:CreationBaseDns:<sourceConnectionName>": [ "OU=Staff,DC=corp,DC=local", ... ]
    private const string CreationBaseDnsSection = "AdProvisioning:CreationBaseDns";

    private readonly IEnumerable<IConnectorAdapter> _adapters;
    private readonly SinkConnectionCredentialMapRepository _credentialMap;
    private readonly TenantRepository _tenants;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdAgentCreateExecutor> _logger;

    public AdAgentCreateExecutor(
        IEnumerable<IConnectorAdapter> adapters,
        SinkConnectionCredentialMapRepository credentialMap,
        TenantRepository tenants,
        IConfiguration configuration,
        ILogger<AdAgentCreateExecutor> logger)
    {
        _adapters = adapters;
        _credentialMap = credentialMap;
        _tenants = tenants;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Validate + create. Returns (success, message, resultJson) for the poller's complete callback.
    /// NEVER throws to the caller and NEVER logs the raw payload body.
    /// </summary>
    public async Task<(bool Success, string Message, string? ResultJson)> ExecuteAsync(Guid commandId, string? payloadJson, CancellationToken ct)
    {
        // ── (1) Strict, size-bounded parse ───────────────────────────────────
        if (string.IsNullOrWhiteSpace(payloadJson))
            return Fail("CreateAdAccount: empty payload.");
        if (Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadBytes)
            return Fail($"CreateAdAccount: payload exceeds {MaxPayloadBytes / 1024} KB cap.");

        CreateAdAccountPayload? p;
        try
        {
            p = JsonSerializer.Deserialize<CreateAdAccountPayload>(payloadJson, StrictJson);
        }
        catch (JsonException)
        {
            return Fail("CreateAdAccount: malformed payload JSON.");
        }
        if (p is null)
            return Fail("CreateAdAccount: payload deserialized to null.");
        if (p.SchemaVersion != 1)
            return Fail($"CreateAdAccount: unsupported schemaVersion {p.SchemaVersion}.");

        // ── (2) operation + required fields ───────────────────────────────────
        if (!string.Equals(p.Operation?.Trim(), ExpectedOperation, StringComparison.Ordinal))
            return Fail($"CreateAdAccount: operation '{p.Operation}' is not allowed.");

        var sam = p.SamAccountName?.Trim();
        if (string.IsNullOrEmpty(sam))
            return Fail("CreateAdAccount: samAccountName is required.");

        var sourceConnectionName = p.SourceConnectionName?.Trim();
        if (string.IsNullOrEmpty(sourceConnectionName))
            return Fail("CreateAdAccount: sourceConnectionName is missing.");

        var targetOu = p.TargetOu?.Trim();
        if (string.IsNullOrEmpty(targetOu))
            return Fail("CreateAdAccount: targetOu is required.");

        if (p.Attributes is { Count: > MaxAttributeCount })
            return Fail($"CreateAdAccount: too many attributes (>{MaxAttributeCount}).");

        // ── (3) Defensive: hard-reject any password/enable/UAC key AND any location/identity/structural
        //        key. A create's OU + identity come ONLY from the validated top-level fields — the
        //        free-form bag may never redirect the create after containment has validated targetOu.
        if (p.Attributes is not null)
        {
            foreach (var key in p.Attributes.Keys)
            {
                if (IsRejectedAttributeKey(key))
                    return Fail($"CreateAdAccount: attribute '{key}' is not permitted in the bag (password/enable/UAC or location/identity keys are rejected — those come from the validated request fields).");
            }
        }

        // ── (4) ★ Containment — the deny-all base-DN allow-list, BEFORE any LDAP call ──
        var permitted = LoadPermittedBaseDns(sourceConnectionName);
        if (!BaseDnContainment.IsContained(targetOu, permitted))
        {
            _logger.LogWarning("CreateAdAccount {CommandId}: targetOu refused by the creation base-DN allow-list.", commandId);
            return Fail("CreateAdAccount: targetOu is not within any permitted creation base DN for this connection. Create refused.");
        }

        // ── (5)-(7) Credential resolution + create + read-back. Wrapped so a transient DB/LDAP error
        //           returns a clean failure — the executor NEVER throws to the caller. ──
        try
        {
            var resolvedTenantId = await _credentialMap.GetTenantIdByNameAsync(sourceConnectionName);
            if (resolvedTenantId is null || resolvedTenantId.Value == Guid.Empty)
                return Fail($"CreateAdAccount: No Conduit credential mapping for source connection '{sourceConnectionName}'. Run a sync from this connection to register it.");
            var tenant = await _tenants.GetByIdAsync(resolvedTenantId.Value);
            if (tenant is null || !tenant.IsActive)
                return Fail($"CreateAdAccount: No Conduit credential mapping for source connection '{sourceConnectionName}'. Run a sync from this connection to register it.");

            var adapter = _adapters.FirstOrDefault(a =>
                string.Equals(a.SystemType, "ActiveDirectory", StringComparison.OrdinalIgnoreCase));
            if (adapter is null)
                return Fail("CreateAdAccount: Active Directory connector is not available on this agent.");
            if (adapter.CreateSink(resolvedTenantId.Value) is not ActiveDirectorySink sink)
                return Fail("CreateAdAccount: could not create an AD sink for the requested connection.");

            // Build the create object — NO password, disabled by construction (sink :663-664).
            var connectorObject = BuildCreateObject(p, sam!, targetOu!);

            var result = await sink.CreateAsync(connectorObject, ct);
            if (result.Outcome != ProvisionOutcome.Success)
                return Fail("CreateAdAccount: " + SanitizeLdapError(result.ErrorMessage));

            var dn = result.ExternalId;
            // Read-back MUST NOT fail the create — the account already exists. Degrade to null on error.
            var objectGuid = await sink.ReadObjectGuidByDnAsync(dn!, ct);

            var resultJson = BuildSuccessResultJson(objectGuid, dn, sam!, p.UserPrincipalName?.Trim());
            var message = objectGuid is null
                ? $"Created (degraded — objectGUID read-back failed) {dn}."
                : $"Created {dn}.";
            return (true, message, resultJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateAdAccount {CommandId} threw during create.", commandId);
            return Fail("CreateAdAccount: " + SanitizeLdapError(ex.Message));
        }
    }

    // ── Pure helpers (unit-tested directly) ──────────────────────────────────

    /// <summary>
    /// Build the sink's ConnectorObject from the request: sAM/UPN/displayName/targetOU + the passthrough
    /// attributes the sink honours. Forbidden keys have already been rejected; NO password / active /
    /// UAC key is ever placed here. (The sink additionally only writes a hardcoded attribute set.)
    /// </summary>
    public static ConnectorObject BuildCreateObject(CreateAdAccountPayload p, string sam, string targetOu)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // (1) Descriptive passthrough FIRST. Any forbidden or reserved location/identity/structural key
        //     is dropped here (defence-in-depth — ExecuteAsync already hard-rejects them), so it can
        //     never survive to clobber the controlled keys set below.
        if (p.Attributes is not null)
        {
            foreach (var kvp in p.Attributes)
            {
                if (IsRejectedAttributeKey(kvp.Key)) continue;
                var value = JsonValueToString(kvp.Value);
                if (value is not null) attrs[kvp.Key] = value;
            }
        }

        // (2) Controlled/structural keys LAST, from the validated top-level fields. Because they are
        //     written after the passthrough loop, a passthrough entry can NEVER override the OU, the
        //     identity, or the RDN the sink will use.
        attrs["sAMAccountName"] = sam;
        attrs["targetOU"] = targetOu;
        if (!string.IsNullOrWhiteSpace(p.UserPrincipalName)) attrs["userPrincipalName"] = p.UserPrincipalName!.Trim();
        if (!string.IsNullOrWhiteSpace(p.DisplayName)) attrs["displayName"] = p.DisplayName!.Trim();
        if (!string.IsNullOrWhiteSpace(p.ManagerDn)) attrs["manager"] = p.ManagerDn!.Trim();

        // (3) Belt-and-suspenders: the location + identity the sink will read MUST equal the validated
        //     inputs, and no alias location key ('ou') may remain. Fail CLOSED if a future sink/refactor
        //     ever reopens this seam.
        if (!string.Equals(attrs["targetOU"] as string, targetOu, StringComparison.Ordinal)
            || !string.Equals(attrs["sAMAccountName"] as string, sam, StringComparison.Ordinal)
            || attrs.ContainsKey("ou"))
            throw new InvalidOperationException(
                "CreateAdAccount: internal invariant violated — the create OU/identity does not match the validated values.");

        return new ConnectorObject { SourceId = sam, ObjectClass = "User", Attributes = attrs };
    }

    public static string BuildSuccessResultJson(Guid? objectGuid, string? distinguishedName, string sam, string? upn) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            objectGuid = objectGuid?.ToString(),
            distinguishedName,
            samAccountName = sam,
            userPrincipalName = upn,
            accountEnabled = false,
            ldapError = (string?)null
        });

    public static string BuildFailureResultJson(string ldapError) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            objectGuid = (string?)null,
            distinguishedName = (string?)null,
            ldapError
        });

    // Strip CR/LF + all control chars, neutralize angle brackets, and cap — the agent result is
    // untrusted downstream input.
    public static string SanitizeLdapError(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "unknown error";
        var cleaned = new string(raw.Where(c => !char.IsControl(c) && c != '<' && c != '>').ToArray());
        return cleaned.Length > MaxLdapErrorChars ? cleaned[..MaxLdapErrorChars] : cleaned;
    }

    private (bool, string, string?) Fail(string message)
    {
        var sanitized = SanitizeLdapError(message);
        return (false, sanitized, BuildFailureResultJson(sanitized));
    }

    private List<string> LoadPermittedBaseDns(string sourceConnectionName)
    {
        // FAIL-CLOSED: any missing/unreadable/exception path yields an empty list (deny-all).
        try
        {
            var section = _configuration.GetSection($"{CreationBaseDnsSection}:{sourceConnectionName}");
            var values = section.Get<string[]>();
            if (values is null || values.Length == 0)
                return new List<string>();
            return values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CreateAdAccount: failed to read the creation base-DN allow-list; denying by default.");
            return new List<string>();
        }
    }

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
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    /// <summary>
    /// Strict typed model for the IC CreateAdAccount payload (schemaVersion 1). There is NO password /
    /// active / userAccountControl field here by design — those are not trusted and not read.
    /// </summary>
    public sealed class CreateAdAccountPayload
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("operation")] public string? Operation { get; set; }
        [JsonPropertyName("connectionId")] public string? ConnectionId { get; set; }              // informational; ignored
        [JsonPropertyName("sourceConnectionName")] public string? SourceConnectionName { get; set; } // the credential selector
        [JsonPropertyName("objectClass")] public string? ObjectClass { get; set; }
        [JsonPropertyName("samAccountName")] public string? SamAccountName { get; set; }
        [JsonPropertyName("userPrincipalName")] public string? UserPrincipalName { get; set; }
        [JsonPropertyName("targetOu")] public string? TargetOu { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("attributes")] public Dictionary<string, JsonElement?>? Attributes { get; set; }
        [JsonPropertyName("managerDn")] public string? ManagerDn { get; set; }
        [JsonPropertyName("mustChangePasswordAtLogon")] public bool MustChangePasswordAtLogon { get; set; }
        [JsonPropertyName("accountDefinitionId")] public string? AccountDefinitionId { get; set; }
    }
}
