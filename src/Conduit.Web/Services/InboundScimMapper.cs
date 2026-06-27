using System;
using System.Collections.Generic;
using System.Linq;
using Conduit.Core.Models;
using Conduit.Sync.Connectors;

namespace Conduit.Web.Services
{
    /// <summary>
    /// Phase 1 inbound-proxy mapping layer. Translates an inbound SCIM / generic-REST
    /// payload into a tenant-neutral <see cref="ConnectorObject"/> whose attribute bag
    /// uses the SAME canonical keys every Conduit sink already understands
    /// (userName / sAMAccountName / displayName / givenName / sn / mail /
    /// userPrincipalName / department / title / manager / employeeID / active …).
    ///
    /// This is the single funnel: the SCIM Users/Groups controllers and the
    /// /api/v1/users controller all map through here before forwarding the object to a
    /// connection's sink via the unified provision path. Keeping the canonical key set
    /// identical to <c>ProvisioningController.BuildConnectorObject</c> means each sink's
    /// existing attribute projection (AD AppendIfPresent, IC PostObjectBatch /
    /// PostIdentityBatch, Entra Graph map) handles a proxied object exactly as it
    /// handles a synced one — no per-sink inbound code.
    ///
    /// The mapper is deliberately SINK-AGNOSTIC: it emits the union of well-known keys
    /// and lets each sink ignore the ones it doesn't use (every sink's attribute
    /// projection is allow-list / TryGet based). It does NOT decide Objects-vs-Identities
    /// — that is the connection's TargetTable, stamped on the ambient context by the
    /// controller before the sink call.
    /// </summary>
    public static class InboundScimMapper
    {
        /// <summary>
        /// Map a SCIM User to a ConnectorObject (objectClass "user"). The SCIM
        /// <c>externalId</c> (if present) becomes the ConnectorObject.SourceId so the
        /// sink keys create/update on a stable caller-supplied id; otherwise the
        /// userName is used as the deterministic key.
        /// </summary>
        public static ConnectorObject FromScimUser(ScimUser user)
        {
            var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            Put(attrs, "userName", user.UserName);
            Put(attrs, "sAMAccountName", user.UserName);
            Put(attrs, "displayName", user.DisplayName);
            Put(attrs, "givenName", user.Name?.GivenName);
            Put(attrs, "sn", user.Name?.FamilyName);
            Put(attrs, "title", user.Title);
            Put(attrs, "userType", user.UserType);
            Put(attrs, "preferredLanguage", user.PreferredLanguage);
            Put(attrs, "locale", user.Locale);
            Put(attrs, "timezone", user.Timezone);
            attrs["active"] = user.Active;

            // Password is write-only and only meaningful to sinks that provision a
            // credential (AD over LDAPS); harmless to forward — others ignore it.
            Put(attrs, "password", user.Password);

            // Primary email → mail; default UPN to the primary email when the payload
            // has no explicit UPN (same convention as BuildConnectorObject).
            var primaryEmail = user.Emails?.FirstOrDefault(e => e.Primary)?.Value
                               ?? user.Emails?.FirstOrDefault()?.Value;
            if (!string.IsNullOrEmpty(primaryEmail))
            {
                Put(attrs, "mail", primaryEmail);
                Put(attrs, "userPrincipalName", primaryEmail);
            }

            var primaryPhone = user.PhoneNumbers?.FirstOrDefault(p => p.Primary)?.Value
                               ?? user.PhoneNumbers?.FirstOrDefault()?.Value;
            Put(attrs, "telephoneNumber", primaryPhone);

            // Enterprise extension → typed columns / AD attributes.
            var ext = user.EnterpriseExtension;
            if (ext is not null)
            {
                Put(attrs, "department", ext.Department);
                Put(attrs, "employeeID", ext.EmployeeNumber);
                Put(attrs, "employeeNumber", ext.EmployeeNumber);
                Put(attrs, "company", ext.Organization);
                Put(attrs, "division", ext.Division);
                Put(attrs, "costCenter", ext.CostCenter);
                Put(attrs, "manager", ext.Manager?.Value);
            }

            return new ConnectorObject
            {
                SourceId = FirstNonEmpty(user.ExternalId, user.UserName),
                ObjectClass = "user",
                Attributes = attrs
            };
        }

        /// <summary>
        /// Map a SCIM Group to a ConnectorObject (objectClass "group"). Membership is
        /// carried on a "_members" attribute (semicolon-joined member ids) so a sink
        /// that supports membership can resolve it; the create path itself only needs
        /// the group's identity. SourceId = externalId ?? displayName.
        /// </summary>
        public static ConnectorObject FromScimGroup(ScimGroup group)
        {
            var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            Put(attrs, "displayName", group.DisplayName);
            // AD-shaped sinks key a group's name on cn / sAMAccountName.
            Put(attrs, "cn", group.DisplayName);
            Put(attrs, "sAMAccountName", group.DisplayName);
            Put(attrs, "description", group.Description);
            Put(attrs, "groupType", group.Type);
            Put(attrs, "owner", group.Owner?.Value);

            if (group.Members is { Count: > 0 })
            {
                var memberIds = group.Members
                    .Select(m => m.Value)
                    .Where(v => !string.IsNullOrEmpty(v));
                var joined = string.Join(";", memberIds);
                if (!string.IsNullOrEmpty(joined))
                    attrs["_members"] = joined;
            }

            return new ConnectorObject
            {
                SourceId = FirstNonEmpty(group.ExternalId, group.DisplayName),
                ObjectClass = "group",
                Attributes = attrs
            };
        }

        // ─── Reverse mapping (Item 3: GET read-through) ─────────────────────────
        //
        // Map a ConnectorObject read from a connection's SOURCE adapter back into a
        // SCIM resource so the inbound proxy can serve GET /Users|/Groups from the
        // external system. SINK-AGNOSTIC by the same principle as the forward map:
        // read the union of well-known source attribute keys (AD givenName/sn/mail,
        // IC displayName/userName/userPrincipalName/isActive, Entra-isms) case-
        // insensitively, emitting whatever is present. The resource Id is the
        // ConnectorObject.SourceId — for an IC Objects source this is the row's
        // SourceUniqueId; the proxy is responsible for choosing what id the caller
        // round-trips (see the controller). Membership is NOT resolved here (groups
        // come back without expanded members on a list read).

        public static ScimUser ToScimUser(ConnectorObject obj)
        {
            var u = new ScimUser
            {
                Id = obj.SourceId,
                UserName = First(obj, "userName", "sAMAccountName", "username") ?? obj.SourceId,
                DisplayName = First(obj, "displayName"),
                Title = First(obj, "title", "jobTitle"),
                UserType = First(obj, "userType"),
            };

            var given = First(obj, "givenName", "firstName");
            var family = First(obj, "sn", "lastName", "familyName");
            if (given is not null || family is not null)
                u.Name = new ScimName { GivenName = given, FamilyName = family };

            var mail = First(obj, "mail", "email", "primaryEmail", "userPrincipalName");
            if (!string.IsNullOrEmpty(mail))
                u.Emails = new System.Collections.Generic.List<ScimEmail> { new() { Value = mail, Type = "work", Primary = true } };

            // active: prefer a typed bool attribute, else parse a string form.
            u.Active = ReadBool(obj, true, "active", "isActive", "accountEnabled");

            var dept = First(obj, "department");
            var empId = First(obj, "employeeID", "employeeId", "employeeNumber");
            var manager = First(obj, "manager");
            if (dept is not null || empId is not null || manager is not null)
            {
                u.EnterpriseExtension = new ScimEnterpriseUser
                {
                    Department = dept,
                    EmployeeNumber = empId,
                    Manager = manager is null ? null : new ScimManager { Value = manager }
                };
                if (!u.Schemas.Contains("urn:ietf:params:scim:schemas:extension:enterprise:2.0:User"))
                    u.Schemas.Add("urn:ietf:params:scim:schemas:extension:enterprise:2.0:User");
            }

            return u;
        }

        public static ScimGroup ToScimGroup(ConnectorObject obj)
        {
            var g = new ScimGroup
            {
                Id = obj.SourceId,
                DisplayName = First(obj, "displayName", "cn", "sAMAccountName") ?? obj.SourceId,
                Description = First(obj, "description"),
                Type = First(obj, "groupType", "type"),
            };
            return g;
        }

        /// <summary>First non-empty value across the given attribute keys (case-insensitive).</summary>
        private static string? First(ConnectorObject obj, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (obj.Attributes.TryGetValue(k, out var v) && v is not null)
                {
                    var s = v.ToString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            return null;
        }

        private static bool ReadBool(ConnectorObject obj, bool fallback, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (!obj.Attributes.TryGetValue(k, out var v) || v is null) continue;
                if (v is bool b) return b;
                if (bool.TryParse(v.ToString(), out var parsed)) return parsed;
            }
            return fallback;
        }

        private static void Put(Dictionary<string, object?> d, string key, string? value)
        {
            if (!string.IsNullOrEmpty(value)) d[key] = value;
        }

        private static string FirstNonEmpty(params string?[] candidates)
        {
            foreach (var c in candidates)
                if (!string.IsNullOrEmpty(c)) return c!;
            return string.Empty;
        }
    }
}
