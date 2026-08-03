using System;
using System.Collections.Generic;
using System.Net.Http;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.IdentityCenter;

/// <summary>
/// Certification Center (SaaS) adapter — the customer-facing skin over the
/// IdentityCenter connector. It is a THIN adapter: it reuses the existing
/// <see cref="IdentityCenterSink"/> / <see cref="IdentityCenterSource"/> internals
/// byte-for-byte (AES-GCM credential keyring, objectClass forwarding, the
/// Objects/Identities → bulk-endpoint dispatch, TestConnectionAsync). All that
/// differs from <see cref="IdentityCenterAdapter"/> is the presentation: a distinct
/// SystemType + friendly name, and a MINIMAL two-field credential (BaseUrl pre-filled
/// to the SaaS default + ApiKey) with the power-user AgentApiKey field omitted.
///
/// CREDENTIAL REUSE: the credential type Name is the SAME const the reader keys on
/// (<see cref="IdentityCenterCredentialReader.CredentialName"/> = "identitycenter"),
/// so the credential plumbing — store, retrieve, decrypt — is identical to the IC
/// adapter's. The (TenantId, CredentialName) key is per-connection-row, so a
/// Certification Center connection and an IdentityCenter connection never collide:
/// different connection ⇒ different tenantId.
///
/// TARGET TABLE: like the IC adapter, the sink's People/Systems choice is NOT a
/// credential field. The proxy/inbound path stamps <c>IdentityCenterTableContext</c>
/// from the connection's <c>Tenant.TargetTable</c> (the "People to review / Systems to
/// inventory" picker on the connection form). A Sync Project's per-side SinkTable
/// overrides this for the sync path — that is expected and unchanged.
/// </summary>
public sealed class CertificationCenterSaaSAdapter : IConnectorAdapter
{
    /// <summary>The SaaS API default. Pre-filled so a customer normally never touches it.</summary>
    private const string DefaultBaseUrl = "https://api.certification-center.com";

    public string SystemType => "CertificationCenterSaaS";
    public string DisplayName => "Identity Center (SaaS Tenant)";
    public bool SupportsSource => true;
    public bool SupportsSink => true;

    // A lean SINK subset — exactly what the People/Systems flow uses. Bulk upsert
    // (Objects/Identities), delta reads, the inbound-proxy write path (create/update/
    // reversible-delete), and the group-membership second pass (so Systems groups are
    // not dropped by IC's objectClass ingest gate). The EntraID-source governance
    // ingest caps (license / sign-in / usage / app-role) and the workflow person-aware
    // caps are intentionally NOT declared here — they are not part of the branded flow.
    public ConnectorCapabilities Capabilities { get; } = new()
    {
        SupportsBulk = true,
        MaxBatchSize = 500,
        SupportsIncremental = true,
        SupportsCreate = true,
        SupportsUpdate = true,
        SupportsDelete = true,
        SupportsGroupMembership = true
    };

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes { get; } = new[]
    {
        new CredentialTypeInfo
        {
            // Same key the reader resolves — keeps the credential plumbing byte-identical.
            Name = IdentityCenterCredentialReader.CredentialName,
            DisplayName = "Identity Center (SaaS Tenant)",
            Description = "Paste your Identity Center (SaaS Tenant) API key. That's it.",
            Fields = new[]
            {
                new CredentialFieldSpec
                {
                    Key = "BaseUrl", Label = "Service URL", IsRequired = true,
                    DefaultValue = DefaultBaseUrl,
                    Help = "Pre-filled for the standard Identity Center (SaaS Tenant) service. Only change this if you were given a different URL."
                },
                new CredentialFieldSpec
                {
                    Key = "ApiKey", Label = "API Key", IsRequired = true, IsSecret = true,
                    Help = "The key from your Certification Center account. Sent as X-API-Key on every request; stored encrypted, never logged."
                }
            }
        }
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly CredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public CertificationCenterSaaSAdapter(IHttpClientFactory httpFactory, CredentialProtector protector, ILoggerFactory loggerFactory)
    {
        _httpFactory = httpFactory;
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new IdentityCenterSource(tenantId, _httpFactory, _protector, _loggerFactory.CreateLogger<IdentityCenterSource>());

    public IConnectorSink? CreateSink(Guid tenantId) =>
        new IdentityCenterSink(tenantId, _httpFactory, _protector, _loggerFactory.CreateLogger<IdentityCenterSink>());
}
