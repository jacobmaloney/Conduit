using System;
using System.Collections.Generic;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.ActiveRoles;

/// <summary>
/// One Identity Active Roles adapter. Unlike the raw-AD connector, every read and
/// write goes through the Active Roles Administration Service via the AR ADSI
/// provider (EDMS://) — so policies, workflows and virtual attributes fire. The
/// headline: a Separation-of-Duties policy can DENY a toxic role pairing during a
/// sync, and the deny surfaces as a failed object carrying the SoD reason.
///
/// Phase 1: source + sink, per-object (no bulk), non-incremental. The credential
/// blob and connection settings are resolved through <see cref="IArsConnectionResolver"/>
/// — the in-app path reads the AES-GCM "ars" credential exactly like the AD
/// connector reads "ldap"; the standalone CLI harness supplies a static resolver.
///
/// DEPLOYMENT CONSTRAINT: the Active Roles ADSI provider (AR Management Tools)
/// must be installed on the host that RUNS this connector. It compiles anywhere
/// (System.DirectoryServices is a normal NuGet); it only RUNS where the provider
/// exists (the AR server, or an admin workstation with AR Management Tools).
/// </summary>
public sealed class ActiveRolesAdapter : IConnectorAdapter
{
    public string SystemType => "ActiveRoles";
    public string DisplayName => "Active Roles";
    public bool SupportsSource => true;
    public bool SupportsSink => true;

    /// <summary>
    /// Phase 1: per-object writes (the AR service serializes policy per object),
    /// no bulk, no incremental cursor. SupportsIncremental flips true in Phase 2
    /// when the fast direct-read + whenChanged/USN cursor lands.
    /// </summary>
    public ConnectorCapabilities Capabilities { get; } = new()
    {
        SupportsBulk = false,
        MaxBatchSize = 1,
        SupportsIncremental = false,
    };

    public IReadOnlyList<TenantFieldRequirement> TenantFieldRequirements { get; } = new[]
    {
        new TenantFieldRequirement
        {
            FieldName = "Domain",
            Required = false,
            HelpText = "Optional Active Roles Administration Service host (EDMS:// target). " +
                       "Leave blank to use the AR ADSI provider's default service.",
            Placeholder = "ars01.domain.local"
        }
    };

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes { get; } = new[]
    {
        new CredentialTypeInfo
        {
            Name = "ars",
            DisplayName = "Active Roles Bind",
            Description = "Bind account used by the Active Roles ADSI provider (EDMS://). " +
                          "Writes route through the AR service so policies/workflows/VAs fire.",
            Fields = new[]
            {
                new CredentialFieldSpec
                {
                    Key = "arsServiceHost",
                    Label = "AR Administration Service host",
                    IsRequired = true,
                    Placeholder = "ars01.domain.local",
                    Help = "Host of the Active Roles Administration Service the EDMS:// provider binds through."
                },
                new CredentialFieldSpec
                {
                    Key = "bindUser",
                    Label = "Bind user (DOMAIN\\user or UPN)",
                    IsRequired = true
                },
                new CredentialFieldSpec
                {
                    Key = "bindPassword",
                    Label = "Bind password",
                    IsRequired = true,
                    IsSecret = true
                },
                // ─── Phase 2 fast-read fields: declared but unused in Phase 1 ───
                new CredentialFieldSpec
                {
                    Key = "adHost",
                    Label = "AD host (Phase 2 fast read)",
                    IsRequired = false,
                    Help = "Phase 2: a DC for the fast direct-LDAP read path. Unused in Phase 1."
                },
                new CredentialFieldSpec
                {
                    Key = "arsSqlConnString",
                    Label = "ARS SQL connection (Phase 2 virtual attrs)",
                    IsRequired = false,
                    IsSecret = true,
                    Help = "Phase 2: read-only conn to the ARS config DB (CVSAValues). Unused in Phase 1."
                },
                new CredentialFieldSpec
                {
                    Key = "readMode",
                    Label = "Read mode (Phase 2)",
                    IsRequired = false,
                    AllowedValues = new[] { "policy", "fast" },
                    DefaultValue = "policy",
                    Help = "Phase 2: 'fast' (direct LDAP+SQL) vs 'policy' (through the AR service). Phase 1 is always policy."
                },
            }
        }
    };

    private readonly TenantRepository _tenantRepo;
    private readonly CredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public ActiveRolesAdapter(
        TenantRepository tenantRepo,
        CredentialProtector protector,
        ILoggerFactory loggerFactory)
    {
        _tenantRepo = tenantRepo;
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    private IArsConnectionResolver Resolver(Guid tenantId) =>
        new TenantCredentialArsConnectionResolver(tenantId, _tenantRepo, _protector);

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new ActiveRolesSource(Resolver(tenantId), _loggerFactory.CreateLogger<ActiveRolesSource>());

    public IConnectorSink? CreateSink(Guid tenantId) =>
        new ActiveRolesSink(Resolver(tenantId), _loggerFactory.CreateLogger<ActiveRolesSink>());
}
