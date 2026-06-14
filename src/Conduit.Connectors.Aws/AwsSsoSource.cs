using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Amazon.IdentityStore.Model;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;
using SsoAdminModel = Amazon.SSOAdmin.Model;

namespace Conduit.Connectors.Aws;

/// <summary>
/// AWS IAM Identity Center source — enumerates Users + Groups from the
/// IdentityStore API (the SSO-flavored identity store, NOT classic IAM) plus
/// permission sets from the SSO Admin API. SourceId is the IdentityStore
/// UserId / GroupId (for user/group) or the permission-set ARN (for
/// permissionSet). User/Group/permissionSet are explicit branches; an unknown
/// class throws NotSupportedException. A per-class AccessDenied on permission
/// sets is logged as a WARNING and yields nothing rather than aborting the run.
/// </summary>
public sealed class AwsSsoSource : IConnectorSource
{
    private readonly Guid _tenantId;
    private readonly CredentialProtector _protector;
    private readonly ILogger<AwsSsoSource> _logger;

    public AwsSsoSource(Guid tenantId, CredentialProtector protector, ILogger<AwsSsoSource> logger)
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
        var creds = await AwsSsoCredentialReader.ReadAsync(_protector, _tenantId)
            ?? throw new InvalidOperationException($"No 'awssso' credential for tenant {_tenantId}.");

        var instance = await AwsSsoCredentialReader.ResolveInstanceAsync(creds, cancellationToken)
            ?? throw new InvalidOperationException("No Identity Center instance found in this AWS account/region.");

        if (string.Equals(objectClass, "permissionSet", StringComparison.OrdinalIgnoreCase))
        {
            var emittedPs = 0;
            await foreach (var ps in EnumeratePermissionSetsAsync(creds, instance.SsoInstanceArn, cancellationToken))
            {
                if (scope.MaxObjects.HasValue && emittedPs >= scope.MaxObjects.Value) yield break;
                emittedPs++;
                yield return ps;
            }
            yield break;
        }

        if (!string.Equals(objectClass, "user", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(objectClass, "group", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"AWS Identity Center source does not support object class '{objectClass}'. Supported: {string.Join(", ", SupportedClasses)}.");
        }

        using var idStore = AwsSsoCredentialReader.CreateIdentityStoreClient(creds);

        var emitted = 0;
        if (string.Equals(objectClass, "group", StringComparison.OrdinalIgnoreCase))
        {
            string? next = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var req = new ListGroupsRequest { IdentityStoreId = instance.IdentityStoreId, MaxResults = 100, NextToken = next };
                var resp = await idStore.ListGroupsAsync(req, cancellationToken);
                foreach (var g in resp.Groups)
                {
                    if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                    var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["objectClass"] = "group",
                        ["id"] = g.GroupId,
                        ["objectGuid"] = g.GroupId,
                        ["displayName"] = g.DisplayName,
                        ["cn"] = g.DisplayName,
                        ["description"] = g.Description,
                        ["externalId"] = ExtractExternalId(g.ExternalIds)
                    };
                    emitted++;
                    yield return new ConnectorObject
                    {
                        SourceId = g.GroupId,
                        ObjectClass = "Group",
                        Attributes = attrs
                    };
                }
                next = resp.NextToken;
            } while (!string.IsNullOrEmpty(next));
            yield break;
        }

        // Users
        string? uNext = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var req = new ListUsersRequest { IdentityStoreId = instance.IdentityStoreId, MaxResults = 100, NextToken = uNext };
            var resp = await idStore.ListUsersAsync(req, cancellationToken);
            foreach (var u in resp.Users)
            {
                if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["objectClass"] = "user",
                    ["id"] = u.UserId,
                    ["objectGuid"] = u.UserId,
                    ["userName"] = u.UserName,
                    ["sAMAccountName"] = u.UserName,
                    ["displayName"] = u.DisplayName,
                    ["givenName"] = u.Name?.GivenName,
                    ["surname"] = u.Name?.FamilyName,
                    ["sn"] = u.Name?.FamilyName,
                    ["title"] = u.Title,
                    ["jobTitle"] = u.Title,
                    ["preferredLanguage"] = u.PreferredLanguage,
                    ["locale"] = u.Locale,
                    ["timezone"] = u.Timezone,
                    ["externalId"] = ExtractExternalId(u.ExternalIds)
                };
                if (u.Emails is { Count: > 0 })
                {
                    var primary = u.Emails.Find(e => e.Primary == true) ?? u.Emails[0];
                    attrs["email"] = primary.Value;
                    attrs["mail"] = primary.Value;
                }
                if (u.PhoneNumbers is { Count: > 0 })
                {
                    var work = u.PhoneNumbers.Find(p => string.Equals(p.Type, "work", StringComparison.OrdinalIgnoreCase));
                    if (work is not null) attrs["telephoneNumber"] = work.Value;
                    var mobile = u.PhoneNumbers.Find(p => string.Equals(p.Type, "mobile", StringComparison.OrdinalIgnoreCase));
                    if (mobile is not null) attrs["mobilePhone"] = mobile.Value;
                }
                emitted++;
                yield return new ConnectorObject
                {
                    SourceId = u.UserId,
                    ObjectClass = "User",
                    Attributes = attrs
                };
            }
            uNext = resp.NextToken;
        } while (!string.IsNullOrEmpty(uNext));
    }

    /// <summary>The native object classes this source can enumerate.</summary>
    public static readonly string[] SupportedClasses = { "user", "group", "permissionSet" };

    /// <summary>True when this source can enumerate the given class (case-insensitive).</summary>
    public static bool IsSupportedClass(string objectClass)
    {
        foreach (var c in SupportedClasses)
            if (string.Equals(c, objectClass, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Permission sets are listed as ARNs then hydrated one-by-one via Describe.
    // SourceId = the permission-set ARN. A per-class AccessDenied is logged and
    // yields nothing rather than aborting the run.
    private async IAsyncEnumerable<ConnectorObject> EnumeratePermissionSetsAsync(
        AwsSsoCredentials creds, string instanceArn,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var admin = AwsSsoCredentialReader.CreateSsoAdminClient(creds);
        string? next = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            SsoAdminModel.ListPermissionSetsResponse? resp = null;
            try
            {
                resp = await admin.ListPermissionSetsAsync(new SsoAdminModel.ListPermissionSetsRequest
                {
                    InstanceArn = instanceArn,
                    MaxResults = 100,
                    NextToken = next
                }, cancellationToken);
            }
            catch (Amazon.SSOAdmin.Model.AccessDeniedException)
            {
                _logger.LogWarning("AWS Identity Center: skipping class permissionSet — credential lacks sso:ListPermissionSets (AccessDenied).");
                yield break;
            }

            var arns = resp.PermissionSets;
            if (arns is not null)
            {
                foreach (var arn in arns)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SsoAdminModel.DescribePermissionSetResponse? detail = null;
                    try
                    {
                        detail = await admin.DescribePermissionSetAsync(new SsoAdminModel.DescribePermissionSetRequest
                        {
                            InstanceArn = instanceArn,
                            PermissionSetArn = arn
                        }, cancellationToken);
                    }
                    catch (Amazon.SSOAdmin.Model.AccessDeniedException)
                    {
                        _logger.LogWarning("AWS Identity Center: skipping permissionSet {Arn} — credential lacks sso:DescribePermissionSet (AccessDenied).", arn);
                        continue;
                    }

                    var ps = detail.PermissionSet;
                    var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["objectClass"] = "permissionSet",
                        ["id"] = arn,
                        ["objectGuid"] = arn,
                        ["arn"] = arn,
                        ["displayName"] = ps?.Name,
                        ["cn"] = ps?.Name,
                        ["name"] = ps?.Name,
                        ["description"] = ps?.Description,
                        ["sessionDuration"] = ps?.SessionDuration
                    };
                    if (ps?.CreatedDate is { } created) attrs["whenCreated"] = created.ToString("o");
                    yield return new ConnectorObject
                    {
                        SourceId = arn,
                        ObjectClass = "permissionSet",
                        Attributes = attrs
                    };
                }
            }
            next = resp.NextToken;
        } while (!string.IsNullOrEmpty(next));
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var creds = await AwsSsoCredentialReader.ReadAsync(_protector, _tenantId);
            if (creds is null) return new ConnectorTestResult { IsSuccessful = false, Message = "No 'awssso' credential stored." };
            var instance = await AwsSsoCredentialReader.ResolveInstanceAsync(creds, cancellationToken);
            if (instance is null) return new ConnectorTestResult { IsSuccessful = false, Message = "No Identity Center instance found in this AWS account/region." };
            using var idStore = AwsSsoCredentialReader.CreateIdentityStoreClient(creds);
            var probe = await idStore.ListUsersAsync(new ListUsersRequest { IdentityStoreId = instance.Value.IdentityStoreId, MaxResults = 1 }, cancellationToken);
            return new ConnectorTestResult
            {
                IsSuccessful = true,
                Message = $"AWS Identity Center reachable in {creds.Region}. Instance={instance.Value.SsoInstanceArn} IdentityStore={instance.Value.IdentityStoreId}."
            };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

    private static string? ExtractExternalId(List<ExternalId>? ids)
    {
        if (ids is null || ids.Count == 0) return null;
        return ids[0].Id;
    }
}
