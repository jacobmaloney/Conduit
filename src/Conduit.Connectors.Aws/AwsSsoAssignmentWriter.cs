using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SSOAdmin;
using Amazon.SSOAdmin.Model;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.Aws;

/// <summary>
/// Phase 4. AWS Identity Center account assignments — the triple
/// (accountId, permissionSetArn, principalId, principalType?). These are async:
/// CreateAccountAssignment returns AccountAssignmentCreationRequestId; the
/// resolver below polls DescribeAccountAssignmentCreationStatus until the
/// status is SUCCEEDED or FAILED.
///
/// Modeled as ConnectorObject with ObjectClass="Assignment" and these
/// attributes (camelCase to match SCIM-ish conventions used elsewhere):
///   - accountId           — required, 12-digit AWS account id
///   - permissionSetArn    — required, arn:aws:sso:::permissionSet/...
///   - principalId         — required, IdentityStore UserId or GroupId
///   - principalType       — optional, "USER" (default) or "GROUP"
///   - _deleted (bool)     — when true, issues DeleteAccountAssignment instead
/// </summary>
internal static class AwsSsoAssignmentWriter
{
    public const string ObjectClass = "Assignment";
    public const string JobType = "AwsSsoCreateAccountAssignment";
    public const string DeleteJobType = "AwsSsoDeleteAccountAssignment";

    public static async Task<SinkWriteResult> UpsertAsync(
        ConnectorObject obj,
        AwsSsoCredentials creds,
        ILogger logger,
        CancellationToken ct)
    {
        var accountId = Str(obj, "accountId");
        var permissionSetArn = Str(obj, "permissionSetArn");
        var principalId = Str(obj, "principalId");
        var principalTypeStr = Str(obj, "principalType") ?? "USER";

        if (string.IsNullOrWhiteSpace(accountId) ||
            string.IsNullOrWhiteSpace(permissionSetArn) ||
            string.IsNullOrWhiteSpace(principalId))
        {
            return SinkWriteResult.Fail("Assignment requires accountId + permissionSetArn + principalId.");
        }

        var principalType = string.Equals(principalTypeStr, "GROUP", StringComparison.OrdinalIgnoreCase)
            ? PrincipalType.GROUP
            : PrincipalType.USER;

        var instance = await AwsSsoCredentialReader.ResolveInstanceAsync(creds, ct)
            ?? throw new InvalidOperationException("No Identity Center instance found in this AWS account/region.");

        using var admin = AwsSsoCredentialReader.CreateSsoAdminClient(creds);

        // Conduit-side soft idempotency: list existing assignments for the (account,
        // permission set, principal) tuple to skip if already present (synchronous
        // win — no async job created at all).
        try
        {
            var existing = await admin.ListAccountAssignmentsAsync(new ListAccountAssignmentsRequest
            {
                InstanceArn = instance.SsoInstanceArn,
                AccountId = accountId,
                PermissionSetArn = permissionSetArn
            }, ct);
            if (existing.AccountAssignments is not null)
            {
                foreach (var a in existing.AccountAssignments)
                {
                    if (string.Equals(a.PrincipalId, principalId, StringComparison.OrdinalIgnoreCase)
                        && a.PrincipalType == principalType)
                    {
                        return SinkWriteResult.Ok(SinkWriteOutcome.Skipped);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "AwsSsoAssignmentWriter pre-check failed; proceeding to submit assignment.");
        }

        var isDelete = obj.Attributes.TryGetValue("_deleted", out var del) && del is bool b && b;

        if (isDelete)
        {
            var delResp = await admin.DeleteAccountAssignmentAsync(new DeleteAccountAssignmentRequest
            {
                InstanceArn = instance.SsoInstanceArn,
                TargetId = accountId,
                TargetType = TargetType.AWS_ACCOUNT,
                PermissionSetArn = permissionSetArn,
                PrincipalId = principalId,
                PrincipalType = principalType
            }, ct);

            var status = delResp.AccountAssignmentDeletionStatus;
            if (status is null || string.IsNullOrWhiteSpace(status.RequestId))
                return SinkWriteResult.Ok(SinkWriteOutcome.Updated);

            return SinkWriteResult.Pending(new AsyncJobSubmission
            {
                JobId = status.RequestId,
                JobType = DeleteJobType,
                ObjectExternalId = AssignmentKey(accountId, permissionSetArn, principalId, principalType),
                PayloadJson = BuildPayloadJson(instance.SsoInstanceArn, accountId, permissionSetArn, principalId, principalType, isDelete: true)
            });
        }

        var resp = await admin.CreateAccountAssignmentAsync(new CreateAccountAssignmentRequest
        {
            InstanceArn = instance.SsoInstanceArn,
            TargetId = accountId,
            TargetType = TargetType.AWS_ACCOUNT,
            PermissionSetArn = permissionSetArn,
            PrincipalId = principalId,
            PrincipalType = principalType
        }, ct);

        var crStatus = resp.AccountAssignmentCreationStatus;
        if (crStatus is null || string.IsNullOrWhiteSpace(crStatus.RequestId))
        {
            // AWS returned no request id — treat as synchronous create.
            return SinkWriteResult.Ok(SinkWriteOutcome.Created);
        }

        return SinkWriteResult.Pending(new AsyncJobSubmission
        {
            JobId = crStatus.RequestId,
            JobType = JobType,
            ObjectExternalId = AssignmentKey(accountId, permissionSetArn, principalId, principalType),
            PayloadJson = BuildPayloadJson(instance.SsoInstanceArn, accountId, permissionSetArn, principalId, principalType, isDelete: false)
        });
    }

    public static string AssignmentKey(string accountId, string permissionSetArn, string principalId, PrincipalType principalType)
        => $"{accountId}|{permissionSetArn}|{principalType.Value}:{principalId}";

    public static string BuildPayloadJson(string instanceArn, string accountId, string permissionSetArn, string principalId, PrincipalType principalType, bool isDelete)
        => JsonSerializer.Serialize(new
        {
            instanceArn,
            accountId,
            permissionSetArn,
            principalId,
            principalType = principalType.Value,
            isDelete
        });

    private static string? Str(ConnectorObject obj, string key) =>
        obj.Attributes.TryGetValue(key, out var v) && v is not null
            ? (v is string s ? (string.IsNullOrEmpty(s) ? null : s) : v.ToString())
            : null;
}

/// <summary>
/// Resolver for the assignment-creation / deletion request-ids submitted by
/// <see cref="AwsSsoAssignmentWriter"/>. Calls
/// DescribeAccountAssignmentCreationStatus or DescribeAccountAssignmentDeletionStatus
/// and maps the IN_PROGRESS / SUCCEEDED / FAILED enum to AsyncJobState.
/// </summary>
internal sealed class AwsSsoAsyncJobResolver : IConnectorAsyncJobResolver
{
    private readonly Guid _tenantId;
    private readonly CredentialProtector _protector;
    private readonly ILogger _logger;

    public AwsSsoAsyncJobResolver(Guid tenantId, CredentialProtector protector, ILogger logger)
    {
        _tenantId = tenantId;
        _protector = protector;
        _logger = logger;
    }

    public bool CanResolve(string jobType) =>
        jobType == AwsSsoAssignmentWriter.JobType
        || jobType == AwsSsoAssignmentWriter.DeleteJobType;

    public async Task<AsyncJobStatus> PollAsync(string jobId, string jobType, string? payloadJson, CancellationToken ct)
    {
        var creds = await AwsSsoCredentialReader.ReadAsync(_protector, _tenantId);
        if (creds is null) return AsyncJobStatus.Fail("No 'awssso' credential available to poll status.");

        string? instanceArn = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(payloadJson))
            {
                using var doc = JsonDocument.Parse(payloadJson);
                if (doc.RootElement.TryGetProperty("instanceArn", out var ia) && ia.ValueKind == JsonValueKind.String)
                    instanceArn = ia.GetString();
            }
        }
        catch { /* fall through to discovery */ }

        if (string.IsNullOrWhiteSpace(instanceArn))
        {
            var inst = await AwsSsoCredentialReader.ResolveInstanceAsync(creds, ct);
            if (inst is null) return AsyncJobStatus.Fail("No Identity Center instance available.");
            instanceArn = inst.Value.SsoInstanceArn;
        }

        using var admin = AwsSsoCredentialReader.CreateSsoAdminClient(creds);

        if (jobType == AwsSsoAssignmentWriter.DeleteJobType)
        {
            var resp = await admin.DescribeAccountAssignmentDeletionStatusAsync(new DescribeAccountAssignmentDeletionStatusRequest
            {
                InstanceArn = instanceArn,
                AccountAssignmentDeletionRequestId = jobId
            }, ct);
            var s = resp.AccountAssignmentDeletionStatus;
            if (s is null) return AsyncJobStatus.StillPending();
            return MapStatus(s.Status?.Value, s.FailureReason);
        }
        else
        {
            var resp = await admin.DescribeAccountAssignmentCreationStatusAsync(new DescribeAccountAssignmentCreationStatusRequest
            {
                InstanceArn = instanceArn,
                AccountAssignmentCreationRequestId = jobId
            }, ct);
            var s = resp.AccountAssignmentCreationStatus;
            if (s is null) return AsyncJobStatus.StillPending();
            return MapStatus(s.Status?.Value, s.FailureReason);
        }
    }

    private static AsyncJobStatus MapStatus(string? state, string? failureReason)
    {
        // AWS StatusValues enum: IN_PROGRESS | SUCCEEDED | FAILED
        if (string.IsNullOrEmpty(state)) return AsyncJobStatus.StillPending();
        if (state.Equals("SUCCEEDED", StringComparison.OrdinalIgnoreCase)) return AsyncJobStatus.Ok();
        if (state.Equals("FAILED", StringComparison.OrdinalIgnoreCase)) return AsyncJobStatus.Fail(failureReason ?? "AWS reported FAILED with no reason.");
        return AsyncJobStatus.StillPending();
    }
}
