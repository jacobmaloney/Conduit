using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Google;
using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Admin.Directory.directory_v1.Data;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.GoogleWorkspace;

/// <summary>
/// Google Workspace source — paged enumeration via pageToken. Governance-relevant
/// classes: "user" / "group" / "organizationalUnit" / "role" / "domain". Users
/// uses Projection=Full to pull organizations/phones/relations; group membership
/// is read via members.list per group. User/Group/organizationalUnit/role/domain
/// are explicit branches; an unknown class throws NotSupportedException. A
/// per-class 403 (insufficient delegated scope) is logged as a WARNING and yields
/// nothing rather than aborting the run.
/// </summary>
public sealed class GoogleWorkspaceSource : IConnectorSource
{
    private readonly Guid _tenantId;
    private readonly CredentialProtector _protector;
    private readonly ILogger<GoogleWorkspaceSource> _logger;

    public GoogleWorkspaceSource(Guid tenantId, CredentialProtector protector, ILogger<GoogleWorkspaceSource> logger)
    {
        _tenantId = tenantId;
        _protector = protector;
        _logger = logger;
    }

    public async IAsyncEnumerable<ConnectorObject> ReadAsync(
        string objectClass,
        SyncProjectScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var creds = await GoogleCredentialReader.ReadAsync(_protector, _tenantId)
            ?? throw new InvalidOperationException($"No 'google' credential for tenant {_tenantId}.");
        var service = await GoogleCredentialReader.CreateServiceAsync(creds, readOnly: true);
        var pageSize = scope.PageSize > 0 && scope.PageSize <= 500 ? scope.PageSize : 200;
        var domain = creds.Domain;
        var emitted = 0;

        var stream = Dispatch(objectClass, service, domain, pageSize, cancellationToken);

        await foreach (var obj in stream)
        {
            if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
            emitted++;
            yield return obj;
        }
    }

    /// <summary>
    /// Routes the requested object class to its dedicated enumerator. User/Group are
    /// explicit branches (never the default); organizationalUnit/role/domain are
    /// governance classes each with per-class 403 skip. Unknown classes throw
    /// NotSupportedException.
    /// </summary>
    private IAsyncEnumerable<ConnectorObject> Dispatch(
        string objectClass, DirectoryService service, string? domain, int pageSize, CancellationToken ct)
    {
        if (string.Equals(objectClass, "user", StringComparison.OrdinalIgnoreCase))
            return EnumerateUsersAsync(service, domain, pageSize, ct);
        if (string.Equals(objectClass, "group", StringComparison.OrdinalIgnoreCase))
            return EnumerateGroupsAsync(service, domain, pageSize, ct);
        if (string.Equals(objectClass, "organizationalUnit", StringComparison.OrdinalIgnoreCase))
            return EnumerateOrgUnitsAsync(service, _logger, ct);
        if (string.Equals(objectClass, "role", StringComparison.OrdinalIgnoreCase))
            return EnumerateRolesAsync(service, _logger, ct);
        if (string.Equals(objectClass, "domain", StringComparison.OrdinalIgnoreCase))
            return EnumerateDomainsAsync(service, _logger, ct);
        if (string.Equals(objectClass, "mobiledevice", StringComparison.OrdinalIgnoreCase))
            return EnumerateMobileDevicesAsync(service, _logger, ct);
        if (string.Equals(objectClass, "chromeosdevice", StringComparison.OrdinalIgnoreCase))
            return EnumerateChromeOsDevicesAsync(service, _logger, ct);
        if (string.Equals(objectClass, "roleAssignment", StringComparison.OrdinalIgnoreCase))
            return EnumerateRoleAssignmentsAsync(service, _logger, ct);
        if (string.Equals(objectClass, "calendarresource", StringComparison.OrdinalIgnoreCase))
            return EnumerateCalendarResourcesAsync(service, _logger, ct);
        throw new NotSupportedException(
            $"Google Workspace source does not support object class '{objectClass}'. Supported: {string.Join(", ", SupportedClasses)}.");
    }

    /// <summary>The native object classes this source can enumerate.</summary>
    public static readonly string[] SupportedClasses =
    {
        "user", "group", "organizationalUnit", "role", "domain",
        "mobiledevice", "chromeosdevice", "roleAssignment", "calendarresource"
    };

    /// <summary>True when this source can enumerate the given class (case-insensitive).</summary>
    public static bool IsSupportedClass(string objectClass)
    {
        foreach (var c in SupportedClasses)
            if (string.Equals(c, objectClass, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var creds = await GoogleCredentialReader.ReadAsync(_protector, _tenantId);
            if (creds is null) return new ConnectorTestResult { IsSuccessful = false, Message = "No 'google' credential stored." };
            var service = await GoogleCredentialReader.CreateServiceAsync(creds, readOnly: true);
            var req = service.Users.List();
            if (!string.IsNullOrEmpty(creds.Domain)) req.Domain = creds.Domain;
            else req.Customer = "my_customer";
            req.MaxResults = 1;
            var resp = await req.ExecuteAsync(cancellationToken);
            var count = resp.UsersValue?.Count ?? 0;
            return new ConnectorTestResult { IsSuccessful = true, Message = $"Connected as {creds.AdminEmail}. Sample fetch returned {count}." };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

    private static async IAsyncEnumerable<ConnectorObject> EnumerateUsersAsync(
        DirectoryService service, string? domain, int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var req = service.Users.List();
            if (!string.IsNullOrEmpty(domain)) req.Domain = domain;
            else req.Customer = "my_customer";
            req.MaxResults = pageSize;
            req.Projection = UsersResource.ListRequest.ProjectionEnum.Full;
            if (!string.IsNullOrEmpty(pageToken)) req.PageToken = pageToken;
            var resp = await req.ExecuteAsync(cancellationToken);
            if (resp.UsersValue != null)
                foreach (var u in resp.UsersValue) yield return ConvertUser(u);
            pageToken = resp.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));
    }

    private static async IAsyncEnumerable<ConnectorObject> EnumerateGroupsAsync(
        DirectoryService service, string? domain, int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var req = service.Groups.List();
            if (!string.IsNullOrEmpty(domain)) req.Domain = domain;
            else req.Customer = "my_customer";
            req.MaxResults = pageSize;
            if (!string.IsNullOrEmpty(pageToken)) req.PageToken = pageToken;
            var resp = await req.ExecuteAsync(cancellationToken);
            if (resp.GroupsValue != null)
            {
                foreach (var g in resp.GroupsValue)
                {
                    var members = await TryGetMembersAsync(service, g.Id, cancellationToken);
                    yield return ConvertGroup(g, members);
                }
            }
            pageToken = resp.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));
    }

    // Org units, roles, and domains hang off the customer ("my_customer"), not a
    // domain. Each first call is wrapped so a 403 (insufficient delegated scope)
    // logs a WARNING and yields nothing rather than aborting the whole run.
    private const string CustomerKey = "my_customer";

    private static async IAsyncEnumerable<ConnectorObject> EnumerateOrgUnitsAsync(
        DirectoryService service, ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        OrgUnits? resp = null;
        try
        {
            var req = service.Orgunits.List(CustomerKey);
            req.Type = OrgunitsResource.ListRequest.TypeEnum.All;
            resp = await req.ExecuteAsync(cancellationToken);
        }
        catch (GoogleApiException ex) when (IsForbidden(ex))
        {
            logger.LogWarning("Google Workspace: skipping class organizationalUnit — service account lacks admin.directory.orgunit.readonly (403).");
            yield break;
        }

        if (resp?.OrganizationUnits is null) yield break;
        foreach (var ou in resp.OrganizationUnits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(ou.OrgUnitId)) continue;
            var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["objectClass"] = "organizationalUnit",
                ["id"] = ou.OrgUnitId,
                ["objectGuid"] = ou.OrgUnitId
            };
            Set(attrs, "displayName", ou.Name);
            Set(attrs, "cn", ou.Name);
            Set(attrs, "orgUnitPath", ou.OrgUnitPath);
            Set(attrs, "dn", ou.OrgUnitPath);
            Set(attrs, "description", ou.Description);
            Set(attrs, "parentOrgUnitPath", ou.ParentOrgUnitPath);
            yield return new ConnectorObject
            {
                SourceId = ou.OrgUnitId,
                ObjectClass = "organizationalUnit",
                Attributes = attrs
            };
        }
    }

    private static async IAsyncEnumerable<ConnectorObject> EnumerateRolesAsync(
        DirectoryService service, ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            Roles? resp = null;
            try
            {
                var req = service.Roles.List(CustomerKey);
                req.MaxResults = 100;
                if (!string.IsNullOrEmpty(pageToken)) req.PageToken = pageToken;
                resp = await req.ExecuteAsync(cancellationToken);
            }
            catch (GoogleApiException ex) when (IsForbidden(ex))
            {
                logger.LogWarning("Google Workspace: skipping class role — service account lacks admin.directory.rolemanagement.readonly (403).");
                yield break;
            }

            if (resp?.Items != null)
            {
                foreach (var r in resp.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!r.RoleId.HasValue) continue;
                    var roleId = r.RoleId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["objectClass"] = "role",
                        ["id"] = roleId,
                        ["objectGuid"] = roleId
                    };
                    Set(attrs, "displayName", r.RoleName);
                    Set(attrs, "cn", r.RoleName);
                    Set(attrs, "roleName", r.RoleName);
                    Set(attrs, "description", r.RoleDescription);
                    if (r.IsSystemRole.HasValue) attrs["isSystemRole"] = r.IsSystemRole.Value;
                    if (r.IsSuperAdminRole.HasValue) attrs["isSuperAdminRole"] = r.IsSuperAdminRole.Value;
                    yield return new ConnectorObject
                    {
                        SourceId = roleId,
                        ObjectClass = "role",
                        Attributes = attrs
                    };
                }
            }
            pageToken = resp?.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));
    }

    // Domain's natural key is the domain name. The list endpoint is unpaged.
    private static async IAsyncEnumerable<ConnectorObject> EnumerateDomainsAsync(
        DirectoryService service, ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Domains2? resp = null;
        try
        {
            var req = service.Domains.List(CustomerKey);
            resp = await req.ExecuteAsync(cancellationToken);
        }
        catch (GoogleApiException ex) when (IsForbidden(ex))
        {
            logger.LogWarning("Google Workspace: skipping class domain — service account lacks admin.directory.domain.readonly (403).");
            yield break;
        }

        if (resp?.Domains == null) yield break;
        foreach (var d in resp.Domains)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(d.DomainName)) continue;
            var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["objectClass"] = "domain",
                ["id"] = d.DomainName,
                ["objectGuid"] = d.DomainName
            };
            Set(attrs, "displayName", d.DomainName);
            Set(attrs, "cn", d.DomainName);
            Set(attrs, "domainName", d.DomainName);
            if (d.IsPrimary.HasValue) attrs["isPrimary"] = d.IsPrimary.Value;
            if (d.Verified.HasValue) attrs["verified"] = d.Verified.Value;
            yield return new ConnectorObject
            {
                SourceId = d.DomainName,
                ObjectClass = "domain",
                Attributes = attrs
            };
        }
    }

    // ── Device fleet + delegated-admin classes. Each pages off the customer and
    //    skips itself with a scope-hint warning on 403, mirroring role/orgUnit. ──

    private static async IAsyncEnumerable<ConnectorObject> EnumerateMobileDevicesAsync(
        DirectoryService service, ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            MobileDevices? resp = null;
            try
            {
                var req = service.Mobiledevices.List(CustomerKey);
                req.MaxResults = 100;
                if (!string.IsNullOrEmpty(pageToken)) req.PageToken = pageToken;
                resp = await req.ExecuteAsync(cancellationToken);
            }
            catch (GoogleApiException ex) when (IsForbidden(ex))
            {
                logger.LogWarning("Google Workspace: skipping class mobiledevice — service account lacks admin.directory.device.mobile.readonly (403).");
                yield break;
            }

            if (resp?.Mobiledevices != null)
            {
                foreach (var d in resp.Mobiledevices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var id = d.ResourceId ?? d.DeviceId;
                    if (string.IsNullOrEmpty(id)) continue;
                    var owner = d.Email?.FirstOrDefault();
                    var name = d.Name?.FirstOrDefault() ?? d.Model ?? id;
                    var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["objectClass"] = "mobiledevice",
                        ["id"] = id,
                        ["objectGuid"] = id
                    };
                    Set(attrs, "displayName", name);
                    Set(attrs, "cn", name);
                    Set(attrs, "model", d.Model);
                    Set(attrs, "os", d.Os);
                    Set(attrs, "deviceType", d.Type);
                    Set(attrs, "status", d.Status);
                    Set(attrs, "serialNumber", d.SerialNumber);
                    Set(attrs, "ownerEmail", owner);
                    Set(attrs, "lastSync", d.LastSyncRaw);
                    yield return new ConnectorObject { SourceId = id!, ObjectClass = "mobiledevice", Attributes = attrs };
                }
            }
            pageToken = resp?.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));
    }

    private static async IAsyncEnumerable<ConnectorObject> EnumerateChromeOsDevicesAsync(
        DirectoryService service, ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChromeOsDevices? resp = null;
            try
            {
                var req = service.Chromeosdevices.List(CustomerKey);
                req.MaxResults = 100;
                if (!string.IsNullOrEmpty(pageToken)) req.PageToken = pageToken;
                resp = await req.ExecuteAsync(cancellationToken);
            }
            catch (GoogleApiException ex) when (IsForbidden(ex))
            {
                logger.LogWarning("Google Workspace: skipping class chromeosdevice — service account lacks admin.directory.device.chromeos.readonly (403).");
                yield break;
            }

            if (resp?.Chromeosdevices != null)
            {
                foreach (var d in resp.Chromeosdevices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var id = d.DeviceId;
                    if (string.IsNullOrEmpty(id)) continue;
                    var name = d.AnnotatedAssetId ?? d.SerialNumber ?? id;
                    var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["objectClass"] = "chromeosdevice",
                        ["id"] = id,
                        ["objectGuid"] = id
                    };
                    Set(attrs, "displayName", name);
                    Set(attrs, "cn", name);
                    Set(attrs, "serialNumber", d.SerialNumber);
                    Set(attrs, "status", d.Status);
                    Set(attrs, "model", d.Model);
                    Set(attrs, "osVersion", d.OsVersion);
                    Set(attrs, "platformVersion", d.PlatformVersion);
                    Set(attrs, "macAddress", d.MacAddress);
                    Set(attrs, "annotatedUser", d.AnnotatedUser);
                    Set(attrs, "annotatedLocation", d.AnnotatedLocation);
                    Set(attrs, "orgUnitPath", d.OrgUnitPath);
                    Set(attrs, "lastSync", d.LastSyncRaw);
                    yield return new ConnectorObject { SourceId = id!, ObjectClass = "chromeosdevice", Attributes = attrs };
                }
            }
            pageToken = resp?.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));
    }

    private static async IAsyncEnumerable<ConnectorObject> EnumerateRoleAssignmentsAsync(
        DirectoryService service, ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            RoleAssignments? resp = null;
            try
            {
                var req = service.RoleAssignments.List(CustomerKey);
                req.MaxResults = 200;
                if (!string.IsNullOrEmpty(pageToken)) req.PageToken = pageToken;
                resp = await req.ExecuteAsync(cancellationToken);
            }
            catch (GoogleApiException ex) when (IsForbidden(ex))
            {
                logger.LogWarning("Google Workspace: skipping class roleAssignment — service account lacks admin.directory.rolemanagement.readonly (403).");
                yield break;
            }

            if (resp?.Items != null)
            {
                foreach (var ra in resp.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ra.RoleAssignmentId.HasValue) continue;
                    var id = ra.RoleAssignmentId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var roleId = ra.RoleId?.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["objectClass"] = "roleassignment",
                        ["id"] = id,
                        ["objectGuid"] = id
                    };
                    Set(attrs, "displayName", $"Role {roleId} → {ra.AssignedTo}");
                    Set(attrs, "cn", id);
                    Set(attrs, "roleId", roleId);
                    Set(attrs, "assignedTo", ra.AssignedTo);
                    Set(attrs, "scopeType", ra.ScopeType);
                    Set(attrs, "orgUnitId", ra.OrgUnitId);
                    yield return new ConnectorObject { SourceId = id, ObjectClass = "roleAssignment", Attributes = attrs };
                }
            }
            pageToken = resp?.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));
    }

    private static async IAsyncEnumerable<ConnectorObject> EnumerateCalendarResourcesAsync(
        DirectoryService service, ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            CalendarResources? resp = null;
            try
            {
                var req = service.Resources.Calendars.List(CustomerKey);
                req.MaxResults = 100;
                if (!string.IsNullOrEmpty(pageToken)) req.PageToken = pageToken;
                resp = await req.ExecuteAsync(cancellationToken);
            }
            catch (GoogleApiException ex) when (IsForbidden(ex))
            {
                logger.LogWarning("Google Workspace: skipping class calendarresource — service account lacks admin.directory.resource.calendar.readonly (403).");
                yield break;
            }

            if (resp?.Items != null)
            {
                foreach (var r in resp.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var id = r.ResourceId;
                    if (string.IsNullOrEmpty(id)) continue;
                    var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["objectClass"] = "calendarresource",
                        ["id"] = id,
                        ["objectGuid"] = id
                    };
                    Set(attrs, "displayName", r.ResourceName);
                    Set(attrs, "cn", r.ResourceName);
                    Set(attrs, "resourceType", r.ResourceType);
                    Set(attrs, "resourceEmail", r.ResourceEmail);
                    Set(attrs, "description", r.ResourceDescription);
                    Set(attrs, "buildingId", r.BuildingId);
                    Set(attrs, "floorName", r.FloorName);
                    if (r.Capacity.HasValue) attrs["capacity"] = r.Capacity.Value;
                    yield return new ConnectorObject { SourceId = id!, ObjectClass = "calendarresource", Attributes = attrs };
                }
            }
            pageToken = resp?.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));
    }

    private static bool IsForbidden(GoogleApiException ex) =>
        ex.HttpStatusCode == HttpStatusCode.Forbidden
        || (ex.Error is { Code: 403 });

    private static async Task<List<string>> TryGetMembersAsync(DirectoryService service, string? groupKey, CancellationToken ct)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(groupKey)) return result;
        try
        {
            string? token = null;
            do
            {
                var req = service.Members.List(groupKey);
                req.MaxResults = 200;
                if (!string.IsNullOrEmpty(token)) req.PageToken = token;
                var resp = await req.ExecuteAsync(ct);
                if (resp.MembersValue != null)
                    foreach (var m in resp.MembersValue)
                        if (!string.IsNullOrEmpty(m.Id)) result.Add(m.Id);
                token = resp.NextPageToken;
            } while (!string.IsNullOrEmpty(token));
        }
        catch { /* best effort */ }
        return result;
    }

    private static ConnectorObject ConvertUser(User user)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = "user",
            ["id"] = user.Id,
            ["objectGuid"] = user.Id
        };
        Set(attrs, "mail", user.PrimaryEmail);
        Set(attrs, "email", user.PrimaryEmail);
        Set(attrs, "userPrincipalName", user.PrimaryEmail);
        Set(attrs, "userName", user.PrimaryEmail);
        Set(attrs, "cn", user.PrimaryEmail);
        Set(attrs, "dn", user.OrgUnitPath);
        Set(attrs, "orgUnitPath", user.OrgUnitPath);
        if (user.Name != null)
        {
            Set(attrs, "givenName", user.Name.GivenName);
            Set(attrs, "surname", user.Name.FamilyName);
            Set(attrs, "sn", user.Name.FamilyName);
            Set(attrs, "familyName", user.Name.FamilyName);
            Set(attrs, "displayName", user.Name.FullName);
        }
        if (user.Suspended.HasValue)
        {
            attrs["accountEnabled"] = !user.Suspended.Value;
            attrs["active"] = !user.Suspended.Value;
            attrs["userAccountControl"] = user.Suspended.Value ? 514 : 512;
        }
        if (user.IsAdmin.HasValue && user.IsAdmin.Value) attrs["isAdmin"] = true;
        if (user.LastLoginTimeDateTimeOffset.HasValue) attrs["lastLoginTime"] = user.LastLoginTimeDateTimeOffset.Value.ToString("o");
        if (user.CreationTimeDateTimeOffset.HasValue) attrs["whenCreated"] = user.CreationTimeDateTimeOffset.Value.ToString("o");

        // Organizations / Phones / Relations — Google SDK returns these as
        // IList<object>; JSON-roundtrip into typed values.
        if (user.Organizations is System.Collections.IList orgList && orgList.Count > 0)
        {
            using var orgDoc = JsonDocument.Parse(JsonSerializer.Serialize(orgList[0]));
            Set(attrs, "department", Str(orgDoc.RootElement, "department"));
            Set(attrs, "title", Str(orgDoc.RootElement, "title"));
            Set(attrs, "jobTitle", Str(orgDoc.RootElement, "title"));
            Set(attrs, "company", Str(orgDoc.RootElement, "name"));
            Set(attrs, "companyName", Str(orgDoc.RootElement, "name"));
        }
        if (user.Phones is System.Collections.IList phoneList)
        {
            foreach (var pObj in phoneList)
            {
                using var pDoc = JsonDocument.Parse(JsonSerializer.Serialize(pObj));
                var type = Str(pDoc.RootElement, "type")?.ToLowerInvariant();
                var val = Str(pDoc.RootElement, "value");
                switch (type)
                {
                    case "work": Set(attrs, "telephoneNumber", val); break;
                    case "mobile": Set(attrs, "mobilePhone", val); Set(attrs, "mobile", val); break;
                    case "home": Set(attrs, "homePhone", val); break;
                }
            }
        }
        if (user.Relations is System.Collections.IList relList)
        {
            foreach (var r in relList)
            {
                using var rDoc = JsonDocument.Parse(JsonSerializer.Serialize(r));
                if (string.Equals(Str(rDoc.RootElement, "type"), "manager", StringComparison.OrdinalIgnoreCase))
                {
                    Set(attrs, "manager", Str(rDoc.RootElement, "value"));
                    break;
                }
            }
        }
        return new ConnectorObject
        {
            SourceId = user.Id ?? user.PrimaryEmail ?? string.Empty,
            ObjectClass = "User",
            Attributes = attrs
        };
    }

    private static ConnectorObject ConvertGroup(Group group, List<string> memberIds)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = "group",
            ["id"] = group.Id,
            ["objectGuid"] = group.Id
        };
        Set(attrs, "displayName", group.Name);
        Set(attrs, "cn", group.Name);
        Set(attrs, "mail", group.Email);
        Set(attrs, "email", group.Email);
        Set(attrs, "description", group.Description);
        Set(attrs, "Type", "Distribution"); // Google groups are mailing-list shaped
        if (group.DirectMembersCount.HasValue) attrs["memberCount"] = group.DirectMembersCount.Value;
        if (memberIds.Count > 0) attrs["members"] = memberIds;
        return new ConnectorObject
        {
            SourceId = group.Id ?? group.Email ?? string.Empty,
            ObjectClass = "Group",
            Attributes = attrs
        };
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Set(Dictionary<string, object?> dict, string key, object? value)
    {
        if (value is null) return;
        if (value is string s && string.IsNullOrEmpty(s)) return;
        dict[key] = value;
    }
}
