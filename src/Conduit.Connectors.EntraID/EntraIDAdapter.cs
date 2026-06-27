using System;
using System.Collections.Generic;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.EntraID;

/// <summary>
/// Entra ID (Azure AD / Microsoft 365) adapter — source AND sink via Microsoft
/// Graph SDK. Credentials stored under CredentialName="entraid" as JSON
/// { TenantId, ClientId, ClientSecret } and accessed via ClientSecretCredential.
/// </summary>
public sealed class EntraIDAdapter : IConnectorAdapter
{
    public string SystemType => "EntraID";
    public string DisplayName => "Entra ID (Microsoft 365)";
    public bool SupportsSource => true;
    public bool SupportsSink => true;

    /// <summary>
    /// Phase 3: Graph $batch (hard 20-per-batch limit) wired on the sink for the
    /// happy path (PATCH against existing SourceId). Delta cursor wired on the
    /// source via Users.Delta / Groups.Delta with @odata.deltaLink persistence.
    /// </summary>
    public ConnectorCapabilities Capabilities { get; } = new()
    {
        SupportsBulk = true,
        MaxBatchSize = 20,
        SupportsIncremental = true,
        // Phase 7: Graph Manager + Owner $ref endpoints. No Person concept in Entra.
        SupportsAssignManager = true,
        SupportsAssignGroupOwner = true,
        // Phase 5: Entra supports user create (Users.PostAsync) and password reset
        // (PATCH with PasswordProfile). Move stays unsupported — no OU concept.
        SupportsCreate = true,
        // Phase 2 inbound proxy: Entra's UpsertAsync PATCHes against the existing
        // object id/UPN, so the default UpdateViaUpsert delegate is a genuine Graph
        // PATCH. Graph PATCH is natively PARTIAL, so PUT is honored as a partial
        // merge of the supplied attributes (omitted attributes are left untouched).
        SupportsUpdate = true,
        SupportsMove = false,
        SupportsResetPassword = true
    };

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes { get; } = new[]
    {
        new CredentialTypeInfo
        {
            Name = "entraid",
            DisplayName = "Entra ID App Registration",
            Description = "App-only ClientSecret credential against a Microsoft 365 tenant.",
            Fields = new[]
            {
                new CredentialFieldSpec { Key = "TenantId", Label = "Tenant ID", IsRequired = true, Placeholder = "00000000-0000-0000-0000-000000000000" },
                new CredentialFieldSpec { Key = "ClientId", Label = "Client ID (App Registration)", IsRequired = true },
                new CredentialFieldSpec { Key = "ClientSecret", Label = "Client Secret", IsRequired = true, IsSecret = true },
            }
        }
    };

    private readonly CredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public EntraIDAdapter(CredentialProtector protector, ILoggerFactory loggerFactory)
    {
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new EntraIDSource(tenantId, _protector, _loggerFactory.CreateLogger<EntraIDSource>());

    public IConnectorSink? CreateSink(Guid tenantId) =>
        new EntraIDSink(tenantId, _protector, _loggerFactory.CreateLogger<EntraIDSink>());
}
