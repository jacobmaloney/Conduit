using System;

namespace Conduit.Core.Models
{
    /// <summary>
    /// Internal model for a tenant. UI label = "Connected System" — never expose
    /// "Tenant" to end users. A Connected System represents one external identity
    /// target (a customer's Entra ID, a Google Workspace instance, a demo emulator,
    /// etc.).
    /// </summary>
    public class Tenant
    {
        public Guid Id { get; set; }

        /// <summary>Display name, e.g. "IT Helpdesk Portal".</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>URL-safe identifier, e.g. "it-helpdesk". Unique.</summary>
        public string Slug { get; set; } = string.Empty;

        public string? Description { get; set; }

        /// <summary>"Emulator" (amber badge) or "Real" (green badge).</summary>
        public string SystemType { get; set; } = "Emulator";

        /// <summary>Optional domain hint, e.g. "it.demo.local".</summary>
        public string? Domain { get; set; }

        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Legal hold flag. When true, ClearDataAsync and DeleteAsync refuse to run —
        /// the operator must explicitly clear the flag (an audit-logged action) first.
        /// Intended for Connected Systems that hold historical, non-reproducible data.
        /// </summary>
        public bool LegalHold { get; set; } = false;

        /// <summary>
        /// IC license gate (see <see cref="IcEntitlement"/>). When set, this
        /// IdentityCenter connection has a VALIDATED LINK — a successful authenticated
        /// handshake to a real IC instance was recorded (or the row was grandfathered
        /// by the V30 migration). NULL = not yet validated; an unvalidated IC
        /// connection cannot be USED for sync runs. Only meaningful when
        /// SystemType == "IdentityCenter"; ignored for every other system type.
        /// </summary>
        public DateTime? IcEntitlementValidatedAt { get; set; }

        /// <summary>
        /// The IC base URL that the validating handshake reached. Informational /
        /// audit — lets the UI show "validated against https://…". NULL until a
        /// handshake validates (grandfathered rows leave this NULL).
        /// </summary>
        public string? IcEntitlementBaseUrl { get; set; }

        public DateTime Created { get; set; }
        public DateTime LastModified { get; set; }
    }

    /// <summary>
    /// API token authorization scope. Stored as text in ApiTokens.Scope.
    /// </summary>
    public enum ApiTokenScope
    {
        /// <summary>All tenants, all emulators. Reserved for portal admins.</summary>
        Admin,
        /// <summary>Scoped to a single TenantId.</summary>
        Tenant,
        /// <summary>For /ars/v1/ inbound endpoint only.</summary>
        ArsProxy
    }
}
