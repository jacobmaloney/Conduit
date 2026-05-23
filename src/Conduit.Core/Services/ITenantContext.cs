using System;
using Conduit.Core.Models;

namespace Conduit.Core.Services
{
    /// <summary>
    /// Per-request resolution of the active tenant (UI label: "Connected System").
    /// Implemented in <c>Conduit.Web.Services.TenantContext</c> using HttpContext;
    /// kept in Core so data-access repositories can depend on it without taking an
    /// ASP.NET Core reference.
    /// </summary>
    public interface ITenantContext
    {
        /// <summary>True when the caller is authenticated and tenant scope has been resolved.</summary>
        bool IsResolved { get; }

        /// <summary>
        /// Tenant the caller is scoped to. NULL when Scope == Admin (caller may operate
        /// across all tenants) or when no token has been validated yet.
        /// </summary>
        Guid? TenantId { get; }

        /// <summary>Scope of the active token. Defaults to <see cref="ApiTokenScope.Tenant"/>.</summary>
        ApiTokenScope Scope { get; }

        /// <summary>True if the active token has admin (cross-tenant) authority.</summary>
        bool IsAdmin { get; }

        /// <summary>True if the active token is the ARS proxy scope.</summary>
        bool IsArsProxy { get; }

        /// <summary>
        /// Convenience: tenant filter to apply to repository queries. Returns the
        /// active TenantId, or throws when the caller is unscoped (Admin). Callers
        /// that may operate across tenants should check <see cref="IsAdmin"/> first.
        /// </summary>
        Guid RequireTenantId();
    }
}
