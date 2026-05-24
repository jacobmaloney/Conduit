using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Amazon.IdentityManagement.Model;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.Aws;

/// <summary>
/// AWS IAM source — paged via Marker. Object classes: "User" / "Group". Users
/// emit UserName (= IAM UserName, doubles as SourceId via UserId aria),
/// Arn, Path, CreateDate. Groups emit GroupName + GroupId + Arn + members
/// (via ListGroupsForUser / ListUsersInGroup).
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

        if (string.Equals(objectClass, "Group", StringComparison.OrdinalIgnoreCase))
        {
            string? marker = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var req = new ListGroupsRequest { MaxItems = 100, Marker = marker };
                var resp = await iam.ListGroupsAsync(req, cancellationToken);
                foreach (var g in resp.Groups)
                {
                    if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
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
                    emitted++;
                    yield return new ConnectorObject
                    {
                        SourceId = g.GroupId,
                        ObjectClass = "Group",
                        Attributes = attrs
                    };
                }
                marker = resp.IsTruncated == true ? resp.Marker : null;
            } while (!string.IsNullOrEmpty(marker));
            yield break;
        }

        // Users
        string? userMarker = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var req = new ListUsersRequest { MaxItems = 100, Marker = userMarker };
            var resp = await iam.ListUsersAsync(req, cancellationToken);
            foreach (var u in resp.Users)
            {
                if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
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
                emitted++;
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
