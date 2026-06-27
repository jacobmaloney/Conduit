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

        /// <summary>
        /// Inbound-proxy target table for IdentityCenter-typed connections:
        /// "Objects" | "Identities". Consumed ONLY on the REST/SCIM proxy write
        /// path (the connection's sink), where there is no Sync Project to carry
        /// the V22 per-side SourceTable/SinkTable choice. The proxy stamps
        /// IdentityCenterTableContext.Sink from this value before the IC sink
        /// write. REQUIRED in the wizard for IdentityCenter connections; ignored
        /// by every other connector type. NULL = Objects (back-compat).
        /// </summary>
        public string? TargetTable { get; set; }

        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Legal hold flag. When true, ClearDataAsync and DeleteAsync refuse to run —
        /// the operator must explicitly clear the flag (an audit-logged action) first.
        /// Intended for Connected Systems that hold historical, non-reproducible data.
        /// </summary>
        public bool LegalHold { get; set; } = false;

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
