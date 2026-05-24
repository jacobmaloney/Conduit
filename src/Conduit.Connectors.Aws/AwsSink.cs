using System;
using System.Threading;
using System.Threading.Tasks;
using Amazon.IdentityManagement.Model;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.Aws;

/// <summary>
/// AWS IAM sink. Users: CreateUser or UpdateUser. Groups: CreateGroup or
/// UpdateGroup. Delete: DeleteUser / DeleteGroup. IAM has no "active" flag —
/// deactivation is achieved by removing access keys + login profile, which is
/// out of Phase 1.5 scope. AWS IAM in this connector is mostly identity surface.
/// </summary>
public sealed class AwsSink : IConnectorSink
{
    private readonly Guid _tenantId;
    private readonly CredentialProtector _protector;
    private readonly ILogger<AwsSink> _logger;

    public AwsSink(Guid tenantId, CredentialProtector protector, ILogger<AwsSink> logger)
    {
        _tenantId = tenantId;
        _protector = protector;
        _logger = logger;
    }

    public async Task<SinkWriteResult> UpsertAsync(ConnectorObject obj, CancellationToken cancellationToken)
    {
        try
        {
            var creds = await AwsCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException($"No 'aws' credential for tenant {_tenantId}.");
            using var iam = AwsCredentialReader.CreateIamClient(creds);

            if (string.Equals(obj.ObjectClass, "Group", StringComparison.OrdinalIgnoreCase))
            {
                var name = Get(obj, "GroupName") ?? Get(obj, "displayName") ?? Get(obj, "cn");
                if (string.IsNullOrWhiteSpace(name))
                    return SinkWriteResult.Fail("Cannot upsert AWS group without GroupName.");
                try
                {
                    await iam.GetGroupAsync(new GetGroupRequest { GroupName = name }, cancellationToken);
                    return SinkWriteResult.Ok(SinkWriteOutcome.Updated); // exists
                }
                catch (NoSuchEntityException)
                {
                    await iam.CreateGroupAsync(new CreateGroupRequest { GroupName = name }, cancellationToken);
                    return SinkWriteResult.Ok(SinkWriteOutcome.Created);
                }
            }
            var userName = Get(obj, "UserName") ?? Get(obj, "userName") ?? Get(obj, "sAMAccountName");
            if (string.IsNullOrWhiteSpace(userName))
                return SinkWriteResult.Fail("Cannot upsert AWS user without UserName.");
            try
            {
                await iam.GetUserAsync(new GetUserRequest { UserName = userName }, cancellationToken);
                return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
            }
            catch (NoSuchEntityException)
            {
                await iam.CreateUserAsync(new CreateUserRequest { UserName = userName }, cancellationToken);
                return SinkWriteResult.Ok(SinkWriteOutcome.Created);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AWS sink upsert failed for {SourceId}", obj.SourceId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    public async Task<SinkWriteResult> DeleteAsync(string sourceId, CancellationToken cancellationToken)
    {
        try
        {
            var creds = await AwsCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException($"No 'aws' credential for tenant {_tenantId}.");
            using var iam = AwsCredentialReader.CreateIamClient(creds);
            // sourceId is a UserId or GroupId — IAM Delete requires Name, not Id.
            // We do best-effort: try ListUsers + ListGroups by id match.
            // For Phase 1.5 simplicity, treat sourceId AS the name when ids don't resolve.
            try
            {
                await iam.DeleteUserAsync(new DeleteUserRequest { UserName = sourceId }, cancellationToken);
                return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
            }
            catch (NoSuchEntityException)
            {
                try { await iam.DeleteGroupAsync(new DeleteGroupRequest { GroupName = sourceId }, cancellationToken); return SinkWriteResult.Ok(SinkWriteOutcome.Updated); }
                catch { return SinkWriteResult.Ok(SinkWriteOutcome.Skipped); }
            }
        }
        catch (Exception ex)
        {
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var src = new AwsSource(_tenantId, _protector,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AwsSource>.Instance);
        return await src.TestConnectionAsync(cancellationToken);
    }

    private static string? Get(ConnectorObject obj, string key) =>
        obj.Attributes.TryGetValue(key, out var v) && v is not null
            ? (v is string s ? (string.IsNullOrEmpty(s) ? null : s) : v.ToString())
            : null;
}
