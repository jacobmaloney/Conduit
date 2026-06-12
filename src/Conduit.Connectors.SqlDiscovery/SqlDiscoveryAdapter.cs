using System;
using System.Collections.Generic;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.SqlDiscovery;

/// <summary>
/// SQL Server Discovery adapter — SOURCE ONLY. Enumerates SQL Server hosts
/// (AD MSSQLSvc/* SPNs and/or a manual instance list), scans each instance for
/// license-relevant facts, and emits them as "computer" objects for the
/// IdentityCenter sink (/api/objects/bulk).
///
/// NEVER emits deletions: a failed scan is indistinguishable from a
/// decommission, so the adapter declares SuppressDeleteDetection and the
/// source never reports a complete read. Conduit stores no scan results —
/// everything flows straight through the pump to the sink.
/// </summary>
public sealed class SqlDiscoveryAdapter : IConnectorAdapter
{
    public string SystemType => "SqlDiscovery";
    public string DisplayName => "SQL Server Discovery";
    public bool SupportsSource => true;
    public bool SupportsSink => false;

    public ConnectorCapabilities Capabilities { get; } = new()
    {
        SupportsBulk = false,
        MaxBatchSize = 1,
        SupportsIncremental = false,
        // Failed scans are findings, not decommissions. The orchestrator must
        // never diff this source's seen-set into tombstones.
        SuppressDeleteDetection = true,
        // Per-attempt timestamps change every sweep even when the server itself
        // didn't. Excluding them from the sink-side skip-unchanged content hash
        // lets a stable server skip re-ingest while edition/database/login/status
        // changes still flow. Scoped to THIS source — other connectors' hashing
        // is untouched.
        HashVolatileAttributes = new[] { "sqlLastScannedAt", "sqlLastScanAttemptAt" }
    };

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes { get; } = new[]
    {
        new CredentialTypeInfo
        {
            Name = SqlDiscoveryConfigReader.CredentialName,
            DisplayName = "SQL Discovery Configuration",
            Description = "Enumeration mode, scan credential, and instance list. " +
                          "Leave the project's attribute mappings EMPTY (pass-through) — the emission contract is fixed.",
            Fields = new[]
            {
                new CredentialFieldSpec
                {
                    Key = "Mode", Label = "Enumeration mode", IsRequired = true,
                    AllowedValues = new[] { "SPN", "InstanceList", "Both" },
                    DefaultValue = "SPN",
                    Help = "SPN = LDAP query (servicePrincipalName=MSSQLSvc/*) via an existing Active Directory connection. InstanceList = the manual list below. Both = union (SPN identity wins on overlap)."
                },
                new CredentialFieldSpec
                {
                    Key = "AdConnectionName", Label = "AD connection name",
                    Placeholder = "domain.local2",
                    Help = "Name of an existing Active Directory Connected System whose host + ldap credential drive SPN enumeration. Required for SPN / Both."
                },
                new CredentialFieldSpec
                {
                    Key = "InstanceList", Label = "Manual instance list", IsMultiline = true,
                    Placeholder = "192.168.1.56\nsql01.corp.local\\PAYROLL\nsql02.corp.local,1455",
                    Help = "One entry per line: host[\\instance][,port]. For servers not joined to (or discoverable via) AD."
                },
                new CredentialFieldSpec
                {
                    Key = "AuthType", Label = "Scan authentication",
                    AllowedValues = new[] { "SqlAuth", "WindowsAuth" },
                    DefaultValue = "SqlAuth",
                    Help = "SqlAuth uses the username/password below on every instance. WindowsAuth uses the Conduit service account (integrated security)."
                },
                new CredentialFieldSpec { Key = "SqlUsername", Label = "SQL username", Placeholder = "sa" },
                new CredentialFieldSpec { Key = "SqlPassword", Label = "SQL password", IsSecret = true },
                new CredentialFieldSpec
                {
                    Key = "TrustServerCertificate", Label = "Trust server certificate", IsBoolean = true,
                    DefaultValue = "false",
                    Help = "OFF by default: SQL auth scans require encryption (Encrypt=Mandatory) with a validated certificate so a rogue discovered host cannot harvest the scan credential. Turn ON only for labs / estates with self-signed certificates."
                },
                new CredentialFieldSpec
                {
                    Key = "CollectLogins", Label = "Collect server logins", IsBoolean = true,
                    DefaultValue = "true",
                    Help = "Read sys.server_principals (login name, type, enabled state ONLY — never SIDs or password hashes) on each successful scan. Feeds the per-server logins list in IdentityCenter."
                },
                new CredentialFieldSpec
                {
                    Key = "Parallelism", Label = "Scan parallelism", DefaultValue = "16",
                    Help = "Concurrent server scans (1-64). Per-server failures are isolated findings; one dead server never kills the sweep."
                },
                new CredentialFieldSpec
                {
                    Key = "ConnectTimeoutSeconds", Label = "Connect timeout (seconds)", DefaultValue = "8"
                },
                new CredentialFieldSpec
                {
                    Key = "DiscoverySourceName", Label = "Discovery source name", DefaultValue = "SQLDiscovery",
                    Help = "IC-side source connection name for instance-list hosts that are not in AD (auto-seeds a connection in IC). SPN-enumerated hosts use the AD connection's name so the scan enriches the already-synced computer object."
                },
            }
        }
    };

    private readonly TenantRepository _tenantRepo;
    private readonly CredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public SqlDiscoveryAdapter(
        TenantRepository tenantRepo,
        CredentialProtector protector,
        ILoggerFactory loggerFactory)
    {
        _tenantRepo = tenantRepo;
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new SqlDiscoverySource(tenantId, _tenantRepo, _protector, _loggerFactory.CreateLogger<SqlDiscoverySource>());

    public IConnectorSink? CreateSink(Guid tenantId) => null;
}
