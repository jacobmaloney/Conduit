using System.Text.Json;
using System.Text.Json.Serialization;
using Conduit.Connectors.Aws;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Security;

namespace Conduit.Web.Services;

/// <summary>
/// Executes an IdentityCenter "ApplyAwsWrite" agent command: a single, whitelisted
/// AWS IAM change (tag, group membership, managed-policy attach/detach, access-key
/// status flip, console-access removal) routed through this Conduit job server,
/// which holds the per-connection 'aws' access-key credential. IC enqueues the
/// command over its agent-command HTTP API; the poller hands the raw PayloadJson
/// here. ALL AWS SDK calls live in Conduit.Connectors.Aws (AwsIamWriter) — this
/// class never touches the AWS SDK; it validates the trust boundary and dispatches.
///
/// This class owns ALL trust-boundary enforcement. IC is a SEPARATE trust domain:
/// nothing in the payload is taken on faith — not the operation, not the names,
/// not the policy ARN, and NOT any credential or tenant id (those are NEVER carried
/// in the command; the tenant is re-resolved server-side from sourceConnectionName).
///
/// Security constraints enforced here:
///   1  Strict typed parse; >64 KB → hard failure; schemaVersion must be 1.
///   2  operation allow-list (closed set of 9); anything else → fail, no SDK call.
///   3  IAM name + managed-policy ARN validation server-side (connector re-checks).
///   4  credential resolution is Conduit-owned: sourceConnectionName → tenant →
///      'aws' credential. connectionId in the payload is IGNORED. Fail closed.
///   5  privileged-policy backstop: attaching a well-known AWS-managed admin ARN
///      (AdministratorAccess / PowerUserAccess / IAMFullAccess) OR ANY customer-
///      managed policy (unbounded, non-enumerable blast radius) is refused unless
///      the payload carries privileged == true, a flag the IC privileged path sets
///      ONLY AFTER its own capability gate + step-up. The executor never trusts the
///      client for the gate itself — it is the independent backstop that REQUIRES
///      the marker for high-blast-radius attaches (user OR group), and tags the
///      audit message [PRIVILEGED GRANT].
///   6  never throws to the caller; never logs the raw payload; AWS errors are
///      first-line-only and never carry a credential.
///
/// PRIVILEGED-ATTACH MARKER PAYLOAD KEY: "privileged" (bool, default false). The IC
/// build MUST set "privileged": true on the AttachManagedPolicy command (and only
/// that command) after passing its privileged capability check + step-up, OR the
/// attach of Administrator/PowerUser/IAMFullAccess is refused here.
/// </summary>
public sealed class AwsAgentWriteExecutor
{
    private const int MaxPayloadBytes = 64 * 1024;

    private const string OpTagUser = "TagUser";
    private const string OpUntagUser = "UntagUser";
    private const string OpAddGroupMember = "AddGroupMember";
    private const string OpRemoveGroupMember = "RemoveGroupMember";
    private const string OpAttachManagedPolicy = "AttachManagedPolicy";
    private const string OpDetachManagedPolicy = "DetachManagedPolicy";
    private const string OpEnableAccessKey = "EnableAccessKey";
    private const string OpDisableAccessKey = "DisableAccessKey";
    private const string OpRemoveConsoleAccess = "RemoveConsoleAccess";

    private static readonly HashSet<string> AllowedOperations = new(StringComparer.Ordinal)
    {
        OpTagUser, OpUntagUser, OpAddGroupMember, OpRemoveGroupMember,
        OpAttachManagedPolicy, OpDetachManagedPolicy,
        OpEnableAccessKey, OpDisableAccessKey, OpRemoveConsoleAccess
    };

    /// <summary>
    /// High-blast-radius AWS-managed policy ARNs. Attaching any of these is refused
    /// unless the command carries the privileged marker (set only by IC's privileged
    /// path after step-up). Defense in depth — the executor never trusts the client
    /// for the gate, only for the marker that the gated path produced.
    /// </summary>
    private static readonly HashSet<string> PrivilegedPolicyArns = new(StringComparer.OrdinalIgnoreCase)
    {
        "arn:aws:iam::aws:policy/AdministratorAccess",
        "arn:aws:iam::aws:policy/PowerUserAccess",
        "arn:aws:iam::aws:policy/IAMFullAccess",
    };

    private readonly CredentialProtector _protector;
    private readonly SinkConnectionCredentialMapRepository _credentialMap;
    private readonly TenantRepository _tenants;
    private readonly ILogger<AwsAgentWriteExecutor> _logger;

    public AwsAgentWriteExecutor(
        CredentialProtector protector,
        SinkConnectionCredentialMapRepository credentialMap,
        TenantRepository tenants,
        ILogger<AwsAgentWriteExecutor> logger)
    {
        _protector = protector;
        _credentialMap = credentialMap;
        _tenants = tenants;
        _logger = logger;
    }

    /// <summary>
    /// Validate + execute an ApplyAwsWrite payload. Returns (success, message) for
    /// the poller's complete callback. NEVER throws to the caller and NEVER logs the
    /// raw payload body — only command-shaped facts (operation + result).
    /// </summary>
    public async Task<(bool Success, string Message)> ExecuteAsync(Guid commandId, string? payloadJson, CancellationToken ct)
    {
        // ── (1) Strict, size-bounded parse ───────────────────────────────────
        if (string.IsNullOrWhiteSpace(payloadJson))
            return (false, "ApplyAwsWrite: empty payload.");
        if (System.Text.Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadBytes)
            return (false, $"ApplyAwsWrite: payload exceeds {MaxPayloadBytes / 1024} KB cap.");

        ApplyAwsWritePayload? p;
        try
        {
            p = JsonSerializer.Deserialize<ApplyAwsWritePayload>(payloadJson, StrictJson);
        }
        catch (JsonException)
        {
            return (false, "ApplyAwsWrite: malformed payload JSON.");
        }
        if (p is null)
            return (false, "ApplyAwsWrite: payload deserialized to null.");

        if (p.SchemaVersion != 1)
            return (false, $"ApplyAwsWrite: unsupported schemaVersion {p.SchemaVersion}.");

        // ── (2) operation allow-list (closed set) — before any SDK call ──────
        var operation = p.Operation?.Trim();
        if (string.IsNullOrEmpty(operation) || !AllowedOperations.Contains(operation))
            return (false, $"ApplyAwsWrite: operation '{p.Operation}' is not allowed.");

        // ── Credential resolution (Conduit owns it) ─────────────────────────────
        // The caller-supplied connectionId is IGNORED for credential selection.
        // We resolve the Conduit tenant whose 'aws' credential backs this write from
        // the server-trusted sourceConnectionName via the orchestrator-owned mapping.
        // No mapping / disabled tenant / missing credential → fail closed; never fall
        // back to connectionId or a default tenant.
        var sourceConnectionName = p.SourceConnectionName?.Trim();
        if (string.IsNullOrEmpty(sourceConnectionName))
            return (false, "ApplyAwsWrite: sourceConnectionName is missing.");

        var resolvedTenantId = await _credentialMap.GetTenantIdByNameAsync(sourceConnectionName);
        if (resolvedTenantId is null || resolvedTenantId.Value == Guid.Empty)
            return (false, $"ApplyAwsWrite: No Conduit credential mapping for source connection '{sourceConnectionName}'. Run a sync from this connection to register it.");

        var tenant = await _tenants.GetByIdAsync(resolvedTenantId.Value);
        if (tenant is null || !tenant.IsActive)
            return (false, $"ApplyAwsWrite: No Conduit credential mapping for source connection '{sourceConnectionName}'. Run a sync from this connection to register it.");

        // ── Load the 'aws' credential for the resolved tenant (same blob the source uses) ──
        AwsIamWriter? writer;
        try
        {
            writer = await AwsIamWriter.CreateAsync(_protector, resolvedTenantId.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyAwsWrite {CommandId}: failed reading aws credential.", commandId);
            return (false, "ApplyAwsWrite: could not read the AWS credential for this connection.");
        }
        if (writer is null)
            return (false, $"ApplyAwsWrite: No Conduit credential mapping for source connection '{sourceConnectionName}'. Run a sync from this connection to register it.");

        using (writer)
        {
            try
            {
                return operation switch
                {
                    OpTagUser => await DoTagUserAsync(writer, p, ct),
                    OpUntagUser => await DoUntagUserAsync(writer, p, ct),
                    OpAddGroupMember => await DoMembershipAsync(writer, p, add: true, ct),
                    OpRemoveGroupMember => await DoMembershipAsync(writer, p, add: false, ct),
                    OpAttachManagedPolicy => await DoManagedPolicyAsync(writer, p, attach: true, ct),
                    OpDetachManagedPolicy => await DoManagedPolicyAsync(writer, p, attach: false, ct),
                    OpEnableAccessKey => await DoAccessKeyAsync(writer, p, enable: true, ct),
                    OpDisableAccessKey => await DoAccessKeyAsync(writer, p, enable: false, ct),
                    OpRemoveConsoleAccess => await DoRemoveConsoleAccessAsync(writer, p, ct),
                    _ => (false, $"ApplyAwsWrite: operation '{operation}' is not allowed.")
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ArgumentException ex)
            {
                // Server-side validation failure from the writer — a clean reject.
                // The message is our own ("... is not a valid ..."), never AWS text.
                return (false, $"ApplyAwsWrite: {operation} rejected: {FirstLine(ex.Message)}");
            }
            catch (Exception ex)
            {
                // Never leak payload content OR AWS error text (IAM messages carry the
                // calling principal ARN + account id across the trust boundary). Detail
                // stays in the local log; IC gets a generic per-operation failure.
                _logger.LogError(ex, "ApplyAwsWrite {CommandId} ({Operation}) threw.", commandId, operation);
                return (false, $"ApplyAwsWrite: {operation} failed (see Conduit agent log).");
            }
        }
    }

    // ── TagUser / UntagUser ──────────────────────────────────────────────────
    private async Task<(bool, string)> DoTagUserAsync(AwsIamWriter writer, ApplyAwsWritePayload p, CancellationToken ct)
    {
        if (!AwsIamWriter.IsValidIamName(p.UserName)) return (false, "ApplyAwsWrite: userName is not a valid IAM name.");
        if (string.IsNullOrEmpty(p.TagKey)) return (false, "ApplyAwsWrite: tagKey is required.");
        await writer.TagUserAsync(p.UserName!, p.TagKey!, p.TagValue ?? string.Empty, ct);
        return (true, $"Tagged IAM user [{p.UserName}] ({p.TagKey}).");
    }

    private async Task<(bool, string)> DoUntagUserAsync(AwsIamWriter writer, ApplyAwsWritePayload p, CancellationToken ct)
    {
        if (!AwsIamWriter.IsValidIamName(p.UserName)) return (false, "ApplyAwsWrite: userName is not a valid IAM name.");
        if (string.IsNullOrEmpty(p.TagKey)) return (false, "ApplyAwsWrite: tagKey is required.");
        await writer.UntagUserAsync(p.UserName!, p.TagKey!, ct);
        return (true, $"Untagged IAM user [{p.UserName}] ({p.TagKey}).");
    }

    // ── Add / Remove group member ─────────────────────────────────────────────
    private async Task<(bool, string)> DoMembershipAsync(AwsIamWriter writer, ApplyAwsWritePayload p, bool add, CancellationToken ct)
    {
        var verb = add ? "AddGroupMember" : "RemoveGroupMember";
        if (!AwsIamWriter.IsValidIamName(p.UserName)) return (false, $"ApplyAwsWrite: {verb}: userName is not a valid IAM name.");
        if (!AwsIamWriter.IsValidIamName(p.GroupName)) return (false, $"ApplyAwsWrite: {verb}: groupName is not a valid IAM name.");

        if (add) await writer.AddUserToGroupAsync(p.UserName!, p.GroupName!, ct);
        else await writer.RemoveUserFromGroupAsync(p.UserName!, p.GroupName!, ct);

        return (true, add
            ? $"Added IAM user [{p.UserName}] to group [{p.GroupName}]."
            : $"Removed IAM user [{p.UserName}] from group [{p.GroupName}].");
    }

    // ── Attach / Detach managed policy (user OR group, by objectClass) ─────────
    private async Task<(bool, string)> DoManagedPolicyAsync(AwsIamWriter writer, ApplyAwsWritePayload p, bool attach, CancellationToken ct)
    {
        var verb = attach ? "AttachManagedPolicy" : "DetachManagedPolicy";
        if (!AwsIamWriter.IsValidManagedPolicyArn(p.PolicyArn))
            return (false, $"ApplyAwsWrite: {verb}: policyArn is not a valid IAM managed-policy ARN.");

        // (5) Independent privileged-attach backstop — re-checked here, NOT trusted
        // from the client. Privileged = a well-known AWS-managed admin ARN OR ANY
        // customer-managed policy (unbounded blast radius, can't be enumerated).
        // Either requires the privileged marker that IC's gated step-up path sets.
        // Detach is never gated.
        var isPrivilegedAttach = attach && IsPrivilegedAttach(p.PolicyArn!);
        if (isPrivilegedAttach && p.Privileged != true)
            return (false, $"ApplyAwsWrite: AttachManagedPolicy of '{p.PolicyArn}' requires a privileged (step-up) command.");

        var objectClass = p.ObjectClass?.Trim();
        var isGroup = string.Equals(objectClass, "Group", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(objectClass, "group", StringComparison.Ordinal);
        var isUser = string.Equals(objectClass, "User", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(objectClass, "user", StringComparison.Ordinal);
        if (!isGroup && !isUser)
            return (false, $"ApplyAwsWrite: {verb}: objectClass must be 'User' or 'Group'.");

        if (isGroup)
        {
            if (!AwsIamWriter.IsValidIamName(p.GroupName)) return (false, $"ApplyAwsWrite: {verb}: groupName is not a valid IAM name.");
            if (attach) await writer.AttachGroupPolicyAsync(p.GroupName!, p.PolicyArn!, ct);
            else await writer.DetachGroupPolicyAsync(p.GroupName!, p.PolicyArn!, ct);
            var groupNote = isPrivilegedAttach ? " [PRIVILEGED GRANT]" : string.Empty;
            return (true, $"{(attach ? "Attached" : "Detached")} policy [{p.PolicyArn}] {(attach ? "to" : "from")} IAM group [{p.GroupName}].{groupNote}");
        }

        if (!AwsIamWriter.IsValidIamName(p.UserName)) return (false, $"ApplyAwsWrite: {verb}: userName is not a valid IAM name.");
        if (attach) await writer.AttachUserPolicyAsync(p.UserName!, p.PolicyArn!, ct);
        else await writer.DetachUserPolicyAsync(p.UserName!, p.PolicyArn!, ct);
        var note = isPrivilegedAttach ? " [PRIVILEGED GRANT]" : string.Empty;
        return (true, $"{(attach ? "Attached" : "Detached")} policy [{p.PolicyArn}] {(attach ? "to" : "from")} IAM user [{p.UserName}].{note}");
    }

    // ── Enable / Disable access key ────────────────────────────────────────────
    private async Task<(bool, string)> DoAccessKeyAsync(AwsIamWriter writer, ApplyAwsWritePayload p, bool enable, CancellationToken ct)
    {
        var verb = enable ? "EnableAccessKey" : "DisableAccessKey";
        if (!AwsIamWriter.IsValidIamName(p.UserName)) return (false, $"ApplyAwsWrite: {verb}: userName is not a valid IAM name.");
        if (!AwsIamWriter.IsValidAccessKeyId(p.AccessKeyId)) return (false, $"ApplyAwsWrite: {verb}: accessKeyId is not valid.");

        if (enable) await writer.EnableAccessKeyAsync(p.UserName!, p.AccessKeyId!, ct);
        else await writer.DisableAccessKeyAsync(p.UserName!, p.AccessKeyId!, ct);

        return (true, $"{(enable ? "Enabled" : "Disabled")} access key for IAM user [{p.UserName}].");
    }

    // ── RemoveConsoleAccess (delete login profile — never create) ──────────────
    private async Task<(bool, string)> DoRemoveConsoleAccessAsync(AwsIamWriter writer, ApplyAwsWritePayload p, CancellationToken ct)
    {
        if (!AwsIamWriter.IsValidIamName(p.UserName)) return (false, "ApplyAwsWrite: userName is not a valid IAM name.");
        await writer.RemoveConsoleAccessAsync(p.UserName!, ct);
        return (true, $"Removed console access for IAM user [{p.UserName}].");
    }

    /// <summary>
    /// True when attaching <paramref name="policyArn"/> is privileged and therefore
    /// requires the step-up marker: a well-known AWS-managed admin ARN, OR any
    /// customer-managed policy (unbounded, non-enumerable blast radius).
    /// </summary>
    private static bool IsPrivilegedAttach(string policyArn) =>
        PrivilegedPolicyArns.Contains(policyArn.Trim())
        || AwsIamWriter.IsCustomerManagedPolicyArn(policyArn);

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
    /// Strict typed model for the IC ApplyAwsWrite payload (schemaVersion 1). NO
    /// secret / password / access-key-secret / token / session-token field exists
    /// here by design — access-key CREATION and console-password set are deferred
    /// (they would persist a secret at rest in the command payload). The command
    /// NEVER carries a credential or tenant id; the tenant is re-resolved server-
    /// side from sourceConnectionName.
    /// </summary>
    private sealed class ApplyAwsWritePayload
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("connectionId")] public string? ConnectionId { get; set; }   // transition only — IGNORED for credential selection
        [JsonPropertyName("sourceConnectionName")] public string? SourceConnectionName { get; set; }  // server-resolved IC connection name; the credential selector
        [JsonPropertyName("objectClass")] public string? ObjectClass { get; set; }     // "User" | "Group" — selects user-vs-group attach/detach
        [JsonPropertyName("operation")] public string? Operation { get; set; }
        [JsonPropertyName("userName")] public string? UserName { get; set; }
        [JsonPropertyName("groupName")] public string? GroupName { get; set; }
        [JsonPropertyName("policyArn")] public string? PolicyArn { get; set; }
        [JsonPropertyName("tagKey")] public string? TagKey { get; set; }
        [JsonPropertyName("tagValue")] public string? TagValue { get; set; }
        [JsonPropertyName("accessKeyId")] public string? AccessKeyId { get; set; }
        [JsonPropertyName("privileged")] public bool? Privileged { get; set; }          // set TRUE only by IC's privileged path after capability gate + step-up
    }
}
