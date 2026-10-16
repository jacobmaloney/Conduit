using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conduit.Connectors.ActiveDirectory;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;

namespace Conduit.Web.Services;

/// <summary>
/// Executes an IdentityCenter "CreateAdAccount" agent command: create a NEW Active Directory user
/// through this Conduit agent, which holds DC line-of-sight + bind creds. IC is a SEPARATE trust
/// domain — nothing in the payload is taken on faith.
///
/// SECURITY MODEL:
///   - By default (`enableOnCreate` absent/false) the account is created DISABLED and with NO password.
///     No credential material EVER comes from the cloud.
///   - UC008-D07: when the validated top-level `enableOnCreate` is true, THIS executor generates the
///     initial password locally (<see cref="AgentPasswordGenerator"/>), places it — together with
///     `active` and `mustChangePasswordAtLogon` — on the <see cref="ConnectorObject"/> it builds itself,
///     and the sink sets the password, forces change-at-logon when asked, and clears the disable bit.
///     The password is never logged, never in the result JSON, never in a message; the reference is
///     dropped after use. BEFORE any LDAP add the connection is probed: no LDAPS/StartTLS ⇒ the create
///     is REFUSED (a half-created disabled account is worse than no account).
///   - Defensively HARD-REJECT any password / enable / UAC / pwdLastSet key in the attribute bag — we do
///     not trust that IC stripped them. Only the executor places the credential keys, from validated
///     top-level fields, and only when it was asked to enable.
///   - UC008-D04(a)/F08: search-before-create. When an account with this sAMAccountName (or UPN) already
///     exists under the connection, NOTHING is created or changed: the result is success with
///     `linked: true`, the existing objectGUID/DN, and `accountEnabled` as the directory reports it.
///   - ★ The real containment control is the deny-all base-DN allow-list (<see cref="BaseDnContainment"/>):
///     the customer-owned config names the permitted creation base DNs, and targetOu must be
///     component-wise contained within one BEFORE any LDAP call. Empty/missing/unreadable/malformed
///     config, or a null/blank targetOu, refuses everything (create nothing).
///   - Result is reported as ResultJson: { objectGuid, distinguishedName, accountEnabled, passwordSet,
///     linked } on success — `accountEnabled` is TRUE only when the enable was actually applied and
///     succeeded — the degraded objectGuid:null when the post-create read-back fails (the account exists;
///     IC stamps ProvisionedUnverified and reconciles on next sync), or { ldapError } on a real failure.
/// </summary>
public sealed class AdAgentCreateExecutor
{
    private const int MaxPayloadBytes = 64 * 1024;
    private const int MaxAttributeCount = 64;
    private const int MaxLdapErrorChars = 2000;

    private const string ExpectedOperation = "CreateUser";

    // Keys that must NEVER be honoured in the attribute bag — password material, enable/UAC shapes and
    // the change-at-logon instruction the sink now reads. The executor alone places password / active /
    // mustChangePasswordAtLogon, from the validated top-level fields (BuildCreateObject).
    private static readonly HashSet<string> ForbiddenAttributeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "userPassword", "unicodePwd",
        "active", "enabled", "userAccountControl", "accountExpires",
        "pwdLastSet", "mustChangePasswordAtLogon"
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

    private readonly IEnumerable<IConnectorAdapter> _adapters;
    private readonly SinkConnectionCredentialMapRepository _credentialMap;
    private readonly TenantRepository _tenants;
    // The permitted creation base DNs come through the policy: DB-authoritative, config fallback,
    // fail-closed deny-all. This executor does not read config directly.
    private readonly ICreationBaseDnPolicy _baseDnPolicy;
    private readonly ILogger<AdAgentCreateExecutor> _logger;

    public AdAgentCreateExecutor(
        IEnumerable<IConnectorAdapter> adapters,
        SinkConnectionCredentialMapRepository credentialMap,
        TenantRepository tenants,
        ICreationBaseDnPolicy baseDnPolicy,
        ILogger<AdAgentCreateExecutor> logger)
    {
        _adapters = adapters;
        _credentialMap = credentialMap;
        _tenants = tenants;
        _baseDnPolicy = baseDnPolicy;
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

        // ── (4) ★ Containment — the deny-all base-DN allow-list, BEFORE any LDAP call. The policy is
        //        DB-authoritative with a config fallback and FAILS CLOSED (deny-all) on any read error. ──
        var permitted = await _baseDnPolicy.GetPermittedBaseDnsAsync(sourceConnectionName);
        if (!BaseDnContainment.IsContained(targetOu, permitted))
        {
            _logger.LogWarning("CreateAdAccount {CommandId}: targetOu refused by the creation base-DN allow-list.", commandId);
            return Fail("CreateAdAccount: targetOu is not within any permitted creation base DN for this connection. Create refused.");
        }

        // ── (5) Credential resolution. Wrapped so a transient DB error returns a clean failure — the
        //        executor NEVER throws to the caller. ──
        ActiveDirectorySink sink;
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
            if (adapter.CreateSink(resolvedTenantId.Value) is not ActiveDirectorySink adSink)
                return Fail("CreateAdAccount: could not create an AD sink for the requested connection.");
            sink = adSink;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateAdAccount {CommandId} threw while resolving the connection.", commandId);
            return Fail("CreateAdAccount: " + SanitizeLdapError(ex.Message));
        }

        // ── (6)-(10) Search-before-create, the secure-channel refusal, the create, the read-back. ──
        return await ProvisionAsync(commandId, new ActiveDirectorySinkCreateAdapter(sink), p, sam!, targetOu!, ct);
    }

    /// <summary>
    /// Post-validation phase, in order: (6) search-before-create (UC008-D04(a)/F08) → link, never
    /// duplicate; (7) the enable-on-create secure-channel refusal BEFORE any LDAP add (UC008-D07);
    /// (8) the create with the executor-built ConnectorObject; (9) the objectGUID read-back (degrades,
    /// never fails the create); (10) one 32-char retry when the directory's password policy refused the
    /// first value. Called by <see cref="ExecuteAsync"/> only after every gate has passed; public so the
    /// sink seam can be driven by a fake. NEVER throws; the generated password never leaves this method.
    /// </summary>
    public async Task<(bool Success, string Message, string? ResultJson)> ProvisionAsync(
        Guid commandId, IAdAccountCreateSink sink, CreateAdAccountPayload p, string sam, string targetOu, CancellationToken ct)
    {
        var upn = string.IsNullOrWhiteSpace(p.UserPrincipalName) ? null : p.UserPrincipalName!.Trim();
        var enableOnCreate = p.EnableOnCreate;
        string? password = null;
        try
        {
            // (6) Search-before-create. Fails CLOSED: a directory that cannot be asked gets no create.
            var precheck = await sink.PrecheckCreateAsync(sam, upn, ct);
            if (precheck.Existing is { } existing)
            {
                _logger.LogInformation("CreateAdAccount {CommandId}: an account already exists (matched by {MatchedBy}) — linked, nothing created or changed.",
                    commandId, existing.MatchedBy);
                return LinkedResult(existing.DistinguishedName, existing.ObjectGuid, existing.Enabled, sam, upn);
            }

            // (7) Enable-on-create needs a channel that can carry the unicodePwd write. Refuse BEFORE the add.
            if (enableOnCreate && !precheck.SecurePasswordChannel)
            {
                _logger.LogWarning("CreateAdAccount {CommandId}: enable-on-create refused — the AD connection is not LDAPS/StartTLS, so the initial password could not be set.", commandId);
                return Fail("CreateAdAccount: enable-on-create needs a secure (LDAPS/StartTLS) connection to set the initial password; create refused.");
            }

            if (enableOnCreate)
                password = AgentPasswordGenerator.Generate(AgentPasswordGenerator.DefaultLength);

            // (8) Create. The ConnectorObject is built HERE; the credential keys exist only when asked.
            var result = await sink.CreateAsync(BuildCreateObject(p, sam, targetOu, password), ct);
            if (result.Outcome != ProvisionOutcome.Success)
                return Fail("CreateAdAccount: " + SanitizeLdapError(result.ErrorMessage));

            var dn = result.ExternalId;
            if (string.IsNullOrWhiteSpace(dn))
                return Fail("CreateAdAccount: the sink reported success without a distinguished name.");

            // The sink's own create-else-link caught a race the pre-search missed: report the link and
            // claim nothing else (no password was set, no enable applied).
            if (result.Linked)
            {
                var linkedGuid = await sink.ReadObjectGuidByDnAsync(dn!, ct);
                return LinkedResult(dn!, linkedGuid, enabled: null, sam, upn);
            }

            // (9) Read-back MUST NOT fail the create — the account already exists. Degrades to null on error.
            var objectGuid = await sink.ReadObjectGuidByDnAsync(dn!, ct);

            var passwordSet = result.PasswordSet;
            var accountEnabled = result.AccountEnabled;
            var warning = result.Warning;

            // (10) One retry at 32 chars when the password policy refused the first value — the same shape
            //      as the reset path. The account now exists (disabled), so the retry goes through
            //      ResetPassword + enable, not a second create (which would link to it).
            if (enableOnCreate && !passwordSet)
            {
                password = AgentPasswordGenerator.Generate(AgentPasswordGenerator.RetryLength);
                var reset = await sink.ResetPasswordAsync(dn!, password, p.MustChangePasswordAtLogon, ct);
                if (reset.Outcome == ProvisionOutcome.Success)
                {
                    passwordSet = true;
                    if (objectGuid is Guid g)
                    {
                        var enable = await sink.SetEnabledByGuidAsync(g.ToString(), enabled: true, ct);
                        accountEnabled = enable.Outcome != SinkWriteOutcome.Failed;
                        warning = accountEnabled ? null : "the initial password was set on retry but the enable failed: " + enable.ErrorMessage;
                    }
                    else
                    {
                        warning = "the initial password was set on retry, but the objectGUID read-back failed so the account was left disabled";
                    }
                }
                else
                {
                    warning = "the directory's password policy refused the initial password (24 and 32 chars); account left disabled: " + reset.ErrorMessage;
                }
            }

            var resultJson = BuildSuccessResultJson(objectGuid, dn, sam, upn, accountEnabled, passwordSet, linked: false);
            var message = BuildCreatedMessage(dn!, objectGuid is null, enableOnCreate, passwordSet, accountEnabled, p.MustChangePasswordAtLogon, warning);
            return (true, message, resultJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateAdAccount {CommandId} threw during create.", commandId);
            return Fail("CreateAdAccount: " + SanitizeLdapError(ex.Message));
        }
        finally
        {
            password = null; // drop the only reference; never logged, never in messages or results
        }
    }

    // ── Pure helpers (unit-tested directly) ──────────────────────────────────

    /// <summary>
    /// Build the sink's ConnectorObject from the request: sAM/UPN/displayName/targetOU + the passthrough
    /// attributes the sink honours. Forbidden keys have already been rejected; NO password / active /
    /// UAC key is ever placed here. (The sink additionally only writes a hardcoded attribute set.)
    /// </summary>
    public static ConnectorObject BuildCreateObject(CreateAdAccountPayload p, string sam, string targetOu)
        => BuildCreateObject(p, sam, targetOu, initialPassword: null);

    /// <summary>
    /// As above, plus the UC008-D07 credential instruction: when <paramref name="initialPassword"/> is
    /// non-null (the executor was asked to enable and generated it locally) the object carries
    /// `password`, `active = true` and `mustChangePasswordAtLogon` from the validated top-level field.
    /// When it is null NO credential key exists on the object — the bag can never supply one.
    /// </summary>
    public static ConnectorObject BuildCreateObject(CreateAdAccountPayload p, string sam, string targetOu, string? initialPassword)
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

        // (2b) The credential instruction, ONLY when the executor was asked. These are the keys the sink's
        //      CreateAsync password branch reads; they are written last so nothing from the bag can win.
        if (initialPassword is not null)
        {
            attrs["password"] = initialPassword;
            attrs["active"] = true;
            attrs["mustChangePasswordAtLogon"] = p.MustChangePasswordAtLogon;
        }

        // (3) Belt-and-suspenders: the location + identity the sink will read MUST equal the validated
        //     inputs, no alias location key ('ou') may remain, and the credential keys exist exactly when
        //     asked. Fail CLOSED if a future sink/refactor ever reopens this seam.
        if (!string.Equals(attrs["targetOU"] as string, targetOu, StringComparison.Ordinal)
            || !string.Equals(attrs["sAMAccountName"] as string, sam, StringComparison.Ordinal)
            || attrs.ContainsKey("ou"))
            throw new InvalidOperationException(
                "CreateAdAccount: internal invariant violated — the create OU/identity does not match the validated values.");

        var credentialKeysPresent = ForbiddenAttributeKeys.Count(attrs.ContainsKey);
        var credentialKeysExpected = initialPassword is null ? 0 : 3;
        if (credentialKeysPresent != credentialKeysExpected
            || (initialPassword is not null && !ReferenceEquals(attrs["password"], initialPassword)))
            throw new InvalidOperationException(
                "CreateAdAccount: internal invariant violated — credential keys on the create object do not match the enable instruction.");

        return new ConnectorObject { SourceId = sam, ObjectClass = "User", Attributes = attrs };
    }

    public static string BuildSuccessResultJson(Guid? objectGuid, string? distinguishedName, string sam, string? upn,
        bool accountEnabled = false, bool passwordSet = false, bool linked = false) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            objectGuid = objectGuid?.ToString(),
            distinguishedName,
            samAccountName = sam,
            userPrincipalName = upn,
            accountEnabled,
            passwordSet,
            linked,
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

    private static (bool, string, string?) LinkedResult(string dn, Guid? objectGuid, bool? enabled, string sam, string? upn)
    {
        // accountEnabled reports the directory's UAC when it was read; when it was not, it is FALSE and
        // the message says so rather than guessing.
        var resultJson = BuildSuccessResultJson(objectGuid, dn, sam, upn, accountEnabled: enabled ?? false, passwordSet: false, linked: true);
        var message = $"Linked existing account {dn}." + (enabled is null ? " (enabled state not read; accountEnabled reported false)" : string.Empty);
        return (true, message, resultJson);
    }

    private static string BuildCreatedMessage(string dn, bool degraded, bool enableRequested, bool passwordSet, bool accountEnabled, bool mustChange, string? warning)
    {
        var sb = new StringBuilder("Created ");
        if (degraded) sb.Append("(degraded — objectGUID read-back failed) ");
        sb.Append(dn).Append('.');
        if (enableRequested)
        {
            if (accountEnabled)
                sb.Append(" Enabled with an initial password").Append(mustChange ? " (must change at next logon)." : ".");
            else
                sb.Append(" Enable requested but NOT applied — account left disabled").Append(passwordSet ? " (password set)." : " (no password).");
        }
        if (!string.IsNullOrEmpty(warning)) sb.Append(' ').Append(SanitizeLdapError(warning));
        return sb.ToString();
    }

    private (bool, string, string?) Fail(string message)
    {
        var sanitized = SanitizeLdapError(message);
        return (false, sanitized, BuildFailureResultJson(sanitized));
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
    /// active / userAccountControl field here by design — those are not trusted and not read. The
    /// enable instruction is the boolean <c>enableOnCreate</c> (UC008-D07, additive, default false):
    /// the agent generates the credential itself.
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
        [JsonPropertyName("enableOnCreate")] public bool EnableOnCreate { get; set; }
        [JsonPropertyName("accountDefinitionId")] public string? AccountDefinitionId { get; set; }
    }

    /// <summary>Production seam: the real <see cref="ActiveDirectorySink"/>, one-to-one, no policy.</summary>
    private sealed class ActiveDirectorySinkCreateAdapter : IAdAccountCreateSink
    {
        private readonly ActiveDirectorySink _sink;
        public ActiveDirectorySinkCreateAdapter(ActiveDirectorySink sink) => _sink = sink;

        public Task<AdCreatePrecheck> PrecheckCreateAsync(string sam, string? upn, CancellationToken ct)
            => _sink.PrecheckCreateAsync(sam, upn, ct);
        public Task<ProvisionResult> CreateAsync(ConnectorObject newObject, CancellationToken ct)
            => _sink.CreateAsync(newObject, ct);
        public Task<Guid?> ReadObjectGuidByDnAsync(string distinguishedName, CancellationToken ct)
            => _sink.ReadObjectGuidByDnAsync(distinguishedName, ct);
        public Task<ProvisionResult> ResetPasswordAsync(string externalId, string newPassword, bool requireChangeAtNextLogin, CancellationToken ct)
            => _sink.ResetPasswordAsync(externalId, newPassword, requireChangeAtNextLogin, ct);
        public Task<SinkWriteResult> SetEnabledByGuidAsync(string objectGuid, bool enabled, CancellationToken ct)
            => _sink.SetEnabledByGuidAsync(objectGuid, enabled, ct);
    }
}

/// <summary>
/// The sink operations <see cref="AdAgentCreateExecutor.ProvisionAsync"/> uses, in the order it uses
/// them. <see cref="ActiveDirectorySink"/> is sealed with non-virtual methods and binds to a live DC, so
/// the post-validation phase runs against this seam: production wraps the real sink, tests drive a
/// fake. The seam carries NO policy — every rule lives in the executor or the sink.
/// </summary>
public interface IAdAccountCreateSink
{
    Task<AdCreatePrecheck> PrecheckCreateAsync(string sam, string? upn, CancellationToken ct);
    Task<ProvisionResult> CreateAsync(ConnectorObject newObject, CancellationToken ct);
    Task<Guid?> ReadObjectGuidByDnAsync(string distinguishedName, CancellationToken ct);
    Task<ProvisionResult> ResetPasswordAsync(string externalId, string newPassword, bool requireChangeAtNextLogin, CancellationToken ct);
    Task<SinkWriteResult> SetEnabledByGuidAsync(string objectGuid, bool enabled, CancellationToken ct);
}
