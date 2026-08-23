using System;
using System.Collections.Generic;
using System.Linq;

namespace Conduit.Sync.Templates;

/// <summary>
/// Phase 2. Per-connector attribute template library, ported from
/// IdentityCenter's AutoAttributeMappingService. Each template maps a
/// connector's NATIVE source attribute (sAMAccountName, userPrincipalName,
/// displayName, mail, …) to a CANONICAL schema key. The canonical keys are the
/// IC Objects column names (Username, DisplayName, Email, UserPrincipalName,
/// ManagerSourceId, FirstName, LastName, IsActive, …) — they form a connector-
/// neutral bridge so the resolver can join any source connector to any sink
/// connector on the canonical key.
///
/// This is pure static data — no DB, no Objects table. Conduit's symmetric
/// router never lands objects in a lake; the catalog only describes how to
/// translate attribute names between two systems for a sync project.
/// </summary>
public static class AttributeTemplateCatalog
{
    /// <summary>One row in a connector template.</summary>
    public sealed class Entry
    {
        public string SourceAttribute { get; init; } = string.Empty;
        /// <summary>IC Objects column name used as the connector-neutral join key.</summary>
        public string Canonical { get; init; } = string.Empty;
        public bool IsRequired { get; init; }
        public string DataType { get; init; } = "String";
        /// <summary>
        /// Native name to WRITE when this template is the SINK side. Null for the
        /// overwhelming majority of entries, where a connector exposes an attribute
        /// under the same name it accepts it under. Set it only when read-name and
        /// write-name genuinely differ — otherwise the resolver would have to pick
        /// one and break the other direction.
        /// </summary>
        public string? SinkAttribute { get; init; }
        /// <summary>SinkAttribute when set, otherwise SourceAttribute.</summary>
        public string SinkName => string.IsNullOrWhiteSpace(SinkAttribute) ? SourceAttribute : SinkAttribute!;
        /// <summary>
        /// Entry participates ONLY when its template is the sink. A template is used
        /// in both directions, so declaring a canonical to ACCEPT a value silently
        /// also declares it as something to HAND OUT. For directory-control
        /// attributes that is a write primitive, not a convenience — see the
        /// IdentityCenter User template.
        /// </summary>
        public bool SinkOnly { get; init; }
    }

    private static Entry E(string source, string canonical, bool required = false, string dataType = "String", string? sinkAttribute = null, bool sinkOnly = false)
        => new() { SourceAttribute = source, Canonical = canonical, IsRequired = required, DataType = dataType, SinkAttribute = sinkAttribute, SinkOnly = sinkOnly };

    // (SystemType, ObjectClass) -> ordered attribute entries. Keys are matched
    // case-insensitively by the lookup helpers below.
    private static readonly Dictionary<(string SystemType, string ObjectClass), IReadOnlyList<Entry>> _catalog = Build();

    /// <summary>SystemType strings carried by Conduit connections.</summary>
    public static class Systems
    {
        public const string ActiveDirectory = "ActiveDirectory";
        public const string ActiveRoles = "ActiveRoles";
        public const string EntraID = "EntraID";
        public const string AzureResourceGraph = "AzureResourceGraph";
        public const string Okta = "Okta";
        public const string GoogleWorkspace = "GoogleWorkspace";
        public const string Scim = "Scim";
        public const string Csv = "CSV";
        public const string GenericLdap = "GenericLdap";
        public const string Database = "Database";
        public const string SharePoint = "SharePoint";
        public const string Aws = "Aws";
        public const string AwsIdentityCenter = "AWSIdentityCenter";
        public const string SqlDiscovery = "SqlDiscovery";
        // IdentityCenter as a SOURCE (Objects / Identities → any sink). The IC
        // adapter's SystemType is the bare "IdentityCenter" (no namespace), so the
        // string must match IdentityCenterAdapter.SystemType verbatim.
        public const string IdentityCenter = "IdentityCenter";
    }

    /// <summary>Look up a template by connector + object class. Null when none exists.</summary>
    public static IReadOnlyList<Entry>? Get(string systemType, string objectClass)
    {
        foreach (var kvp in _catalog)
        {
            if (string.Equals(kvp.Key.SystemType, systemType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(kvp.Key.ObjectClass, objectClass, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }
        return null;
    }

    /// <summary>All (SystemType, ObjectClass) keys present in the catalog.</summary>
    public static IReadOnlyCollection<(string SystemType, string ObjectClass)> Keys => _catalog.Keys;

    private static Dictionary<(string, string), IReadOnlyList<Entry>> Build()
    {
        var c = new Dictionary<(string, string), IReadOnlyList<Entry>>();

        // ─────────────────────────── Active Directory ───────────────────────────
        c[(Systems.ActiveDirectory, "User")] = new[]
        {
            E("objectGUID", "SourceUniqueId", true),
            E("distinguishedName", "DN", true),
            E("cn", "CN", true),
            E("whenCreated", "WhenCreated"),
            E("whenChanged", "WhenChanged"),
            E("sAMAccountName", "Username", true),
            E("userPrincipalName", "UserPrincipalName"),
            E("displayName", "DisplayName"),
            E("givenName", "FirstName"),
            E("sn", "LastName"),
            E("mail", "Email"),
            E("telephoneNumber", "PhoneNumber"),
            E("mobile", "MobilePhone"),
            E("department", "Department"),
            E("title", "JobTitle"),
            E("company", "Company"),
            E("division", "Division"),
            E("physicalDeliveryOfficeName", "Office"),
            E("costCenter", "CostCenter"),
            E("manager", "ManagerSourceId"),
            E("employeeID", "EmployeeId"),
            E("userAccountControl", "UserAccountControl", false, "Integer"),
            E("pwdLastSet", "PasswordLastSet", false, "DateTime"),
            E("lastLogonTimestamp", "LastLogonTimestamp"),
            // EntryToConnectorObject falls back to lastLogon when the replicated
            // lastLogonTimestamp is absent — but that fallback is dead code unless
            // lastLogon is REQUESTED, and the request set is derived from the mapped
            // source attributes. Mapping it is what makes the fallback reachable.
            // Per-DC and non-replicated, so it is a floor, never an authority.
            E("lastLogon", "LastLogon"),
        };
        c[(Systems.ActiveDirectory, "Group")] = new[]
        {
            E("objectGUID", "SourceUniqueId", true),
            E("distinguishedName", "DN", true),
            E("cn", "CN", true),
            E("whenCreated", "WhenCreated"),
            E("whenChanged", "WhenChanged"),
            E("sAMAccountName", "Username", true),
            E("displayName", "DisplayName"),
            E("description", "Description"),
            E("mail", "Email"),
            E("groupType", "GroupType", false, "Integer"),
            E("managedBy", "ManagedBy"),
            E("adminCount", "AdminCount", false, "Integer"),
            E("isCriticalSystemObject", "IsCriticalSystemObject"),
            // Mapped so the AD source REQUESTS it (the read honors RequestedAttributes,
            // which is derived from the mapped attributes); the orchestrator's
            // group-membership second pass reads Attributes["member"] to push edges.
            // Sink key stays the camelCase AD name "member" (NOT in StructuralAttributes,
            // so without this entry AD groups carry no members). AD member values are
            // DNs — IC leaves them unresolved pending DN->objectGUID reconciliation.
            E("member", "member"),
        };
        c[(Systems.ActiveDirectory, "Computer")] = new[]
        {
            E("objectGUID", "SourceUniqueId", true),
            E("distinguishedName", "DN", true),
            E("cn", "CN", true),
            E("whenCreated", "WhenCreated"),
            E("whenChanged", "WhenChanged"),
            E("sAMAccountName", "Username", true),
            E("displayName", "DisplayName"),
            E("dNSHostName", "DNSHostName"),
            E("operatingSystem", "OperatingSystem"),
            E("operatingSystemVersion", "OSVersion"),
            E("description", "Description"),
            E("location", "Location"),
            E("managedBy", "ManagerSourceId"),
            // Sink key MUST be the camelCase AD name: IC stores non-column attributes
            // verbatim in ObjectAttributes, and every IC consumer (SQL inventory SPN
            // detection, NHI, License Center) queries AttributeName='servicePrincipalName'.
            E("servicePrincipalName", "servicePrincipalName"),
            E("lastLogonTimestamp", "LastLogonTimestamp"),
            E("lastLogon", "LastLogon"),
            E("userAccountControl", "UserAccountControl", false, "Integer"),
            E("pwdLastSet", "PasswordLastSet", false, "DateTime"),
        };
        c[(Systems.ActiveDirectory, "Contact")] = new[]
        {
            E("objectGUID", "SourceUniqueId", true),
            E("distinguishedName", "DN", true),
            E("cn", "CN", true),
            E("whenCreated", "WhenCreated"),
            E("whenChanged", "WhenChanged"),
            E("displayName", "DisplayName"),
            E("givenName", "FirstName"),
            E("sn", "LastName"),
            E("mail", "Email"),
            E("telephoneNumber", "PhoneNumber"),
            E("company", "Company"),
            E("department", "Department"),
            E("title", "JobTitle"),
            E("manager", "ManagerSourceId"),
            E("targetAddress", "TargetAddress"),
            E("proxyAddresses", "ProxyAddresses"),
        };
        c[(Systems.ActiveDirectory, "OrganizationalUnit")] = new[]
        {
            E("objectGUID", "SourceUniqueId", true),
            E("distinguishedName", "DN", true),
            E("name", "CN", true),
            E("whenCreated", "WhenCreated"),
            E("whenChanged", "WhenChanged"),
            E("name", "DisplayName", true),
            E("ou", "OU"),
            E("description", "Description"),
            E("managedBy", "ManagerSourceId"),
            E("gPLink", "GPLink"),
            E("gPOptions", "GPOptions", false, "Integer"),
        };

        // ───────────────── AD infrastructure classes (baseline) ─────────────────
        // The Auto-Generate "Infrastructure" / "Full" tiers emit 19 more AD classes
        // beyond User/Group/Computer/Contact/OU. Previously NONE of them had a
        // template, so each generated a Mapping step with ZERO mappings → the AD read
        // returned only the structural baseline and the sink wrote nothing useful.
        // These give every infra class the universal AD identity set every adObject
        // carries (objectGUID, distinguishedName, cn/name, whenCreated/whenChanged,
        // description) plus a few SAFE class-specific attributes. The grid is fully
        // operator-editable; richer per-class attribute sets are deferred (need the
        // directory owner's judgment). AddAdInfra appends the per-class extras.
        void AddAdInfra(string objectClass, params Entry[] extras)
        {
            var rows = new List<Entry>
            {
                E("objectGUID", "SourceUniqueId", true),
                E("distinguishedName", "DN", true),
                E("name", "CN", true),
                E("displayName", "DisplayName"),
                E("whenCreated", "WhenCreated"),
                E("whenChanged", "WhenChanged"),
                E("description", "Description"),
            };
            rows.AddRange(extras);
            c[(Systems.ActiveDirectory, objectClass)] = rows.ToArray();
        }

        AddAdInfra("container");
        AddAdInfra("domainDNS",
            E("dc", "DomainComponent"),
            E("ms-DS-MachineAccountQuota", "MachineAccountQuota", false, "Integer"));
        AddAdInfra("groupPolicyContainer",
            E("gPCFileSysPath", "GPCFileSysPath"),
            E("versionNumber", "VersionNumber", false, "Integer"),
            E("flags", "Flags", false, "Integer"));
        AddAdInfra("msDS-GroupManagedServiceAccount",
            E("sAMAccountName", "Username"),
            E("dNSHostName", "DNSHostName"),
            E("userAccountControl", "UserAccountControl", false, "Integer"));
        AddAdInfra("msDS-ManagedServiceAccount",
            E("sAMAccountName", "Username"),
            E("dNSHostName", "DNSHostName"),
            E("userAccountControl", "UserAccountControl", false, "Integer"));
        AddAdInfra("foreignSecurityPrincipal",
            E("objectSid", "ObjectSid"));
        AddAdInfra("trustedDomain",
            E("trustPartner", "TrustPartner"),
            E("trustDirection", "TrustDirection", false, "Integer"),
            E("trustType", "TrustType", false, "Integer"),
            E("flatName", "FlatName"));
        AddAdInfra("serviceConnectionPoint",
            E("serviceClassName", "ServiceClassName"),
            E("serviceDNSName", "ServiceDNSName"),
            E("keywords", "Keywords"));
        AddAdInfra("printQueue",
            E("printerName", "PrinterName"),
            E("serverName", "ServerName"),
            E("location", "Location"),
            E("driverName", "DriverName"));
        AddAdInfra("subnet",
            E("siteObject", "SiteObject"),
            E("location", "Location"));
        AddAdInfra("site");
        AddAdInfra("siteLink",
            E("cost", "Cost", false, "Integer"),
            E("replInterval", "ReplInterval", false, "Integer"),
            E("siteList", "SiteList"));
        AddAdInfra("pKICertificateTemplate",
            E("pKIDefaultKeySpec", "PKIDefaultKeySpec", false, "Integer"),
            E("msPKI-Cert-Template-OID", "CertTemplateOID"));
        AddAdInfra("msFVE-RecoveryInformation",
            E("msFVE-RecoveryGuid", "RecoveryGuid"));
        AddAdInfra("certificationAuthority",
            E("cACertificateDN", "CACertificateDN"),
            E("dNSHostName", "DNSHostName"));
        AddAdInfra("attributeSchema",
            E("lDAPDisplayName", "LdapDisplayName"),
            E("attributeID", "AttributeID"),
            E("isSingleValued", "IsSingleValued", false, "Boolean"));
        AddAdInfra("classSchema",
            E("lDAPDisplayName", "LdapDisplayName"),
            E("governsID", "GovernsID"),
            E("objectClassCategory", "ObjectClassCategory", false, "Integer"));
        AddAdInfra("dnsNode",
            E("dnsTombstoned", "DnsTombstoned", false, "Boolean"));
        AddAdInfra("dnsZone");

        // ─────────────────────────── Active Roles (ARS) ─────────────────────────
        // Mirrors the Active Directory real-attribute set verbatim (ARS objects ARE
        // AD objects; the fast read is raw AD LDAP) so Auto-Generate fills the same
        // ~23 user / ~12 group mappings as AD. The differentiator is the trailing
        // block of Active Roles VIRTUAL ATTRIBUTES — they exist only in ARS and are
        // joined from CVSAValues by the fast read. They map to a canonical key of
        // their OWN name so they pass straight through to any sink (and IC stores
        // them verbatim in ObjectAttributes, keyed by the camelCase VA name). The
        // names below are the live UNITE-2026 RBAC/SoD role VAs; other deployments'
        // VAs still flow because the source emits whatever CVSAValues returns — these
        // entries simply pre-seed the mapping grid with the known role VAs.
        c[(Systems.ActiveRoles, "User")] = new[]
        {
            E("objectGUID", "SourceUniqueId", true),
            E("distinguishedName", "DN", true),
            E("cn", "CN", true),
            E("whenCreated", "WhenCreated"),
            E("whenChanged", "WhenChanged"),
            E("sAMAccountName", "Username", true),
            E("userPrincipalName", "UserPrincipalName"),
            E("displayName", "DisplayName"),
            E("givenName", "FirstName"),
            E("sn", "LastName"),
            E("mail", "Email"),
            E("telephoneNumber", "PhoneNumber"),
            E("mobile", "MobilePhone"),
            E("department", "Department"),
            E("title", "JobTitle"),
            E("company", "Company"),
            E("division", "Division"),
            E("physicalDeliveryOfficeName", "Office"),
            E("costCenter", "CostCenter"),
            E("manager", "ManagerSourceId"),
            E("employeeID", "EmployeeId"),
            E("userAccountControl", "UserAccountControl", false, "Integer"),
            E("pwdLastSet", "PasswordLastSet", false, "DateTime"),
            // ─── Active Roles VIRTUAL ATTRIBUTES (joined from CVSAValues) ─────────
            // Boolean role VAs. Canonical = the VA's own name so the value passes
            // through unchanged to the sink. Not "required" — they're optional per user.
            E("UNITE-HelpDeskAdministrator", "UNITE-HelpDeskAdministrator", false, "Boolean"),
            E("UNITE-HelpDeskAuditor", "UNITE-HelpDeskAuditor", false, "Boolean"),
            E("UNITE-HelpDeskOperator", "UNITE-HelpDeskOperator", false, "Boolean"),
            E("UNITE-HRConnectAdmin", "UNITE-HRConnectAdmin", false, "Boolean"),
            E("UNITE-HRConnectPayroll", "UNITE-HRConnectPayroll", false, "Boolean"),
            E("UNITE-HRConnectRecruiter", "UNITE-HRConnectRecruiter", false, "Boolean"),
            E("UNITE-VPNAdmin", "UNITE-VPNAdmin", false, "Boolean"),
            E("UNITE-VPNPrivileged", "UNITE-VPNPrivileged", false, "Boolean"),
            E("UNITE-VPNStandard", "UNITE-VPNStandard", false, "Boolean"),
        };
        c[(Systems.ActiveRoles, "Group")] = new[]
        {
            E("objectGUID", "SourceUniqueId", true),
            E("distinguishedName", "DN", true),
            E("cn", "CN", true),
            E("whenCreated", "WhenCreated"),
            E("whenChanged", "WhenChanged"),
            E("sAMAccountName", "Username", true),
            E("displayName", "DisplayName"),
            E("description", "Description"),
            E("mail", "Email"),
            E("groupType", "GroupType", false, "Integer"),
            E("managedBy", "ManagedBy"),
            E("adminCount", "AdminCount", false, "Integer"),
            E("isCriticalSystemObject", "IsCriticalSystemObject"),
            // See the ActiveDirectory Group note: mapped so the raw AD read requests
            // it; the orchestrator's second pass reads Attributes["member"]. DNs land
            // unresolved on IC until DN->objectGUID reconciliation.
            E("member", "member"),
        };

        // The remaining 22 AD classes the generator advertises for ARS (Computer,
        // Contact, OrganizationalUnit + the 19 infrastructure classes) are read by
        // the same raw AD LDAP path (FastAdReader / EDMS:// DirectorySearcher with a
        // plain (objectClass=X) filter), so the AD templates are the real attribute
        // set for them. No class-specific Active Roles VAs are pre-seeded here; the
        // fast read still merges whatever CVSAValues holds for each object.
        // The alias also resolves real LDAP write names on the SINK side; that is
        // safe only because ActiveRolesSink.EnsureSupportedSinkObjectClass refuses
        // every class except user/group/contact/computer/organizationalUnit.
        foreach (var objectClass in new[]
                 {
                     "Computer", "Contact", "OrganizationalUnit",
                     "container", "domainDNS", "groupPolicyContainer",
                     "msDS-GroupManagedServiceAccount", "msDS-ManagedServiceAccount",
                     "foreignSecurityPrincipal", "trustedDomain", "serviceConnectionPoint",
                     "printQueue", "subnet", "site", "siteLink", "pKICertificateTemplate",
                     "msFVE-RecoveryInformation", "certificationAuthority", "attributeSchema",
                     "classSchema", "dnsNode", "dnsZone"
                 })
        {
            c[(Systems.ActiveRoles, objectClass)] = c[(Systems.ActiveDirectory, objectClass)];
        }

        // ──────────────────────────── IdentityCenter ───────────────────────────
        // IC as a SOURCE. The source native names below are EXACTLY the keys the
        // IdentityCenterSource emits into the attribute bag (Convert / ConvertIdentity)
        // — sourceUniqueId, userName/sAMAccountName, displayName, cn, dn,
        // userPrincipalName, mail/email, isActive, plus the flattened ObjectAttributes
        // (department, jobTitle, manager, …) and the Identities typed columns
        // (firstName, lastName, primaryEmail, employeeId, …). Each maps to the SAME
        // canonical key (= IC Objects column) the other connectors use, so:
        //   • IC → IC  (Objects → Identities/Persons)  bridges native→canonical→native
        //   • IC → any external sink                    bridges on the shared canonical
        // Without these entries the resolver found no SOURCE template for an IC-sourced
        // project and returned ZERO mappings (the "new project on the IC connection had
        // no mappings" bug). The User set covers BOTH the Objects and Identities tables
        // (both surface as ObjectClass "User"); Identities-only keys are additive and
        // harmless when absent on an Objects row.
        //
        // This template is ALSO the SINK side of every AD/ARS/Entra → IC project, and
        // that direction is the one that bites: the resolver INNER-JOINs source to sink
        // on the canonical key, so any canonical MISSING here is dropped from the
        // project before the source is even read. Adding a source connector attribute
        // is not enough — its canonical must exist below or the data never lands.
        c[(Systems.IdentityCenter, "User")] = new[]
        {
            E("sourceUniqueId", "SourceUniqueId", true),
            E("dn", "DN"),
            E("cn", "CN"),
            E("userName", "Username", true),
            E("userPrincipalName", "UserPrincipalName"),
            E("displayName", "DisplayName"),
            E("firstName", "FirstName"),
            E("lastName", "LastName"),
            E("email", "Email"),
            E("phoneNumber", "PhoneNumber"),
            E("mobilePhone", "MobilePhone"),
            E("department", "Department"),
            E("jobTitle", "JobTitle"),
            E("company", "Company"),
            E("division", "Division"),
            E("office", "Office"),
            E("costCenter", "CostCenter"),
            // READ name and WRITE name differ. IC SOURCES this as "manager" (the value
            // lives in ObjectAttributes under that name), but IC's /api/objects/bulk
            // allow-list accepts the manager reference only as "ManagerSourceId" — sent
            // as "manager" it lands back in ObjectAttributes and Objects.ManagerSourceId
            // stays 0, leaving the Lookup step's ManagerSourceId -> ManagerObjectId
            // resolution nothing to read. One name cannot serve both directions.
            E("manager", "ManagerSourceId", sinkAttribute: "ManagerSourceId"),
            E("employeeId", "EmployeeId"),
            E("employeeType", "EmployeeType"),
            E("isActive", "IsActive", false, "Boolean"),
            // Account-state trio. Absent here, the resolver's canonical INNER JOIN
            // dropped them from every AD/ARS -> IC User project before the LDAP read,
            // so IC saw no disabled accounts, no password age and no logon data. IC has
            // no typed Objects column for these — they land in ObjectAttributes under
            // the canonical name, which is exactly what IC's Computer passthrough and
            // the Entra device sync already write.
            //
            // SINK-ONLY, deliberately. IC's ObjectAttributes is app-writable, and the
            // AD/ARS templates declare these same canonicals, so letting IC SOURCE them
            // would resolve IC -> AD/ARS mappings that write an attacker-settable
            // integer straight into AD's account-control bitmask (clear 0x2 to re-enable
            // a terminated account, set 0x80000 TRUSTED_FOR_DELEGATION, ...). The ARS
            // and Generic LDAP sinks write whatever key they are handed. IC is a
            // destination for account state, never an authority on it.
            E("UserAccountControl", "UserAccountControl", false, "Integer", sinkOnly: true),
            E("PasswordLastSet", "PasswordLastSet", false, "DateTime", sinkOnly: true),
            E("LastLogonTimestamp", "LastLogonTimestamp", sinkOnly: true),
            E("LastLogon", "LastLogon", sinkOnly: true),
            E("whenChanged", "WhenChanged"),
            E("whenCreated", "WhenCreated"),
            // Hybrid-twin keys (AD <-> Entra correlation). These land in IC's ObjectAttributes
            // under the Graph spelling; the typed Objects columns (Username, Email, IsActive)
            // do not tell IC whether the value came from on-prem or the cloud. The Entra User
            // template sources them; AD's ProxyAddresses canonical finally has a sink here too.
            // SINK-ONLY for the same reason as UserAccountControl: ObjectAttributes is
            // app-writable, and proxyAddresses / immutableId written back INTO a directory
            // would be a mailbox-routing / join-key change nothing in IC is authorised to make.
            E("ProxyAddresses", "ProxyAddresses", sinkAttribute: "proxyAddresses", sinkOnly: true),
            E("OnPremisesImmutableId", "OnPremisesImmutableId", sinkAttribute: "onPremisesImmutableId", sinkOnly: true),
            E("OnPremisesSyncEnabled", "OnPremisesSyncEnabled", false, "Boolean", sinkAttribute: "onPremisesSyncEnabled", sinkOnly: true),
            E("OnPremisesSamAccountName", "OnPremisesSamAccountName", sinkAttribute: "onPremisesSamAccountName", sinkOnly: true),
            E("Mail", "Mail", sinkAttribute: "mail", sinkOnly: true),
            E("AccountEnabled", "AccountEnabled", false, "Boolean", sinkAttribute: "accountEnabled", sinkOnly: true),
        };
        c[(Systems.IdentityCenter, "Group")] = new[]
        {
            E("sourceUniqueId", "SourceUniqueId", true),
            E("dn", "DN"),
            E("cn", "CN"),
            E("userName", "Username"),
            E("displayName", "DisplayName"),
            E("description", "Description"),
            E("email", "Email"),
            // Same canonical INNER JOIN gap as the User template, and the one that cost
            // the live project its group membership: AD/ARS Group declares groupType,
            // managedBy and member, none of which existed here, so all three were
            // dropped before the LDAP read. Only canonicals IC actually CONSUMES are
            // added — adminCount and isCriticalSystemObject stay out because IC has no
            // reader for either, and a mapping IC discards is just payload weight.
            //
            // groupType drives IC's security-vs-distribution and scope display
            // (PersonRepository / GroupService / ObjectGroupSettingsTab all read
            // ObjectAttributes 'groupType'). SINK-ONLY: it encodes group scope and type,
            // which IC never authors, and the ARS / Generic LDAP sinks write whatever
            // key they are handed.
            E("groupType", "GroupType", false, "Integer", sinkOnly: true),
            // managedBy is the group OWNER reference. IC's bulk ingest resolves it
            // DN -> ObjectId itself, which is the entire job of the "Resolve Group Owner
            // Relationships" Lookup step — a step that resolves nothing while this
            // mapping is absent. Bidirectional on purpose: owner assignment is a real
            // IC -> directory write.
            E("managedBy", "ManagedBy"),
            // Member edges. The value never needs to LAND on IC (nothing there reads an
            // ObjectAttributes row named 'member'; memberships arrive via
            // /api/objects/group-memberships/bulk). The mapping exists so the attribute
            // is REQUESTED from LDAP at all — the orchestrator's group-membership second
            // pass reads the raw Attributes["member"], and an unrequested attribute
            // comes back empty. SINK-ONLY: writing a member list INTO a directory group
            // is a full replace, so an IC-sourced project must never resolve it.
            E("member", "member", sinkOnly: true),
            E("whenChanged", "WhenChanged"),
            E("whenCreated", "WhenCreated"),
        };

        // ─────────────────────────────── EntraID ────────────────────────────────
        c[(Systems.EntraID, "User")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("userPrincipalName", "UserPrincipalName", true),
            E("displayName", "DisplayName", true),
            E("mailNickname", "CN"),
            E("onPremisesSamAccountName", "Username"),
            E("givenName", "FirstName"),
            E("surname", "LastName"),
            E("mail", "Email"),
            E("businessPhones", "PhoneNumber"),
            E("mobilePhone", "MobilePhone"),
            E("department", "Department"),
            E("jobTitle", "JobTitle"),
            E("companyName", "Company"),
            E("manager", "ManagerSourceId"),
            E("employeeId", "EmployeeId"),
            E("employeeType", "EmployeeType"),
            E("accountEnabled", "IsActive", false, "Boolean"),
            E("createdDateTime", "WhenCreated"),
            // Tier 1: per-user last-sign-in summary. IC has no typed Objects column;
            // its canonical last-logon attribute key is "LastLogonTimestamp" (the same
            // key IC's own AD + Entra-device syncs use), landing in ObjectAttributes.
            E("lastSignInDateTime", "LastLogonTimestamp"),
            // Hybrid-twin keys. Graph only returns what is $select-ed (EntraIDSource.UserSelectFields)
            // and the resolver INNER-JOINs on canonical, so an attribute that is selected but has
            // no canonical here (proxyAddresses was) is dropped before it reaches the sink. The four
            // that already map to typed IC columns above (UPN, Username, Email, IsActive) are
            // re-declared under their Graph names so the raw Entra value is preserved alongside.
            // userPrincipalName has no second entry: IC's bulk allow-list matches column names
            // case-insensitively, so a "userPrincipalName" attribute is the typed column again.
            E("proxyAddresses", "ProxyAddresses"),
            E("onPremisesImmutableId", "OnPremisesImmutableId"),
            E("onPremisesSyncEnabled", "OnPremisesSyncEnabled", false, "Boolean"),
            E("onPremisesSamAccountName", "OnPremisesSamAccountName"),
            E("mail", "Mail"),
            E("accountEnabled", "AccountEnabled", false, "Boolean"),
        };
        c[(Systems.EntraID, "Group")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("mailNickname", "CN"),
            E("onPremisesSamAccountName", "Username"),
            E("description", "Description"),
            E("mail", "Email"),
            E("securityEnabled", "SecurityEnabled", false, "Boolean"),
            E("groupTypes", "GroupTypes"),
            E("mailEnabled", "MailEnabled", false, "Boolean"),
            E("createdDateTime", "WhenCreated"),
        };
        c[(Systems.EntraID, "ServicePrincipal")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("appId", "AppId"),
            E("servicePrincipalType", "ServicePrincipalType"),
            E("appDisplayName", "AppDisplayName"),
            E("servicePrincipalNames", "ServicePrincipalNames"),
            E("accountEnabled", "IsActive", false, "Boolean"),
            E("createdDateTime", "WhenCreated"),
        };
        c[(Systems.EntraID, "DirectoryRole")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("description", "Description"),
            E("roleTemplateId", "RoleTemplateId"),
        };
        c[(Systems.EntraID, "Application")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("appId", "AppId"),
            E("signInAudience", "SignInAudience"),
            E("publisherDomain", "PublisherDomain"),
            E("description", "Description"),
            E("identifierUris", "IdentifierUris"),
            E("tags", "Tags"),
            E("createdDateTime", "WhenCreated"),
        };
        c[(Systems.EntraID, "Device")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("deviceId", "DeviceId"),
            E("operatingSystem", "OperatingSystem"),
            E("operatingSystemVersion", "OSVersion"),
            E("trustType", "TrustType"),
            E("managementType", "ManagementType"),
            E("manufacturer", "Manufacturer"),
            E("model", "Model"),
            E("isManaged", "IsManaged", false, "Boolean"),
            E("isCompliant", "IsCompliant", false, "Boolean"),
            E("accountEnabled", "IsActive", false, "Boolean"),
            E("lastSignInDateTime", "LastLogonTimestamp"),
            E("createdDateTime", "WhenCreated"),
        };
        c[(Systems.EntraID, "AdministrativeUnit")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("description", "Description"),
            E("visibility", "Visibility"),
        };
        c[(Systems.EntraID, "ConditionalAccessPolicy")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("state", "State"),
            E("createdDateTime", "WhenCreated"),
            E("modifiedDateTime", "WhenChanged"),
        };
        c[(Systems.EntraID, "OAuth2PermissionGrant")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("clientId", "ClientId"),
            E("consentType", "ConsentType"),
            E("principalId", "PrincipalId"),
            E("resourceId", "ResourceId"),
            E("scope", "Scope"),
        };
        c[(Systems.EntraID, "Domain")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("authenticationType", "AuthenticationType"),
            E("isDefault", "IsDefault", false, "Boolean"),
            E("isVerified", "IsVerified", false, "Boolean"),
            E("isInitial", "IsInitial", false, "Boolean"),
            E("supportedServices", "SupportedServices"),
        };
        // Per-user Microsoft 365 usage — five Graph usage reports merged by UPN
        // (Office365ActiveUserDetail spine + OneDrive/Mailbox storage + M365 apps +
        // Teams activity). SourceUniqueId = UPN (joins to the IC user object).
        c[(Systems.EntraID, "m365usage")] = new[]
        {
            E("UserPrincipalName", "SourceUniqueId", true),
            E("DisplayName", "DisplayName"),
            E("HasExchangeLicense", "HasExchangeLicense", false, "Boolean"),
            E("HasOneDriveLicense", "HasOneDriveLicense", false, "Boolean"),
            E("HasSharePointLicense", "HasSharePointLicense", false, "Boolean"),
            E("HasTeamsLicense", "HasTeamsLicense", false, "Boolean"),
            E("HasYammerLicense", "HasYammerLicense", false, "Boolean"),
            E("ExchangeLastActivityDate", "ExchangeLastActivityDate", false, "DateTime"),
            E("OneDriveLastActivityDate", "OneDriveLastActivityDate", false, "DateTime"),
            E("SharePointLastActivityDate", "SharePointLastActivityDate", false, "DateTime"),
            E("TeamsLastActivityDate", "TeamsLastActivityDate", false, "DateTime"),
            E("YammerLastActivityDate", "YammerLastActivityDate", false, "DateTime"),
            E("M365AppLastActivityDate", "M365AppLastActivityDate", false, "DateTime"),
            E("OneDriveStorageUsedBytes", "OneDriveStorageUsedBytes", false, "Integer"),
            E("OneDriveStorageAllocatedBytes", "OneDriveStorageAllocatedBytes", false, "Integer"),
            E("MailboxStorageUsedBytes", "MailboxStorageUsedBytes", false, "Integer"),
            E("MailboxQuotaBytes", "MailboxQuotaBytes", false, "Integer"),
            E("TeamsChatMessages", "TeamsChatMessages", false, "Integer"),
            E("TeamsPrivateChatMessages", "TeamsPrivateChatMessages", false, "Integer"),
            E("TeamsCallCount", "TeamsCallCount", false, "Integer"),
            E("TeamsMeetingCount", "TeamsMeetingCount", false, "Integer"),
            E("AssignedProducts", "AssignedProducts"),
            E("ReportRefreshDate", "ReportRefreshDate", false, "DateTime"),
        };

        // Entra sign-in EVENT stream (objectClass "signinlog"). Pumped as a Mapping
        // step; the IC sink routes it to its sign-in ingest endpoint. SourceUniqueId =
        // the Graph sign-in id; userSourceUniqueId/userPrincipalName join the event to
        // the user. Event-shaped fields pass through to same-named canonical keys (no
        // person column), like m365usage / azureresource. Source-native names match
        // EntraSignInLogSource.Convert verbatim.
        c[(Systems.EntraID, "signinlog")] = new[]
        {
            E("signInId", "SourceUniqueId", true),
            E("userSourceUniqueId", "userSourceUniqueId"),
            E("userPrincipalName", "UserPrincipalName"),
            E("signInDateTime", "signInDateTime", false, "DateTime"),
            E("appDisplayName", "appDisplayName"),
            E("appId", "appId"),
            E("clientAppUsed", "clientAppUsed"),
            E("ipAddress", "ipAddress"),
            E("location", "location"),
            E("status", "status"),
            E("errorCode", "errorCode", false, "Integer"),
            E("isInteractive", "isInteractive", false, "Boolean"),
            E("riskLevel", "riskLevel"),
            E("riskState", "riskState"),
            E("conditionalAccessStatus", "conditionalAccessStatus"),
            E("resourceDisplayName", "resourceDisplayName"),
            E("resourceId", "resourceId"),
        };

        // Entra license-assignment stream (objectClass "license"). Pumped as a Mapping
        // step; the IC sink routes it to /api/objects/licenses/bulk. SourceUniqueId =
        // "{userId}:{skuId}" (the per-assignment key). Pool fields (SkuId/SkuName/part
        // number + capacity counts) AND assignee fields (UPN + objectGUID) pass through
        // to same-named keys so the sink's BuildLicenseRow can populate both the
        // LicensePools upsert and the LicenseAssignments upsert. Names match
        // EntraLicenseSource.Build verbatim.
        c[(Systems.EntraID, "license")] = new[]
        {
            E("SkuId", "SkuId", true),
            E("SkuName", "SkuName"),
            E("SkuPartNumber", "SkuPartNumber"),
            E("TotalUnits", "TotalUnits", false, "Integer"),
            E("ConsumedUnits", "ConsumedUnits", false, "Integer"),
            E("WarningUnits", "WarningUnits", false, "Integer"),
            E("SuspendedUnits", "SuspendedUnits", false, "Integer"),
            E("UserPrincipalName", "UserPrincipalName"),
            E("UserSourceUniqueId", "UserSourceUniqueId"),
            E("AssignmentSource", "AssignmentSource"),
        };

        // Entra enterprise-app role-assignment stream (objectClass "approleassignment").
        // Pumped as a Mapping step; the IC sink routes it to
        // /api/objects/app-role-assignments/bulk. SourceUniqueId = the appRoleAssignment
        // id. Principal + resource GUIDs and display names pass through to same-named
        // keys so the sink's BuildAppRoleRow can populate the AppRoleAssignments row.
        // Names match EntraAppRoleSource.Build verbatim.
        c[(Systems.EntraID, "approleassignment")] = new[]
        {
            E("AppRoleAssignmentId", "SourceUniqueId", true),
            E("PrincipalId", "PrincipalId"),
            E("PrincipalType", "PrincipalType"),
            E("PrincipalDisplayName", "PrincipalDisplayName"),
            E("ResourceId", "ResourceId"),
            E("ResourceDisplayName", "ResourceDisplayName"),
            E("AppRoleId", "AppRoleId"),
            E("CreatedDateTime", "CreatedDateTime", false, "DateTime"),
        };

        // ────────────────────────── Azure Resource Graph ───────────────────────
        // Source-only cloud inventory. Non-person classes: id is the ARM resource id
        // (stable join key → SourceUniqueId). Attributes pass through to same-named
        // canonical keys where there is no person-shaped column.
        c[(Systems.AzureResourceGraph, "azuresubscription")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("subscriptionId", "subscriptionId"),
            E("tenantId", "tenantId"),
            E("state", "state"),
        };
        c[(Systems.AzureResourceGraph, "azureresource")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("name", "DisplayName", true),
            E("resourceType", "resourceType"),
            E("location", "location"),
            E("subscriptionId", "subscriptionId"),
            E("resourceGroup", "resourceGroup"),
            E("sku", "sku"),
            E("tags", "tags"),
            E("licenseType", "licenseType"),
            E("azureHybridBenefit", "azureHybridBenefit", false, "Boolean"),
            E("size", "size"),
            E("vCores", "vCores"),
        };

        // ───────────────────────────── SharePoint ──────────────────────────────
        c[(Systems.SharePoint, "Site")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("name", "CN"),
            E("webUrl", "WebUrl"),
            E("description", "Description"),
            E("createdDateTime", "WhenCreated"),
            E("lastModifiedDateTime", "WhenChanged"),
            // Site-tree hierarchy ref: parent site's SourceId (empty for roots).
            // Derived by webUrl path containment in the connector (no Graph call).
            E("parentSiteId", "ParentSiteId"),
            // Storage joined from getSharePointSiteUsageDetail (Reports.Read.All).
            E("StorageUsedBytes", "StorageUsedBytes", false, "Integer"),
            E("StorageAllocatedBytes", "StorageAllocatedBytes", false, "Integer"),
            E("FileCount", "FileCount", false, "Integer"),
        };
        // Per-site SharePoint groups. NOTE: enumeration is deferred in the Graph-
        // only connector (requires the SharePoint REST API); the template is
        // pre-seeded so the mapping grid is ready when REST enumeration lands.
        c[(Systems.SharePoint, "sharepointgroup")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("loginName", "CN"),
            E("description", "Description"),
            E("siteId", "SiteId"),
            E("siteName", "SiteName"),
            E("ownerTitle", "OwnerTitle"),
        };
        c[(Systems.SharePoint, "Team")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("mailNickname", "CN"),
            E("description", "Description"),
            E("mail", "Email"),
            E("visibility", "Visibility"),
            E("createdDateTime", "WhenCreated"),
            // Team membership edges. Mapped so the orchestrator's group-membership
            // second pass reads Attributes["members"] and pushes the edges to IC
            // /api/objects/group-memberships/bulk (identical to AD group "member").
            E("members", "members"),
        };
        c[(Systems.SharePoint, "Drive")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("name", "CN"),
            E("driveType", "DriveType"),
            E("webUrl", "WebUrl"),
            E("siteName", "SiteName"),
            E("quotaTotal", "QuotaTotal"),
            E("quotaUsed", "QuotaUsed"),
            E("quotaState", "QuotaState"),
            E("createdDateTime", "WhenCreated"),
            E("lastModifiedDateTime", "WhenChanged"),
        };
        c[(Systems.SharePoint, "Channel")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("description", "Description"),
            E("membershipType", "MembershipType"),
            E("webUrl", "WebUrl"),
            E("teamId", "TeamId"),
            E("teamName", "TeamName"),
            E("createdDateTime", "WhenCreated"),
        };
        // Bounded set of top-level channel files (driveItems under the channel's
        // filesFolder). channelId/teamId are hierarchy refs so IC can browse
        // team -> channel -> files. Capped per channel by the connector.
        c[(Systems.SharePoint, "channelfile")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("driveItemId", "DriveItemId"),
            E("displayName", "DisplayName", true),
            E("webUrl", "WebUrl"),
            E("size", "Size", false, "Integer"),
            E("isFolder", "IsFolder", false, "Boolean"),
            E("channelId", "ChannelId"),
            E("teamId", "TeamId"),
            E("lastModifiedDateTime", "WhenChanged"),
        };
        c[(Systems.SharePoint, "List")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("name", "CN"),
            E("webUrl", "WebUrl"),
            E("description", "Description"),
            E("siteName", "SiteName"),
            E("listTemplate", "ListTemplate"),
            E("createdDateTime", "WhenCreated"),
            E("lastModifiedDateTime", "WhenChanged"),
        };
        c[(Systems.SharePoint, "SubscribedSku")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("skuPartNumber", "DisplayName", true),
            E("skuId", "SkuId"),
            E("appliesTo", "AppliesTo"),
            E("consumedUnits", "ConsumedUnits"),
            E("prepaidEnabled", "PrepaidEnabled"),
            E("prepaidSuspended", "PrepaidSuspended"),
            E("servicePlanCount", "ServicePlanCount"),
        };

        // ──────────────────────────────── SCIM ─────────────────────────────────
        // ─────────────────────────── CSV (flat HR feed) ───────────────────────────
        // A CSV has no fixed native schema — columns are whatever the file's header row
        // says. This template assumes the CONVENTIONAL HR-export header names (which are
        // the canonical keys themselves) so Auto-map yields a sensible Identities feed out
        // of the box. Columns absent from a given file simply produce no value; rename the
        // rows for a non-standard header, or leave the step on passthrough to carry every
        // column verbatim. EmployeeId is the correlation/business key.
        c[(Systems.Csv, "User")] = new[]
        {
            E("EmployeeId", "EmployeeId", true),
            E("FirstName", "FirstName"),
            E("LastName", "LastName"),
            E("DisplayName", "DisplayName"),
            E("Email", "Email"),
            E("UserPrincipalName", "UserPrincipalName"),
            E("Username", "Username"),
            E("Department", "Department"),
            E("JobTitle", "JobTitle"),
            E("EmployeeType", "EmployeeType"),
            E("ManagerId", "ManagerSourceId"),
            E("Company", "Company"),
            E("Office", "Office"),
            E("PhoneNumber", "PhoneNumber"),
            E("MobilePhone", "MobilePhone"),
            E("CostCenter", "CostCenter"),
        };

        c[(Systems.Scim, "User")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("userName", "Username", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("givenName", "FirstName"),
            E("sn", "LastName"),
            E("mail", "Email"),
            E("userName", "UserPrincipalName"),
            E("telephoneNumber", "PhoneNumber"),
            E("mobile", "MobilePhone"),
            E("department", "Department"),
            E("title", "JobTitle"),
            E("company", "Company"),
            E("division", "Division"),
            E("costCenter", "CostCenter"),
            E("employeeId", "EmployeeId"),
            E("manager", "ManagerSourceId"),
            E("accountEnabled", "IsActive", false, "Boolean"),
            E("whenCreated", "WhenCreated"),
            E("whenChanged", "WhenChanged"),
        };
        c[(Systems.Scim, "Group")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("whenCreated", "WhenCreated"),
            E("whenChanged", "WhenChanged"),
        };

        // ──────────────────────────────── Okta ─────────────────────────────────
        c[(Systems.Okta, "User")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("userPrincipalName", "UserPrincipalName", true),
            E("cn", "Username"),
            E("displayName", "DisplayName"),
            E("givenName", "FirstName"),
            E("sn", "LastName"),
            E("mail", "Email"),
            E("department", "Department"),
            E("title", "JobTitle"),
            E("company", "Company"),
            E("telephoneNumber", "PhoneNumber"),
            E("mobile", "MobilePhone"),
            E("division", "Division"),
            E("costCenter", "CostCenter"),
            E("employeeId", "EmployeeId"),
            E("manager", "ManagerSourceId"),
            E("accountEnabled", "IsActive", false, "Boolean"),
            E("lastLogin", "LastLogin"),
            E("whenCreated", "WhenCreated"),
        };
        c[(Systems.Okta, "Group")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("description", "Description"),
            E("groupType", "GroupType"),
        };
        c[(Systems.Okta, "Application")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("appSignOnMode", "SignOnMode"),
            E("appStatus", "AppStatus"),
        };

        // ─────────────────────────── Google Workspace ──────────────────────────
        c[(Systems.GoogleWorkspace, "User")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("mail", "Email", true),
            E("userPrincipalName", "UserPrincipalName"),
            E("cn", "Username"),
            E("displayName", "DisplayName"),
            E("givenName", "FirstName"),
            E("sn", "LastName"),
            E("dn", "DN"),
            E("department", "Department"),
            E("title", "JobTitle"),
            E("company", "Company"),
            E("telephoneNumber", "PhoneNumber"),
            E("mobile", "MobilePhone"),
            E("division", "Division"),
            E("costCenter", "CostCenter"),
            E("accountEnabled", "IsActive", false, "Boolean"),
            E("isAdmin", "IsAdmin"),
            E("lastLoginTime", "LastLogin"),
            E("whenCreated", "WhenCreated"),
        };
        c[(Systems.GoogleWorkspace, "Group")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("mail", "Email"),
            E("description", "Description"),
        };
        c[(Systems.GoogleWorkspace, "OrganizationalUnit")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("dn", "DN"),
            E("description", "Description"),
            E("orgUnitPath", "OrgUnitPath"),
            E("parentOrgUnitPath", "ParentOrgUnitPath"),
        };
        c[(Systems.GoogleWorkspace, "Role")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("roleName", "RoleName"),
            E("description", "Description"),
            E("isSystemRole", "IsSystemRole", false, "Boolean"),
            E("isSuperAdminRole", "IsSuperAdminRole", false, "Boolean"),
        };
        c[(Systems.GoogleWorkspace, "Domain")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("domainName", "DomainName"),
            E("isPrimary", "IsPrimary", false, "Boolean"),
            E("verified", "Verified", false, "Boolean"),
        };

        c[(Systems.GoogleWorkspace, "mobiledevice")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("model", "Model"),
            E("os", "OperatingSystem"),
            E("deviceType", "DeviceType"),
            E("status", "Status"),
            E("serialNumber", "SerialNumber"),
            E("ownerEmail", "OwnerEmail"),
            E("lastSync", "LastSync"),
        };

        c[(Systems.GoogleWorkspace, "chromeosdevice")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("serialNumber", "SerialNumber"),
            E("status", "Status"),
            E("model", "Model"),
            E("osVersion", "OsVersion"),
            E("platformVersion", "PlatformVersion"),
            E("macAddress", "MacAddress"),
            E("annotatedUser", "AnnotatedUser"),
            E("annotatedLocation", "AnnotatedLocation"),
            E("orgUnitPath", "OrgUnitPath"),
            E("lastSync", "LastSync"),
        };

        c[(Systems.GoogleWorkspace, "roleAssignment")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("roleId", "RoleId"),
            E("assignedTo", "AssignedTo"),
            E("scopeType", "ScopeType"),
            E("orgUnitId", "OrgUnitId"),
        };

        c[(Systems.GoogleWorkspace, "calendarresource")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("resourceType", "ResourceType"),
            E("resourceEmail", "ResourceEmail"),
            E("description", "Description"),
            E("buildingId", "BuildingId"),
            E("floorName", "FloorName"),
            E("capacity", "Capacity", false, "Integer"),
        };

        // ──────────────────────────────── AWS ──────────────────────────────────
        c[(Systems.Aws, "User")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("sAMAccountName", "Username", true),
            E("displayName", "DisplayName"),
            E("cn", "CN"),
            E("givenName", "FirstName"),
            E("sn", "LastName"),
            E("mail", "Email"),
            E("title", "JobTitle"),
            E("accountEnabled", "IsActive", false, "Boolean"),
            E("lastLogin", "LastLogin"),
        };
        c[(Systems.Aws, "Group")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("description", "Description"),
        };
        c[(Systems.Aws, "Role")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("description", "Description"),
            E("arn", "ARN"),
            E("maxSessionDuration", "MaxSessionDuration", false, "Integer"),
            E("whenCreated", "WhenCreated"),
        };
        c[(Systems.Aws, "Policy")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("description", "Description"),
            E("arn", "ARN"),
            E("attachmentCount", "AttachmentCount", false, "Integer"),
            E("whenCreated", "WhenCreated"),
            E("whenChanged", "WhenChanged"),
        };
        c[(Systems.Aws, "Account")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("accountAlias", "AccountAlias"),
        };

        // EC2 instances in the connection's Region — same "computer" class AD hosts
        // use, so cloud VMs land in the one estate inventory.
        c[(Systems.Aws, "Computer")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("instanceType", "InstanceType"),
            E("state", "State"),
            E("region", "Region"),
            E("privateIp", "PrivateIp"),
            E("publicIp", "PublicIp"),
            E("dNSHostName", "DnsHostName"),
            E("imageId", "ImageId"),
            E("vpcId", "VpcId"),
            E("subnetId", "SubnetId"),
            E("architecture", "Architecture"),
            E("platform", "Platform"),
            E("whenCreated", "WhenCreated"),
        };

        // ───────────────────────── AWS Identity Center ─────────────────────────
        // The SSO-flavored IdentityStore + SSO Admin source. SourceUniqueId is the
        // IdentityStore UserId / GroupId for user/group, and the permission-set ARN
        // for permissionSet.
        c[(Systems.AwsIdentityCenter, "User")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("userName", "Username", true),
            E("displayName", "DisplayName"),
            E("givenName", "FirstName"),
            E("sn", "LastName"),
            E("mail", "Email"),
            E("title", "JobTitle"),
            E("telephoneNumber", "PhoneNumber"),
            E("mobilePhone", "MobilePhone"),
            E("externalId", "ExternalId"),
        };
        c[(Systems.AwsIdentityCenter, "Group")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("description", "Description"),
            E("externalId", "ExternalId"),
        };
        c[(Systems.AwsIdentityCenter, "PermissionSet")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("name", "Name"),
            E("description", "Description"),
            E("arn", "ARN"),
            E("sessionDuration", "SessionDuration"),
            E("whenCreated", "WhenCreated"),
        };

        c[(Systems.AwsIdentityCenter, "Application")] = new[]
        {
            E("id", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("name", "Name"),
            E("description", "Description"),
            E("arn", "ARN"),
            E("providerArn", "ProviderArn"),
            E("status", "Status"),
            E("whenCreated", "WhenCreated"),
        };

        // ───────────────────────────── Generic LDAP ────────────────────────────
        c[(Systems.GenericLdap, "User")] = new[]
        {
            E("entryUUID", "SourceUniqueId", true),
            E("uid", "Username", true),
            E("cn", "CN"),
            E("displayName", "DisplayName"),
            E("givenName", "FirstName"),
            E("sn", "LastName"),
            E("mail", "Email"),
            E("telephoneNumber", "PhoneNumber"),
            E("title", "JobTitle"),
            E("ou", "Department"),
            E("o", "Company"),
            E("division", "Division"),
            E("costCenter", "CostCenter"),
            E("manager", "ManagerSourceId"),
            E("dn", "DN"),
        };
        c[(Systems.GenericLdap, "Group")] = new[]
        {
            E("entryUUID", "SourceUniqueId", true),
            E("cn", "CN", true),
            E("displayName", "DisplayName"),
            E("description", "Description"),
            E("dn", "DN"),
        };
        // Full tier emits organizationalUnit; without this the OU step had ZERO mappings.
        c[(Systems.GenericLdap, "OrganizationalUnit")] = new[]
        {
            E("entryUUID", "SourceUniqueId", true),
            E("ou", "CN", true),
            E("dn", "DN"),
            E("description", "Description"),
        };

        // ──────────────────────────────── Database ─────────────────────────────
        c[(Systems.Database, "User")] = new[]
        {
            E("objectGuid", "SourceUniqueId", true),
            E("sAMAccountName", "Username"),
            E("displayName", "DisplayName"),
            E("cn", "CN"),
            E("givenName", "FirstName"),
            E("sn", "LastName"),
            E("mail", "Email"),
            E("department", "Department"),
            E("title", "JobTitle"),
            E("company", "Company"),
            E("division", "Division"),
            E("costCenter", "CostCenter"),
            E("office", "Office"),
            E("employeeId", "EmployeeId"),
            E("manager", "ManagerSourceId"),
            E("accountEnabled", "IsActive", false, "Boolean"),
        };
        c[(Systems.Database, "Group")] = new[]
        {
            E("objectGuid", "SourceUniqueId", true),
            E("displayName", "DisplayName", true),
            E("cn", "CN"),
            E("description", "Description"),
        };

        // ───────────────────────────── SQL Discovery ───────────────────────────
        // Source-only scan: each scanned SQL host is emitted as a "computer". The
        // SourceUniqueId is carried structurally on ConnectorObject.SourceId (not an
        // attribute). The names below match SqlDiscoverySource.cs verbatim. CN /
        // DisplayName / servicePrincipalName / operatingSystem / dNSHostName / IsActive
        // bridge to IC Objects columns (servicePrincipalName lands in ObjectAttributes
        // — the MSSQLSvc SPN drives IC's SQL Servers page + License Center). The sql*
        // facts pass through to same-named canonical keys (like azureresource), so the
        // sink stores them verbatim. Without this template the SqlDiscovery computer
        // step generated ZERO mappings and the sink wrote only the structural baseline.
        c[(Systems.SqlDiscovery, "computer")] = new[]
        {
            E("CN", "CN", true),
            E("DisplayName", "DisplayName", true),
            E("dNSHostName", "DNSHostName"),
            E("operatingSystem", "OperatingSystem"),
            E("IsActive", "IsActive", false, "Boolean"),
            E("servicePrincipalName", "servicePrincipalName"),
            E("sqlServerEdition", "sqlServerEdition"),
            E("sqlServerVersion", "sqlServerVersion"),
            E("sqlInstanceName", "sqlInstanceName"),
            E("sqlServerPort", "sqlServerPort"),
            E("sqlDatabasesJson", "sqlDatabasesJson"),
            E("sqlLoginsJson", "sqlLoginsJson"),
            E("sqlPrincipalsJson", "sqlPrincipalsJson"),
            E("cpuCores", "cpuCores", false, "Integer"),
            E("memoryGB", "memoryGB", false, "Integer"),
            E("ipHostNumber", "ipHostNumber"),
            E("sqlLastScannedAt", "sqlLastScannedAt", false, "DateTime"),
            E("sqlScanStatus", "sqlScanStatus"),
            E("sqlLastScanAttemptAt", "sqlLastScanAttemptAt", false, "DateTime"),
            E("sqlScanError", "sqlScanError"),
        };

        return c;
    }
}
