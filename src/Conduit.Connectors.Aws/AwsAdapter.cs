using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon;
using Amazon.IdentityManagement;
using Amazon.Runtime;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.Aws;

/// <summary>
/// AWS IAM adapter — source AND sink (P2 scope: classic IAM users/groups
/// only; IAM Identity Center / SSO support deferred). Credentials:
/// { AccessKeyId, SecretAccessKey, Region }.
/// </summary>
public sealed class AwsAdapter : IConnectorAdapter
{
    public string SystemType => "AWS";
    public string DisplayName => "AWS IAM";
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
            Name = "aws",
            DisplayName = "AWS IAM Access Key",
            Description = "Long-lived AWS access-key pair for classic IAM users + groups.",
            Fields = new[]
            {
                new CredentialFieldSpec { Key = "AccessKeyId", Label = "Access Key ID", IsRequired = true },
                new CredentialFieldSpec { Key = "SecretAccessKey", Label = "Secret Access Key", IsRequired = true, IsSecret = true },
                new CredentialFieldSpec { Key = "Region", Label = "Region", Placeholder = "us-east-1" },
            }
        }
    };

    private readonly CredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public AwsAdapter(CredentialProtector protector, ILoggerFactory loggerFactory)
    {
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new AwsSource(tenantId, _protector, _loggerFactory.CreateLogger<AwsSource>());

    public IConnectorSink? CreateSink(Guid tenantId) =>
        new AwsSink(tenantId, _protector, _loggerFactory.CreateLogger<AwsSink>());
}

internal sealed record AwsCredentials(string AccessKeyId, string SecretAccessKey, string Region);

internal static class AwsCredentialReader
{
    public const string CredentialName = "aws";

    public static async Task<AwsCredentials?> ReadAsync(CredentialProtector p, Guid tenantId)
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
            var a = doc.RootElement.TryGetProperty("AccessKeyId", out var aEl) ? aEl.GetString() : null;
            var s = doc.RootElement.TryGetProperty("SecretAccessKey", out var sEl) ? sEl.GetString() : null;
            var r = doc.RootElement.TryGetProperty("Region", out var rEl) ? rEl.GetString() : null;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(s)) return null;
            return new AwsCredentials(a!, s!, string.IsNullOrEmpty(r) ? "us-east-1" : r!);
        }
        catch { return null; }
    }

    public static AmazonIdentityManagementServiceClient CreateIamClient(AwsCredentials creds)
    {
        var basic = new BasicAWSCredentials(creds.AccessKeyId, creds.SecretAccessKey);
        var region = RegionEndpoint.GetBySystemName(creds.Region);
        return new AmazonIdentityManagementServiceClient(basic, region);
    }
}
