using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.Aws;

/// <summary>
/// AWS IAM source — paged via Marker. Governance-relevant object classes:
/// "user" / "group" / "role" / "policy" / "account". Users emit UserName
/// (= IAM UserName), Arn, Path, CreateDate (SourceId = UserId). Groups emit
/// GroupName + GroupId + Arn + members (SourceId = GroupId). Roles, customer-
/// managed policies, and the account alias mirror the EntraID pattern: a
/// per-class AccessDenied is logged as a WARNING and yields nothing rather than
/// aborting the run; an unknown objectClass throws NotSupportedException so we
/// never silently emit users under the wrong label.
/// </summary>
public sealed class AwsSource : IConnectorSource
{
    private readonly Guid _tenantId;
    private readonly CredentialProtector _protector;
    private readonly ILogger<AwsSource> _logger;

    public AwsSource(Guid tenantId, CredentialProtector protector, ILogger<AwsSource> logger)
    {
        _tenantId = tenantId;
        _protector = protector;
        _logger = logger;
    }

    public async IAsyncEnumerable<ConnectorObject> ReadAsync(
        string objectClass,
        SyncProjectScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var creds = await AwsCredentialReader.ReadAsync(_protector, _tenantId)
            ?? throw new InvalidOperationException($"No 'aws' credential for tenant {_tenantId}.");
        using var iam = AwsCredentialReader.CreateIamClient(creds);
        var emitted = 0;

        var stream = Dispatch(objectClass, iam, cancellationToken);

        await foreach (var obj in stream)
        {
            if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
            emitted++;
            yield return obj;
        }
    }

    /// <summary>
    /// Routes the requested object class to its dedicated enumerator. User/Group are
    /// explicit branches (never the default); role/policy/account are governance
    /// classes each with per-class AccessDenied skip. An unknown class throws
    /// NotSupportedException listing the supported set so we never emit users under
    /// the wrong label.
    /// </summary>
    private IAsyncEnumerable<ConnectorObject> Dispatch(
        string objectClass, AmazonIdentityManagementServiceClient iam, CancellationToken ct)
    {
        if (string.Equals(objectClass, "user", StringComparison.OrdinalIgnoreCase))
            return EnumerateUsersAsync(iam, ct);
        if (string.Equals(objectClass, "group", StringComparison.OrdinalIgnoreCase))
            return EnumerateGroupsAsync(iam, ct);
        if (string.Equals(objectClass, "role", StringComparison.OrdinalIgnoreCase))
            return EnumerateRolesAsync(iam, ct);
        if (string.Equals(objectClass, "policy", StringComparison.OrdinalIgnoreCase))
            return EnumeratePoliciesAsync(iam, ct);
        if (string.Equals(objectClass, "account", StringComparison.OrdinalIgnoreCase))
            return EnumerateAccountAliasesAsync(iam, ct);
        throw new NotSupportedException(
            $"AWS IAM source does not support object class '{objectClass}'. Supported: {string.Join(", ", SupportedClasses)}.");
    }

    /// <summary>The native object classes this source can enumerate.</summary>
    public static readonly string[] SupportedClasses = { "user", "group", "role", "policy", "account" };

    /// <summary>True when this source can enumerate the given class (case-insensitive).</summary>
    public static bool IsSupportedClass(string objectClass)
    {
        foreach (var c in SupportedClasses)
            if (string.Equals(c, objectClass, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private async IAsyncEnumerable<ConnectorObject> EnumerateUsersAsync(
        AmazonIdentityManagementServiceClient iam,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? userMarker = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var req = new ListUsersRequest { MaxItems = 100, Marker = userMarker };
            var resp = await iam.ListUsersAsync(req, cancellationToken);
            foreach (var u in resp.Users)
            {
                var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["objectClass"] = "user",
                    ["id"] = u.UserId,
                    ["objectGuid"] = u.UserId,
                    ["UserName"] = u.UserName,
                    ["userName"] = u.UserName,
                    ["sAMAccountName"] = u.UserName,
                    ["cn"] = u.UserName,
                    ["displayName"] = u.UserName,
                    ["Arn"] = u.Arn,
                    ["Path"] = u.Path,
                    ["whenCreated"] = u.CreateDate?.ToString("o")
                };
                if (u.PasswordLastUsed.HasValue) attrs["lastLogin"] = u.PasswordLastUsed.Value.ToString("o");
                yield return new ConnectorObject
                {
                    SourceId = u.UserId,
                    ObjectClass = "User",
                    Attributes = attrs
                };
            }
            userMarker = resp.IsTruncated == true ? resp.Marker : null;
        } while (!string.IsNullOrEmpty(userMarker));
    }

    private async IAsyncEnumerable<ConnectorObject> EnumerateGroupsAsync(
        AmazonIdentityManagementServiceClient iam,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? marker = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var req = new ListGroupsRequest { MaxItems = 100, Marker = marker };
            var resp = await iam.ListGroupsAsync(req, cancellationToken);
            foreach (var g in resp.Groups)
            {
                var members = await GetGroupMembersAsync(iam, g.GroupName, cancellationToken);
                var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["objectClass"] = "group",
                    ["id"] = g.GroupId,
                    ["objectGuid"] = g.GroupId,
                    ["GroupName"] = g.GroupName,
                    ["displayName"] = g.GroupName,
                    ["cn"] = g.GroupName,
                    ["Arn"] = g.Arn,
                    ["Path"] = g.Path,
                    ["whenCreated"] = g.CreateDate?.ToString("o")
                };
                if (members.Count > 0) attrs["members"] = members;
                yield return new ConnectorObject
                {
                    SourceId = g.GroupId,
                    ObjectClass = "Group",
                    Attributes = attrs
                };
            }
            marker = resp.IsTruncated == true ? resp.Marker : null;
        } while (!string.IsNullOrEmpty(marker));
    }

    // IAM roles. SourceId = RoleId. Per-class AccessDenied skip.
    private async IAsyncEnumerable<ConnectorObject> EnumerateRolesAsync(
        AmazonIdentityManagementServiceClient iam,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? marker = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListRolesResponse? resp = null;
            try
            {
                resp = await iam.ListRolesAsync(new ListRolesRequest { MaxItems = 100, Marker = marker }, cancellationToken);
            }
            catch (AmazonIdentityManagementServiceException ex) when (IsForbidden(ex))
            {
                _logger.LogWarning("AWS IAM: skipping class role — credential lacks iam:ListRoles (403/AccessDenied).");
                yield break;
            }

            foreach (var r in resp.Roles)
            {
                var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["objectClass"] = "role",
                    ["id"] = r.RoleId,
                    ["objectGuid"] = r.RoleId,
                    ["RoleName"] = r.RoleName,
                    ["displayName"] = r.RoleName,
                    ["cn"] = r.RoleName,
                    ["arn"] = r.Arn,
                    ["Path"] = r.Path,
                    ["description"] = r.Description,
                    ["whenCreated"] = r.CreateDate?.ToString("o")
                };
                if (r.MaxSessionDuration.HasValue) attrs["maxSessionDuration"] = r.MaxSessionDuration.Value;
                yield return new ConnectorObject
                {
                    SourceId = r.RoleId,
                    ObjectClass = "role",
                    Attributes = attrs
                };
            }
            marker = resp.IsTruncated == true ? resp.Marker : null;
        } while (!string.IsNullOrEmpty(marker));
    }

    // Customer-managed IAM policies (Scope=Local). SourceId = PolicyId. Per-class
    // AccessDenied skip.
    private async IAsyncEnumerable<ConnectorObject> EnumeratePoliciesAsync(
        AmazonIdentityManagementServiceClient iam,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? marker = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListPoliciesResponse? resp = null;
            try
            {
                resp = await iam.ListPoliciesAsync(
                    new ListPoliciesRequest { Scope = PolicyScopeType.Local, MaxItems = 100, Marker = marker },
                    cancellationToken);
            }
            catch (AmazonIdentityManagementServiceException ex) when (IsForbidden(ex))
            {
                _logger.LogWarning("AWS IAM: skipping class policy — credential lacks iam:ListPolicies (403/AccessDenied).");
                yield break;
            }

            foreach (var p in resp.Policies)
            {
                var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["objectClass"] = "policy",
                    ["id"] = p.PolicyId,
                    ["objectGuid"] = p.PolicyId,
                    ["PolicyName"] = p.PolicyName,
                    ["displayName"] = p.PolicyName,
                    ["cn"] = p.PolicyName,
                    ["arn"] = p.Arn,
                    ["Path"] = p.Path,
                    ["description"] = p.Description,
                    ["whenCreated"] = p.CreateDate?.ToString("o"),
                    ["whenChanged"] = p.UpdateDate?.ToString("o")
                };
                if (p.AttachmentCount.HasValue) attrs["attachmentCount"] = p.AttachmentCount.Value;
                yield return new ConnectorObject
                {
                    SourceId = p.PolicyId,
                    ObjectClass = "policy",
                    Attributes = attrs
                };
            }
            marker = resp.IsTruncated == true ? resp.Marker : null;
        } while (!string.IsNullOrEmpty(marker));
    }

    // Account alias(es). There is at most one alias per account; SourceId = the
    // alias string (or "account" when none is set). Per-class AccessDenied skip.
    private async IAsyncEnumerable<ConnectorObject> EnumerateAccountAliasesAsync(
        AmazonIdentityManagementServiceClient iam,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ListAccountAliasesResponse? resp = null;
        try
        {
            resp = await iam.ListAccountAliasesAsync(new ListAccountAliasesRequest(), cancellationToken);
        }
        catch (AmazonIdentityManagementServiceException ex) when (IsForbidden(ex))
        {
            _logger.LogWarning("AWS IAM: skipping class account — credential lacks iam:ListAccountAliases (403/AccessDenied).");
            yield break;
        }

        var aliases = resp.AccountAliases;
        if (aliases is null || aliases.Count == 0)
        {
            yield return new ConnectorObject
            {
                SourceId = "account",
                ObjectClass = "account",
                Attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["objectClass"] = "account",
                    ["id"] = "account",
                    ["objectGuid"] = "account",
                    ["displayName"] = "(no account alias)",
                    ["cn"] = "(no account alias)"
                }
            };
            yield break;
        }

        foreach (var alias in aliases)
        {
            yield return new ConnectorObject
            {
                SourceId = alias,
                ObjectClass = "account",
                Attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["objectClass"] = "account",
                    ["id"] = alias,
                    ["objectGuid"] = alias,
                    ["accountAlias"] = alias,
                    ["displayName"] = alias,
                    ["cn"] = alias
                }
            };
        }
    }

    private static bool IsForbidden(AmazonIdentityManagementServiceException ex)
    {
        if (ex.StatusCode == HttpStatusCode.Forbidden) return true;
        var code = ex.ErrorCode;
        return string.Equals(code, "AccessDenied", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "UnauthorizedOperation", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var creds = await AwsCredentialReader.ReadAsync(_protector, _tenantId);
            if (creds is null) return new ConnectorTestResult { IsSuccessful = false, Message = "No 'aws' credential stored." };
            using var iam = AwsCredentialReader.CreateIamClient(creds);
            var probe = await iam.ListUsersAsync(new ListUsersRequest { MaxItems = 1 }, cancellationToken);
            return new ConnectorTestResult { IsSuccessful = true, Message = $"AWS IAM reachable in {creds.Region}." };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

    private static async Task<List<string>> GetGroupMembersAsync(Amazon.IdentityManagement.AmazonIdentityManagementServiceClient iam, string groupName, CancellationToken ct)
    {
        var ids = new List<string>();
        string? marker = null;
        do
        {
            var resp = await iam.GetGroupAsync(new GetGroupRequest { GroupName = groupName, MaxItems = 100, Marker = marker }, ct);
            foreach (var u in resp.Users)
                if (!string.IsNullOrEmpty(u.UserId)) ids.Add(u.UserId);
            marker = resp.IsTruncated == true ? resp.Marker : null;
        } while (!string.IsNullOrEmpty(marker));
        return ids;
    }
}
