using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Conduit.Sync.Security;

namespace Conduit.Connectors.Aws;

/// <summary>
/// AWS IAM write surface for the IC-routed agent-write path. ALL AWS SDK calls
/// live inside THIS assembly (Conduit.Connectors.Aws) — Conduit.Web never touches
/// the AWS SDK. The executor (AwsAgentWriteExecutor) resolves the per-tenant 'aws'
/// credential through the SAME nested AwsCredentialReader the source/sink use, then
/// calls one of these typed, whitelisted methods. There is NO arbitrary-call entry
/// point: only the closed op set below reaches AWS.
///
/// IAM name + ARN shapes are validated server-side here BEFORE any SDK call. The
/// caller (executor) re-validates ARNs and re-checks the privileged-policy guard
/// independently; this writer's validation is the connector-side backstop.
///
/// DEFERRED (deliberately NOT exposed here) — each would persist a secret at rest
/// in the command payload or is otherwise out of the read-mostly governance scope:
///   • CreateUser / DeleteUser           — lifecycle create/delete out of scope.
///   • CreateAccessKey                   — the SDK RETURNS a new secret access key;
///                                         that secret would have to round-trip a
///                                         command payload at rest. Never.
///   • CreateLoginProfile / set console password — the password is a secret in the
///                                         payload at rest. RemoveConsoleAccess
///                                         (DeleteLoginProfile, no secret) DOES ship.
///   • inline policy put/delete (PutUserPolicy etc.) — arbitrary policy-document
///                                         body in the payload; only ATTACH/DETACH
///                                         of an already-existing managed policy ARN
///                                         is allowed.
/// </summary>
public sealed class AwsIamWriter : IDisposable
{
    /// <summary>IAM friendly-name charset (users, groups, tag keys/values within reason).</summary>
    private static readonly Regex IamNameRegex = new(@"^[\w+=,.@-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Tag values may be longer (256) but share the same conservative charset.</summary>
    private static readonly Regex TagValueRegex = new(@"^[\w+=,.@\- ]{0,256}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Access-key IDs are 16-128 uppercase alphanumerics (AKIA…/ASIA…).</summary>
    private static readonly Regex AccessKeyIdRegex = new("^[A-Z0-9]{16,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly AmazonIdentityManagementServiceClient _iam;

    private AwsIamWriter(AmazonIdentityManagementServiceClient iam) => _iam = iam;

    /// <summary>
    /// Builds a writer bound to the resolved per-tenant 'aws' credential. Returns
    /// null (fail-closed) when no credential is registered for the tenant — exactly
    /// the AwsCredentialReader contract the source/sink rely on. The executor maps
    /// null to a fail-closed "no credential mapping" message.
    /// </summary>
    public static async Task<AwsIamWriter?> CreateAsync(CredentialProtector protector, Guid tenantId)
    {
        var creds = await AwsCredentialReader.ReadAsync(protector, tenantId);
        if (creds is null) return null;
        return new AwsIamWriter(AwsCredentialReader.CreateIamClient(creds));
    }

    // ── Server-side validation (connector-side backstop) ─────────────────────

    /// <summary>True when <paramref name="name"/> is a syntactically valid IAM friendly name.</summary>
    public static bool IsValidIamName(string? name) =>
        !string.IsNullOrEmpty(name) && IamNameRegex.IsMatch(name);

    /// <summary>True when <paramref name="accessKeyId"/> is a syntactically valid IAM access-key id.</summary>
    public static bool IsValidAccessKeyId(string? accessKeyId) =>
        !string.IsNullOrEmpty(accessKeyId) && AccessKeyIdRegex.IsMatch(accessKeyId);

    /// <summary>
    /// True when <paramref name="arn"/> parses as an IAM managed-policy ARN: service
    /// "iam" and a resource beginning "policy/" (covers both customer-managed
    /// arn:aws:iam::ACCOUNT:policy/... and AWS-managed arn:aws:iam::aws:policy/...).
    /// </summary>
    public static bool IsValidManagedPolicyArn(string? arn)
    {
        if (string.IsNullOrEmpty(arn) || !Arn.TryParse(arn, out var parsed)) return false;
        if (!string.Equals(parsed.Service, "iam", StringComparison.OrdinalIgnoreCase)) return false;
        if (!parsed.Resource.StartsWith("policy/", StringComparison.OrdinalIgnoreCase)) return false;
        // Only the 'aws' partition and either an AWS-managed policy (account segment
        // empty or "aws") or a real 12-digit-account customer-managed policy. Rejects
        // aws-cn/aws-us-gov and malformed account segments at the boundary.
        if (!string.Equals(parsed.Partition, "aws", StringComparison.OrdinalIgnoreCase)) return false;
        return parsed.AccountId.Length == 0
               || string.Equals(parsed.AccountId, "aws", StringComparison.OrdinalIgnoreCase)
               || (parsed.AccountId.Length == 12 && parsed.AccountId.All(char.IsDigit));
    }

    /// <summary>
    /// True when <paramref name="arn"/> is a CUSTOMER-managed policy (non-empty
    /// 12-digit account segment). Customer-managed policies can grant arbitrary
    /// blast radius and cannot be enumerated, so the executor treats every one as
    /// privileged-by-default (requires the step-up marker).
    /// </summary>
    public static bool IsCustomerManagedPolicyArn(string? arn)
    {
        if (string.IsNullOrEmpty(arn) || !Arn.TryParse(arn, out var parsed)) return false;
        return string.Equals(parsed.Service, "iam", StringComparison.OrdinalIgnoreCase)
               && parsed.Resource.StartsWith("policy/", StringComparison.OrdinalIgnoreCase)
               && parsed.AccountId.Length == 12;
    }

    private static void RequireName(string? name, string field)
    {
        if (!IsValidIamName(name))
            throw new ArgumentException($"{field} is not a valid IAM name.");
    }

    private static void RequirePolicyArn(string? arn)
    {
        if (!IsValidManagedPolicyArn(arn))
            throw new ArgumentException("policyArn is not a valid IAM managed-policy ARN.");
    }

    // ── Tags ──────────────────────────────────────────────────────────────────

    public async Task TagUserAsync(string userName, string tagKey, string tagValue, CancellationToken ct)
    {
        RequireName(userName, "userName");
        if (!IsValidIamName(tagKey)) throw new ArgumentException("tagKey is not a valid IAM tag key.");
        if (tagValue is null || !TagValueRegex.IsMatch(tagValue)) throw new ArgumentException("tagValue is not a valid IAM tag value.");
        await _iam.TagUserAsync(new TagUserRequest
        {
            UserName = userName,
            Tags = { new Tag { Key = tagKey, Value = tagValue } }
        }, ct);
    }

    public async Task UntagUserAsync(string userName, string tagKey, CancellationToken ct)
    {
        RequireName(userName, "userName");
        if (!IsValidIamName(tagKey)) throw new ArgumentException("tagKey is not a valid IAM tag key.");
        await _iam.UntagUserAsync(new UntagUserRequest
        {
            UserName = userName,
            TagKeys = { tagKey }
        }, ct);
    }

    // ── Group membership ────────────────────────────────────────────────────

    public async Task AddUserToGroupAsync(string userName, string groupName, CancellationToken ct)
    {
        RequireName(userName, "userName");
        RequireName(groupName, "groupName");
        await _iam.AddUserToGroupAsync(new AddUserToGroupRequest { UserName = userName, GroupName = groupName }, ct);
    }

    public async Task RemoveUserFromGroupAsync(string userName, string groupName, CancellationToken ct)
    {
        RequireName(userName, "userName");
        RequireName(groupName, "groupName");
        await _iam.RemoveUserFromGroupAsync(new RemoveUserFromGroupRequest { UserName = userName, GroupName = groupName }, ct);
    }

    // ── Managed-policy attach / detach (user or group) ───────────────────────

    public async Task AttachUserPolicyAsync(string userName, string policyArn, CancellationToken ct)
    {
        RequireName(userName, "userName");
        RequirePolicyArn(policyArn);
        await _iam.AttachUserPolicyAsync(new AttachUserPolicyRequest { UserName = userName, PolicyArn = policyArn }, ct);
    }

    public async Task DetachUserPolicyAsync(string userName, string policyArn, CancellationToken ct)
    {
        RequireName(userName, "userName");
        RequirePolicyArn(policyArn);
        await _iam.DetachUserPolicyAsync(new DetachUserPolicyRequest { UserName = userName, PolicyArn = policyArn }, ct);
    }

    public async Task AttachGroupPolicyAsync(string groupName, string policyArn, CancellationToken ct)
    {
        RequireName(groupName, "groupName");
        RequirePolicyArn(policyArn);
        await _iam.AttachGroupPolicyAsync(new AttachGroupPolicyRequest { GroupName = groupName, PolicyArn = policyArn }, ct);
    }

    public async Task DetachGroupPolicyAsync(string groupName, string policyArn, CancellationToken ct)
    {
        RequireName(groupName, "groupName");
        RequirePolicyArn(policyArn);
        await _iam.DetachGroupPolicyAsync(new DetachGroupPolicyRequest { GroupName = groupName, PolicyArn = policyArn }, ct);
    }

    // ── Access-key enable / disable (status flip only — never create) ─────────

    public Task EnableAccessKeyAsync(string userName, string accessKeyId, CancellationToken ct) =>
        UpdateAccessKeyStatusAsync(userName, accessKeyId, StatusType.Active, ct);

    public Task DisableAccessKeyAsync(string userName, string accessKeyId, CancellationToken ct) =>
        UpdateAccessKeyStatusAsync(userName, accessKeyId, StatusType.Inactive, ct);

    private async Task UpdateAccessKeyStatusAsync(string userName, string accessKeyId, StatusType status, CancellationToken ct)
    {
        RequireName(userName, "userName");
        if (!IsValidAccessKeyId(accessKeyId)) throw new ArgumentException("accessKeyId is not a valid IAM access-key id.");
        await _iam.UpdateAccessKeyAsync(new UpdateAccessKeyRequest
        {
            UserName = userName,
            AccessKeyId = accessKeyId,
            Status = status
        }, ct);
    }

    // ── Console access removal (delete login profile — no secret involved) ────

    public async Task RemoveConsoleAccessAsync(string userName, CancellationToken ct)
    {
        RequireName(userName, "userName");
        await _iam.DeleteLoginProfileAsync(new DeleteLoginProfileRequest { UserName = userName }, ct);
    }

    public void Dispose() => _iam.Dispose();
}
