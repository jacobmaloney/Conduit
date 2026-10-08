using System.Text.Json;
using System.Text.Json.Serialization;
using Conduit.Connectors.Scim;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Security;

namespace Conduit.Web.Services;

/// <summary>
/// Executes an IdentityCenter "ApplyScimWrite" agent command: one lifecycle change to one SCIM
/// user (enable/disable, replace attributes, set manager) routed through this Conduit job
/// server, which holds the per-connection 'scim' credential. IC enqueues the command over its
/// agent-command HTTP API; the poller hands the raw PayloadJson here. ALL SCIM HTTP lives in
/// Conduit.Connectors.Scim (ScimSink) — this class validates the trust boundary and dispatches.
///
/// This class owns ALL trust-boundary enforcement. IC is a SEPARATE trust domain: nothing in the
/// payload is taken on faith — not the operation, not the attribute names, and NOT any credential
/// or tenant id (never carried; the tenant is re-resolved server-side from sourceConnectionName).
///
/// Security constraints enforced here:
///   1  Strict typed parse; >64 KB → hard failure; schemaVersion must be 1.
///   2  operation allow-list (closed set of 4); anything else → fail, no HTTP call.
///   3  attribute names are translated through a CLOSED map (<see cref="ScimAttributePaths"/>);
///      an unknown name fails the whole command by name rather than being dropped silently.
///   4  users only: a group payload is refused (group membership is not in this contract yet).
///   5  credential resolution is Conduit-owned: sourceConnectionName → tenant → 'scim'
///      credential. connectionId in the payload is IGNORED. Fail closed.
///   6  never throws to the caller; never logs the raw payload; SCIM errors are first-line-only.
/// </summary>
public sealed class ScimAgentWriteExecutor
{
    private const int MaxPayloadBytes = 64 * 1024;
    private const int MaxAttributes = 32;

    public const string OpSetAttributes = "SetAttributes";
    public const string OpEnable = "Enable";
    public const string OpDisable = "Disable";
    public const string OpSetManager = "SetManager";

    private static readonly HashSet<string> AllowedOperations = new(StringComparer.Ordinal)
    {
        OpSetAttributes, OpEnable, OpDisable, OpSetManager
    };

    private readonly CredentialProtector _protector;
    private readonly SinkConnectionCredentialMapRepository _credentialMap;
    private readonly TenantRepository _tenants;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ScimAgentWriteExecutor> _logger;

    public ScimAgentWriteExecutor(
        CredentialProtector protector,
        SinkConnectionCredentialMapRepository credentialMap,
        TenantRepository tenants,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory)
    {
        _protector = protector;
        _credentialMap = credentialMap;
        _tenants = tenants;
        _httpFactory = httpFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ScimAgentWriteExecutor>();
    }

    /// <summary>
    /// Validate + execute an ApplyScimWrite payload. Returns (success, message) for the poller's
    /// complete callback. NEVER throws and NEVER logs the raw payload — only command-shaped facts.
    /// Every gate that needs no credential runs before the first repository call.
    /// </summary>
    public async Task<(bool Success, string Message)> ExecuteAsync(Guid commandId, string? payloadJson, CancellationToken ct)
    {
        // ── (1) Strict, size-bounded parse ───────────────────────────────────
        if (string.IsNullOrWhiteSpace(payloadJson))
            return (false, "ApplyScimWrite: empty payload.");
        if (System.Text.Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadBytes)
            return (false, $"ApplyScimWrite: payload exceeds {MaxPayloadBytes / 1024} KB cap.");

        ApplyScimWritePayload? p;
        try
        {
            p = JsonSerializer.Deserialize<ApplyScimWritePayload>(payloadJson, StrictJson);
        }
        catch (JsonException)
        {
            return (false, "ApplyScimWrite: malformed payload JSON.");
        }
        if (p is null)
            return (false, "ApplyScimWrite: payload deserialized to null.");
        if (p.SchemaVersion != 1)
            return (false, $"ApplyScimWrite: unsupported schemaVersion {p.SchemaVersion}.");

        // ── (2) operation allow-list (closed set) — before any HTTP call ──────
        var operation = p.Operation?.Trim();
        if (string.IsNullOrEmpty(operation) || !AllowedOperations.Contains(operation))
            return (false, $"ApplyScimWrite: operation '{p.Operation}' is not allowed.");

        // ── (4) users only ──────────────────────────────────────────────────────
        var objectClass = p.ObjectClass?.Trim();
        if (!string.Equals(objectClass, "user", StringComparison.OrdinalIgnoreCase))
            return (false, $"ApplyScimWrite: objectClass '{p.ObjectClass}' is not supported; only 'user' is.");

        var userExternalId = p.UserExternalId?.Trim();
        if (string.IsNullOrEmpty(userExternalId) || userExternalId.Length > 512)
            return (false, "ApplyScimWrite: userExternalId is missing.");

        // ── (3) attribute names through the closed map — before any credential ──
        Dictionary<string, string?>? scimAttributes = null;
        if (operation == OpSetAttributes)
        {
            if (p.Attributes is null || p.Attributes.Count == 0)
                return (false, "ApplyScimWrite: SetAttributes carries no attributes.");
            if (p.Attributes.Count > MaxAttributes)
                return (false, $"ApplyScimWrite: SetAttributes carries more than {MaxAttributes} attributes.");
            scimAttributes = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (name, value) in p.Attributes)
            {
                if (!ScimAttributePaths.TryMap(name, out var path))
                    return (false, $"ApplyScimWrite: attribute '{name}' has no SCIM mapping and was refused (nothing was written).");
                if (value is { Length: > 4096 })
                    return (false, $"ApplyScimWrite: attribute '{name}' exceeds 4096 characters.");
                scimAttributes[path] = value;
            }
        }
        var managerExternalId = p.ManagerExternalId?.Trim();
        if (operation == OpSetManager && (string.IsNullOrEmpty(managerExternalId) || managerExternalId.Length > 512))
            return (false, "ApplyScimWrite: SetManager carries no managerExternalId.");

        // ── (5) Credential resolution (Conduit owns it) ─────────────────────────
        var sourceConnectionName = p.SourceConnectionName?.Trim();
        if (string.IsNullOrEmpty(sourceConnectionName))
            return (false, "ApplyScimWrite: sourceConnectionName is missing.");

        Guid? resolvedTenantId;
        try
        {
            resolvedTenantId = await _credentialMap.GetTenantIdByNameAsync(sourceConnectionName);
            if (resolvedTenantId is null || resolvedTenantId.Value == Guid.Empty)
                return (false, $"ApplyScimWrite: No Conduit credential mapping for source connection '{sourceConnectionName}'. Run a sync from this connection to register it.");

            var tenant = await _tenants.GetByIdAsync(resolvedTenantId.Value);
            if (tenant is null || !tenant.IsActive)
                return (false, $"ApplyScimWrite: No Conduit credential mapping for source connection '{sourceConnectionName}'. Run a sync from this connection to register it.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A repository fault must come back as a failed command, not escape: an escape skips the
            // completion callback and leaves the command claimed with no outcome.
            _logger.LogError(ex, "ApplyScimWrite {CommandId}: credential mapping lookup failed.", commandId);
            return (false, "ApplyScimWrite: could not resolve the credential mapping for this connection (see Conduit agent log).");
        }

        var sink = new ScimSink(resolvedTenantId.Value, _httpFactory, _protector, _loggerFactory.CreateLogger<ScimSink>());
        try
        {
            var result = operation switch
            {
                OpEnable => await sink.SetActiveAsync(userExternalId, true, ct),
                OpDisable => await sink.SetActiveAsync(userExternalId, false, ct),
                OpSetAttributes => await sink.ReplaceAttributesAsync(userExternalId, scimAttributes!, ct),
                OpSetManager => await sink.AssignManagerAsync(userExternalId, managerExternalId!, ct),
                _ => Conduit.Sync.Connectors.SinkWriteResult.Fail($"operation '{operation}' is not allowed.")
            };
            return result.Outcome != Conduit.Sync.Connectors.SinkWriteOutcome.Failed
                ? (true, Describe(operation, userExternalId, scimAttributes))
                : (false, $"ApplyScimWrite: {operation} failed: {FirstLine(result.ErrorMessage ?? "unknown error")}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never leak payload content or raw SCIM text across the trust boundary.
            _logger.LogError(ex, "ApplyScimWrite {CommandId} ({Operation}) threw.", commandId, operation);
            return (false, $"ApplyScimWrite: {operation} failed (see Conduit agent log).");
        }
    }

    private static string Describe(string operation, string userExternalId, Dictionary<string, string?>? attributes) => operation switch
    {
        OpEnable => $"Enabled SCIM user [{userExternalId}].",
        OpDisable => $"Disabled SCIM user [{userExternalId}].",
        OpSetManager => $"Set manager of SCIM user [{userExternalId}].",
        _ => $"Replaced {attributes?.Count ?? 0} attribute(s) on SCIM user [{userExternalId}]."
    };

    private static string FirstLine(string message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        var idx = message.IndexOf('\n');
        var line = idx >= 0 ? message[..idx] : message;
        line = line.Trim();
        return line.Length > 300 ? line[..300] : line;
    }

    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    /// <summary>
    /// Strict typed model for the IC ApplyScimWrite payload (schemaVersion 1). No secret of any
    /// kind exists here by design; the command NEVER carries a credential or tenant id.
    /// </summary>
    private sealed class ApplyScimWritePayload
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("connectionId")] public string? ConnectionId { get; set; }                // transition only — IGNORED for credential selection
        [JsonPropertyName("sourceConnectionName")] public string? SourceConnectionName { get; set; } // server-resolved IC connection name; the credential selector
        [JsonPropertyName("objectClass")] public string? ObjectClass { get; set; }                  // "user" only
        [JsonPropertyName("operation")] public string? Operation { get; set; }
        [JsonPropertyName("userExternalId")] public string? UserExternalId { get; set; }            // the SCIM id (or userName) IC holds as the object's source id
        [JsonPropertyName("attributes")] public Dictionary<string, string?>? Attributes { get; set; }  // directory attribute names, mapped here
        [JsonPropertyName("managerExternalId")] public string? ManagerExternalId { get; set; }
    }
}

/// <summary>
/// The closed map from the directory attribute names IdentityCenter's engine speaks (the AD
/// vocabulary its Mover and account definitions use) to SCIM 2.0 PatchOp paths. Anything not
/// listed is refused by name; a silent drop would report a change that never reached the target.
/// </summary>
public static class ScimAttributePaths
{
    private const string Enterprise = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["givenName"] = "name.givenName",
        ["sn"] = "name.familyName",
        ["familyName"] = "name.familyName",
        ["displayName"] = "displayName",
        ["title"] = "title",
        ["mail"] = "emails[type eq \"work\"].value",
        ["email"] = "emails[type eq \"work\"].value",
        ["telephoneNumber"] = "phoneNumbers[type eq \"work\"].value",
        ["mobile"] = "phoneNumbers[type eq \"mobile\"].value",
        ["preferredLanguage"] = "preferredLanguage",
        ["userType"] = "userType",
        ["department"] = Enterprise + ":department",
        ["division"] = Enterprise + ":division",
        ["company"] = Enterprise + ":organization",
        ["organization"] = Enterprise + ":organization",
        ["employeeID"] = Enterprise + ":employeeNumber",
        ["employeeNumber"] = Enterprise + ":employeeNumber",
        ["costCenter"] = Enterprise + ":costCenter",
    };

    public static bool TryMap(string? directoryAttribute, out string scimPath)
    {
        scimPath = string.Empty;
        if (string.IsNullOrWhiteSpace(directoryAttribute)) return false;
        if (!Map.TryGetValue(directoryAttribute.Trim(), out var path)) return false;
        scimPath = path;
        return true;
    }

    public static IReadOnlyCollection<string> KnownAttributes => Map.Keys;
}
