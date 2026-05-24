using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon;
using Amazon.IdentityStore;
using Amazon.Runtime;
using Amazon.SSOAdmin;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.Aws;

/// <summary>
/// AWS IAM Identity Center (formerly AWS SSO) adapter. Source enumerates users
/// + groups via the IdentityStore API. Sink provisions account assignments via
/// SSO Admin. Distinct from <see cref="AwsAdapter"/> (classic IAM) — different
/// SystemType so a tenant explicitly chooses one or the other at creation time.
///
/// Credentials: { AccessKeyId, SecretAccessKey, Region, IdentityStoreId?, SSOInstanceArn? }.
/// When IdentityStoreId/SSOInstanceArn are blank, the adapter auto-discovers via
/// SSOAdmin.ListInstancesAsync (returns one instance per Identity Center).
/// </summary>
public sealed class AwsSsoAdapter : IConnectorAdapter
{
    public string SystemType => "AWSIdentityCenter";
    public string DisplayName => "AWS IAM Identity Center";
    public bool SupportsSource => true;
    public bool SupportsSink => true;

    public ConnectorCapabilities Capabilities { get; } = new()
    {
        SupportsBulk = false,
        MaxBatchSize = 1,
        SupportsIncremental = false
    };

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes { get; } = new[]
    {
        new CredentialTypeInfo
        {
            Name = "awssso",
            DisplayName = "AWS Identity Center",
            Description = "Access key for AWS IAM Identity Center (formerly AWS SSO). " +
                          "Identity Store ID + SSO Instance ARN auto-discover if blank.",
            Fields = new[]
            {
                new CredentialFieldSpec { Key = "AccessKeyId", Label = "Access Key ID", IsRequired = true },
                new CredentialFieldSpec { Key = "SecretAccessKey", Label = "Secret Access Key", IsRequired = true, IsSecret = true },
                new CredentialFieldSpec { Key = "Region", Label = "Region", Placeholder = "us-east-1", IsRequired = true },
                new CredentialFieldSpec { Key = "IdentityStoreId", Label = "Identity Store ID", Placeholder = "(auto-discover)", Help = "d-xxxxxxxxxx — leave blank to auto-discover via ListInstances." },
                new CredentialFieldSpec { Key = "SSOInstanceArn", Label = "SSO Instance ARN", Placeholder = "(auto-discover)", Help = "arn:aws:sso:::instance/ssoins-... — leave blank to auto-discover." },
            }
        }
    };

    private readonly CredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public AwsSsoAdapter(CredentialProtector protector, ILoggerFactory loggerFactory)
    {
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new AwsSsoSource(tenantId, _protector, _loggerFactory.CreateLogger<AwsSsoSource>());

    public IConnectorSink? CreateSink(Guid tenantId) =>
        new AwsSsoSink(tenantId, _protector, _loggerFactory.CreateLogger<AwsSsoSink>());

    /// <summary>
    /// Phase 4: account-assignment creations / deletions are async. The poller
    /// uses this to advance pending SyncRunAsyncJobs rows.
    /// </summary>
    public IConnectorAsyncJobResolver? CreateAsyncJobResolver(Guid tenantId) =>
        new AwsSsoAsyncJobResolver(tenantId, _protector, _loggerFactory.CreateLogger<AwsSsoAdapter>());
}

internal sealed record AwsSsoCredentials(
    string AccessKeyId,
    string SecretAccessKey,
    string Region,
    string? IdentityStoreId,
    string? SSOInstanceArn);

internal static class AwsSsoCredentialReader
{
    public const string CredentialName = "awssso";

    public static async Task<AwsSsoCredentials?> ReadAsync(CredentialProtector p, Guid tenantId)
    {
        var name = CredentialNameContext.Resolve(CredentialName, CredentialSide.Source);
        var raw = await p.RetrieveAsync(tenantId, name);
        if (string.IsNullOrEmpty(raw))
        {
            var sinkName = CredentialNameContext.Resolve(CredentialName, CredentialSide.Sink);
            if (!string.Equals(sinkName, name, StringComparison.OrdinalIgnoreCase))
                raw = await p.RetrieveAsync(tenantId, sinkName);
        }
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            string? S(string k) => doc.RootElement.TryGetProperty(k, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
            var a = S("AccessKeyId");
            var s = S("SecretAccessKey");
            var r = S("Region");
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(s) || string.IsNullOrEmpty(r)) return null;
            return new AwsSsoCredentials(a!, s!, r!, S("IdentityStoreId"), S("SSOInstanceArn"));
        }
        catch { return null; }
    }

    public static AmazonIdentityStoreClient CreateIdentityStoreClient(AwsSsoCredentials creds)
    {
        var basic = new BasicAWSCredentials(creds.AccessKeyId, creds.SecretAccessKey);
        var region = RegionEndpoint.GetBySystemName(creds.Region);
        return new AmazonIdentityStoreClient(basic, region);
    }

    public static AmazonSSOAdminClient CreateSsoAdminClient(AwsSsoCredentials creds)
    {
        var basic = new BasicAWSCredentials(creds.AccessKeyId, creds.SecretAccessKey);
        var region = RegionEndpoint.GetBySystemName(creds.Region);
        return new AmazonSSOAdminClient(basic, region);
    }

    /// <summary>
    /// Auto-discover SSOInstanceArn + IdentityStoreId via SSOAdmin.ListInstances
    /// when the credential didn't supply them. Identity Center accounts always
    /// host exactly one instance, so the first hit is authoritative.
    /// </summary>
    public static async Task<(string SsoInstanceArn, string IdentityStoreId)?> ResolveInstanceAsync(
        AwsSsoCredentials creds,
        System.Threading.CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(creds.SSOInstanceArn) && !string.IsNullOrEmpty(creds.IdentityStoreId))
            return (creds.SSOInstanceArn!, creds.IdentityStoreId!);
        using var admin = CreateSsoAdminClient(creds);
        var resp = await admin.ListInstancesAsync(new Amazon.SSOAdmin.Model.ListInstancesRequest(), ct);
        if (resp.Instances is null || resp.Instances.Count == 0) return null;
        var inst = resp.Instances[0];
        return (inst.InstanceArn, inst.IdentityStoreId);
    }
}
