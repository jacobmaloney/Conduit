using System;
using System.Collections.Generic;
using Conduit.Core.Models;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.ActiveDirectory;

/// <summary>
/// Active Directory adapter. Phase 1B: source AND sink. The sink resolves its
/// default target OU from the SyncProject scope at run time — orchestrator
/// stamps "targetOU" on each ConnectorObject before handing to the sink.
/// </summary>
public sealed class ActiveDirectoryAdapter : IConnectorAdapter
{
    public string SystemType => "ActiveDirectory";
    public string DisplayName => "Active Directory";
    public bool SupportsSource => true;
    public bool SupportsSink => true;

    /// <summary>
    /// Phase 2: AD declares incremental support via whenChanged filter splice
    /// (RFC 4517 generalizedTime). Bulk is not supported — System.DirectoryServices.Protocols
    /// is single-record per LDAP modify request.
    /// </summary>
    public ConnectorCapabilities Capabilities { get; } = new()
    {
        SupportsBulk = false,
        MaxBatchSize = 1,
        SupportsIncremental = true,
        // Phase 7: AD writes manager/managedBy as LDAP attributes. No PersonMatch/
        // PersonCreate — AD has no IC-shaped Person concept.
        SupportsAssignManager = true,
        SupportsAssignGroupOwner = true,
        // Phase 5: AD supports the full provisioning trifecta (Add / ModifyDN /
        // unicodePwd over LDAPS).
        SupportsCreate = true,
        SupportsMove = true,
        SupportsResetPassword = true
    };

    /// <summary>
    /// Phase 6. AD needs a routable LDAP host on the Tenant before either Source
    /// or Sink can bind — both <see cref="ActiveDirectorySource.ParseHostPort"/>
    /// and <see cref="ActiveDirectorySink"/> throw when Tenant.Domain is empty.
    /// Declaring it here lets the connection form block Save and lets Quick Sync
    /// pre-flight-validate.
    /// </summary>
    public IReadOnlyList<TenantFieldRequirement> TenantFieldRequirements { get; } = new[]
    {
        new TenantFieldRequirement
        {
            FieldName = "Domain",
            Required = true,
            HelpText = "LDAP server host. Format: 'dc01.demo.local' or 'dc01.demo.local:636' for LDAPS.",
            Placeholder = "dc01.demo.local or dc01.demo.local:636"
        }
    };

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes { get; } = new[]
    {
        new CredentialTypeInfo
        {
            Name = "ldap",
            DisplayName = "Active Directory Bind",
            Description = "LDAP simple-bind username + password against an AD domain controller.",
            Fields = new[]
            {
                new CredentialFieldSpec { Key = "Username", Label = "Username (UPN or DOMAIN\\user)", IsRequired = true },
                new CredentialFieldSpec { Key = "Password", Label = "Password", IsRequired = true, IsSecret = true },
            }
        }
    };

    private readonly TenantRepository _tenantRepo;
    private readonly CredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public ActiveDirectoryAdapter(
        TenantRepository tenantRepo,
        CredentialProtector protector,
        ILoggerFactory loggerFactory)
    {
        _tenantRepo = tenantRepo;
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new ActiveDirectorySource(
            tenantId,
            _tenantRepo,
            _protector,
            _loggerFactory.CreateLogger<ActiveDirectorySource>());

    public IConnectorSink? CreateSink(Guid tenantId) =>
        new ActiveDirectorySink(
            tenantId,
            _tenantRepo,
            _protector,
            _loggerFactory.CreateLogger<ActiveDirectorySink>());

    /// <summary>
    /// Live OU/container browse for the wizard Scope step's Base DN picker.
    /// Binds the tenant's stored 'ldap' credentials and enumerates one level of
    /// containers at a time (lazy drill-down).
    /// </summary>
    public IConnectorContainerBrowser? CreateContainerBrowser(Guid tenantId) =>
        new ActiveDirectoryContainerBrowser(
            tenantId,
            _tenantRepo,
            _protector,
            _loggerFactory.CreateLogger<ActiveDirectoryContainerBrowser>());
}
