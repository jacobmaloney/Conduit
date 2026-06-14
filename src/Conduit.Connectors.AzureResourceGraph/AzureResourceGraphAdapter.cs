using System;
using System.Collections.Generic;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.AzureResourceGraph;

/// <summary>
/// Azure Resource Graph (ARG) adapter — SOURCE ONLY. Reads Azure subscriptions
/// and resources via the Resource Graph KQL endpoint using raw HttpClient against
/// ARM (no Azure.ResourceManager SDK). App-only ClientSecret credential against
/// ARM; tokens are scoped to https://management.azure.com/.default.
///
/// NEVER emits deletions: a resource absent from one ARG read is NOT proof of
/// deletion (throttling, RBAC gaps, eventual consistency), so the adapter
/// declares SuppressDeleteDetection — same rationale as SQL Discovery.
/// </summary>
public sealed class AzureResourceGraphAdapter : IConnectorAdapter
{
    public string SystemType => "AzureResourceGraph";
    public string DisplayName => "Azure Resource Graph";
    public bool SupportsSource => true;
    public bool SupportsSink => false;

    public ConnectorCapabilities Capabilities { get; } = new()
    {
        SupportsBulk = false,
        MaxBatchSize = 1,
        SupportsIncremental = false,
        // A resource missing from one read is indistinguishable from a throttled /
        // RBAC-blocked / eventually-consistent read. The orchestrator must never
        // diff this source's seen-set into tombstones.
        SuppressDeleteDetection = true
    };

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes { get; } = new[]
    {
        new CredentialTypeInfo
        {
            Name = AzureResourceGraphCredentialReader.CredentialName,
            DisplayName = "Azure Resource Graph App Registration",
            Description = "App-only ClientSecret credential against Azure Resource Manager. The service principal needs the Reader role on the target scope.",
            Fields = new[]
            {
                new CredentialFieldSpec { Key = "TenantId", Label = "Tenant ID", IsRequired = true, Placeholder = "00000000-0000-0000-0000-000000000000" },
                new CredentialFieldSpec { Key = "ClientId", Label = "Client ID (App Registration)", IsRequired = true },
                new CredentialFieldSpec { Key = "ClientSecret", Label = "Client Secret", IsRequired = true, IsSecret = true },
                new CredentialFieldSpec
                {
                    Key = "ScopeFilter", Label = "Scope filter", IsMultiline = true,
                    Help = "Optional. Comma-separated subscription IDs or a management-group id to scope to. Blank = all subscriptions the SP can read."
                },
            }
        }
    };

    private readonly CredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public AzureResourceGraphAdapter(CredentialProtector protector, ILoggerFactory loggerFactory)
    {
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new AzureResourceGraphSource(tenantId, _protector, _loggerFactory.CreateLogger<AzureResourceGraphSource>());

    public IConnectorSink? CreateSink(Guid tenantId) => null;
}
