using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Amazon.IdentityStore.Model;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.Aws;

/// <summary>
/// AWS IAM Identity Center sink — provisions users + groups into the
/// IdentityStore (the directory side of Identity Center). Account/permission-set
/// assignments via SSOAdmin.CreateAccountAssignment are NOT done by this sink in
/// Phase 2; that would require an explicit project mapping
/// (accountId + permissionSetArn) that doesn't fit the generic ConnectorObject
/// shape today. Surface as a deferred follow-up.
///
/// Lookup-then-decide via SourceId (IdentityStore UserId/GroupId) → username
/// fallback. Create with minimal required fields; update via UpdateUser /
/// UpdateGroup with operations array.
/// </summary>
public sealed class AwsSsoSink : IConnectorSink
{
    private readonly Guid _tenantId;
    private readonly CredentialProtector _protector;
    private readonly ILogger<AwsSsoSink> _logger;

    public AwsSsoSink(Guid tenantId, CredentialProtector protector, ILogger<AwsSsoSink> logger)
    {
        _tenantId = tenantId;
        _protector = protector;
        _logger = logger;
    }

    public async Task<SinkWriteResult> UpsertAsync(ConnectorObject obj, CancellationToken cancellationToken)
    {
        try
        {
            var creds = await AwsSsoCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException($"No 'awssso' credential for tenant {_tenantId}.");

            // Phase 4: account assignments take a different path — async submission
            // via SSOAdmin.CreateAccountAssignment, resolved later by the poller.
            if (string.Equals(obj.ObjectClass, AwsSsoAssignmentWriter.ObjectClass, StringComparison.OrdinalIgnoreCase))
            {
                return await AwsSsoAssignmentWriter.UpsertAsync(obj, creds, _logger, cancellationToken);
            }

            var instance = await AwsSsoCredentialReader.ResolveInstanceAsync(creds, cancellationToken)
                ?? throw new InvalidOperationException("No Identity Center instance found in this AWS account/region.");

            using var idStore = AwsSsoCredentialReader.CreateIdentityStoreClient(creds);
            return string.Equals(obj.ObjectClass, "Group", StringComparison.OrdinalIgnoreCase)
                ? await UpsertGroupAsync(idStore, instance.IdentityStoreId, obj, cancellationToken)
                : await UpsertUserAsync(idStore, instance.IdentityStoreId, obj, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AWS Identity Center sink upsert failed for {SourceId}", obj.SourceId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    public async Task<SinkWriteResult> DeleteAsync(string sourceId, CancellationToken cancellationToken)
    {
        try
        {
            var creds = await AwsSsoCredentialReader.ReadAsync(_protector, _tenantId);
            if (creds is null) return SinkWriteResult.Fail("No 'awssso' credential.");
            var instance = await AwsSsoCredentialReader.ResolveInstanceAsync(creds, cancellationToken);
            if (instance is null) return SinkWriteResult.Fail("No Identity Center instance.");
            using var idStore = AwsSsoCredentialReader.CreateIdentityStoreClient(creds);
            try
            {
                await idStore.DeleteUserAsync(new DeleteUserRequest { IdentityStoreId = instance.Value.IdentityStoreId, UserId = sourceId }, cancellationToken);
                return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
            }
            catch (ResourceNotFoundException) { /* fall through to group try */ }
            try
            {
                await idStore.DeleteGroupAsync(new DeleteGroupRequest { IdentityStoreId = instance.Value.IdentityStoreId, GroupId = sourceId }, cancellationToken);
                return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
            }
            catch (ResourceNotFoundException) { return SinkWriteResult.Ok(SinkWriteOutcome.Skipped); }
        }
        catch (Exception ex) { return SinkWriteResult.Fail(ex.Message); }
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var src = new AwsSsoSource(_tenantId, _protector,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AwsSsoSource>.Instance);
        return await src.TestConnectionAsync(cancellationToken);
    }

    private static async Task<SinkWriteResult> UpsertUserAsync(
        Amazon.IdentityStore.AmazonIdentityStoreClient idStore,
        string identityStoreId,
        ConnectorObject obj,
        CancellationToken ct)
    {
        var userName = Str(obj, "userName") ?? Str(obj, "sAMAccountName") ?? Str(obj, "userPrincipalName");
        if (string.IsNullOrWhiteSpace(userName))
            return SinkWriteResult.Fail("Cannot upsert IdentityStore user without userName.");

        // Look up by username.
        string? existingId = null;
        try
        {
            var lookup = await idStore.GetUserIdAsync(new GetUserIdRequest
            {
                IdentityStoreId = identityStoreId,
                AlternateIdentifier = new AlternateIdentifier
                {
                    UniqueAttribute = new UniqueAttribute
                    {
                        AttributePath = "userName",
                        AttributeValue = userName
                    }
                }
            }, ct);
            existingId = lookup.UserId;
        }
        catch (ResourceNotFoundException) { existingId = null; }

        var givenName = Str(obj, "givenName") ?? Str(obj, "firstName");
        var familyName = Str(obj, "familyName") ?? Str(obj, "surname") ?? Str(obj, "sn") ?? "User";
        var displayName = Str(obj, "displayName") ?? userName;
        var email = Str(obj, "email") ?? Str(obj, "mail");

        if (existingId is null)
        {
            var req = new CreateUserRequest
            {
                IdentityStoreId = identityStoreId,
                UserName = userName,
                DisplayName = displayName,
                Name = new Name
                {
                    GivenName = givenName ?? userName,
                    FamilyName = familyName
                }
            };
            if (!string.IsNullOrEmpty(email))
            {
                req.Emails = new List<Email>
                {
                    new() { Value = email, Type = "work", Primary = true }
                };
            }
            await idStore.CreateUserAsync(req, ct);
            return SinkWriteResult.Ok(SinkWriteOutcome.Created);
        }

        // Update via operations array.
        var ops = new List<AttributeOperation>();
        if (displayName is not null) ops.Add(new AttributeOperation { AttributePath = "displayName", AttributeValue = new Amazon.Runtime.Documents.Document(displayName) });
        if (givenName is not null) ops.Add(new AttributeOperation { AttributePath = "name.givenName", AttributeValue = new Amazon.Runtime.Documents.Document(givenName) });
        if (familyName is not null) ops.Add(new AttributeOperation { AttributePath = "name.familyName", AttributeValue = new Amazon.Runtime.Documents.Document(familyName) });
        if (ops.Count > 0)
        {
            await idStore.UpdateUserAsync(new UpdateUserRequest
            {
                IdentityStoreId = identityStoreId,
                UserId = existingId,
                Operations = ops
            }, ct);
        }
        return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
    }

    private static async Task<SinkWriteResult> UpsertGroupAsync(
        Amazon.IdentityStore.AmazonIdentityStoreClient idStore,
        string identityStoreId,
        ConnectorObject obj,
        CancellationToken ct)
    {
        var displayName = Str(obj, "displayName") ?? Str(obj, "cn");
        if (string.IsNullOrWhiteSpace(displayName))
            return SinkWriteResult.Fail("Cannot upsert IdentityStore group without displayName.");

        string? existingId = null;
        try
        {
            var lookup = await idStore.GetGroupIdAsync(new GetGroupIdRequest
            {
                IdentityStoreId = identityStoreId,
                AlternateIdentifier = new AlternateIdentifier
                {
                    UniqueAttribute = new UniqueAttribute
                    {
                        AttributePath = "displayName",
                        AttributeValue = displayName
                    }
                }
            }, ct);
            existingId = lookup.GroupId;
        }
        catch (ResourceNotFoundException) { existingId = null; }

        var description = Str(obj, "description");

        if (existingId is null)
        {
            var created = await idStore.CreateGroupAsync(new CreateGroupRequest
            {
                IdentityStoreId = identityStoreId,
                DisplayName = displayName,
                Description = description
            }, ct);
            if (!string.IsNullOrEmpty(created.GroupId))
                await ReconcileGroupMembershipsAsync(idStore, identityStoreId, created.GroupId, obj, ct);
            return SinkWriteResult.Ok(SinkWriteOutcome.Created);
        }

        var ops = new List<AttributeOperation>();
        if (description is not null)
            ops.Add(new AttributeOperation { AttributePath = "description", AttributeValue = new Amazon.Runtime.Documents.Document(description) });
        if (ops.Count > 0)
        {
            await idStore.UpdateGroupAsync(new UpdateGroupRequest
            {
                IdentityStoreId = identityStoreId,
                GroupId = existingId,
                Operations = ops
            }, ct);
        }

        // Phase 3: reconcile group memberships when the source provided a members list.
        // IdentityStore has no native bulk CreateGroupMembership — we parallelize at 5
        // concurrent requests (well under the 5-TPS account default) and ship the
        // semantic the user asked for (Identity Center group-member bulk).
        await ReconcileGroupMembershipsAsync(idStore, identityStoreId, existingId, obj, ct);

        return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
    }

    private static async Task ReconcileGroupMembershipsAsync(
        Amazon.IdentityStore.AmazonIdentityStoreClient idStore,
        string identityStoreId,
        string groupId,
        ConnectorObject obj,
        CancellationToken ct)
    {
        if (!obj.Attributes.TryGetValue("members", out var raw) || raw is null) return;
        var desired = ExtractMemberIds(raw);
        if (desired is null) return; // no members key vs empty list: skip vs full-clear handled below

        // Snapshot current memberships.
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // memberId → membershipId
        string? nextToken = null;
        do
        {
            var resp = await idStore.ListGroupMembershipsAsync(new ListGroupMembershipsRequest
            {
                IdentityStoreId = identityStoreId,
                GroupId = groupId,
                NextToken = nextToken,
                MaxResults = 100
            }, ct);
            foreach (var m in resp.GroupMemberships)
            {
                if (m.MemberId?.UserId is { Length: > 0 } uid)
                    existing[uid] = m.MembershipId;
            }
            nextToken = resp.NextToken;
        } while (!string.IsNullOrEmpty(nextToken));

        var toAdd = new List<string>();
        foreach (var d in desired)
            if (!existing.ContainsKey(d)) toAdd.Add(d);
        var toRemove = new List<string>();
        foreach (var kv in existing)
            if (!desired.Contains(kv.Key)) toRemove.Add(kv.Value);

        // Parallel adds (cap 5).
        using var addGate = new System.Threading.SemaphoreSlim(5, 5);
        var addTasks = new List<Task>(toAdd.Count);
        foreach (var memberId in toAdd)
        {
            await addGate.WaitAsync(ct);
            addTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await idStore.CreateGroupMembershipAsync(new CreateGroupMembershipRequest
                    {
                        IdentityStoreId = identityStoreId,
                        GroupId = groupId,
                        MemberId = new MemberId { UserId = memberId }
                    }, ct);
                }
                catch (ConflictException) { /* already a member — race or stale snapshot */ }
                finally { addGate.Release(); }
            }, ct));
        }
        await Task.WhenAll(addTasks);

        // Parallel removes (cap 5).
        using var rmGate = new System.Threading.SemaphoreSlim(5, 5);
        var rmTasks = new List<Task>(toRemove.Count);
        foreach (var membershipId in toRemove)
        {
            await rmGate.WaitAsync(ct);
            rmTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await idStore.DeleteGroupMembershipAsync(new DeleteGroupMembershipRequest
                    {
                        IdentityStoreId = identityStoreId,
                        MembershipId = membershipId
                    }, ct);
                }
                catch (ResourceNotFoundException) { /* already gone */ }
                finally { rmGate.Release(); }
            }, ct));
        }
        await Task.WhenAll(rmTasks);
    }

    private static HashSet<string>? ExtractMemberIds(object raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (raw)
        {
            case IEnumerable<string> ss:
                foreach (var s in ss) if (!string.IsNullOrWhiteSpace(s)) set.Add(s);
                break;
            case IEnumerable<object?> os:
                foreach (var o in os)
                {
                    if (o is string s && !string.IsNullOrWhiteSpace(s)) set.Add(s);
                    else if (o is IDictionary<string, object?> d
                        && d.TryGetValue("value", out var v) && v is string vs && !string.IsNullOrWhiteSpace(vs))
                        set.Add(vs);
                }
                break;
            case string single when !string.IsNullOrWhiteSpace(single):
                set.Add(single);
                break;
            default:
                return null;
        }
        return set;
    }

    private static string? Str(ConnectorObject obj, string key) =>
        obj.Attributes.TryGetValue(key, out var v) && v is not null
            ? (v is string s ? (string.IsNullOrEmpty(s) ? null : s) : v.ToString())
            : null;
}
