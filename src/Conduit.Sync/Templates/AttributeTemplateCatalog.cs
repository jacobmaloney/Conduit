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
        public const string AzureResourceGraph = "AzureResourceGraph";
        public const string Okta = "Okta";
        public const string GoogleWorkspace = "GoogleWorkspace";
        public const string Scim = "Scim";
        public const string GenericLdap = "GenericLdap";
        public const string Database = "Database";
        public const string SharePoint = "SharePoint";
        public const string Aws = "Aws";
        public const string AwsIdentityCenter = "AWSIdentityCenter";
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
