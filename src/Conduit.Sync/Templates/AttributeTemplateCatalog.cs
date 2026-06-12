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
    }

    private static Entry E(string source, string canonical, bool required = false, string dataType = "String")
        => new() { SourceAttribute = source, Canonical = canonical, IsRequired = required, DataType = dataType };

    // (SystemType, ObjectClass) -> ordered attribute entries. Keys are matched
    // case-insensitively by the lookup helpers below.
    private static readonly Dictionary<(string SystemType, string ObjectClass), IReadOnlyList<Entry>> _catalog = Build();

    /// <summary>SystemType strings carried by Conduit connections.</summary>
    public static class Systems
    {
        public const string ActiveDirectory = "ActiveDirectory";
        public const string ActiveRoles = "ActiveRoles";
        public const string EntraID = "EntraID";
        public const string Okta = "Okta";
        public const string GoogleWorkspace = "GoogleWorkspace";
        public const string Scim = "Scim";
        public const string GenericLdap = "GenericLdap";
        public const string Database = "Database";
        public const string SharePoint = "SharePoint";
        public const string Aws = "Aws";
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
        };
        c[(Systems.ActiveDirectory, "Computer")] = new[]
        {
            E("objectGUID", "SourceUniqueId", true),
            E("distinguishedName", "DN", true),
            E("cn", "CN", true),
            E("whenCreated", "WhenCreated"),
            E("whenChanged", "WhenChanged"),
            E("sAMAccountName", "Username", true),
            E("dNSHostName", "DNSHostName"),
            E("operatingSystem", "OperatingSystem"),
            E("operatingSystemVersion", "OSVersion"),
            E("description", "Description"),
            E("location", "Location"),
            E("managedBy", "ManagerSourceId"),
            E("servicePrincipalName", "ServicePrincipalNames"),
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

        return c;
    }
}
