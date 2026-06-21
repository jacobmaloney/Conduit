using System;
using System.Threading.Tasks;
using Conduit.Core.Models;
using Conduit.DataAccess.Repositories;
using Microsoft.Extensions.Configuration;

namespace Conduit.Web.Services
{
    /// <summary>
    /// IdentityCenter-connection license gate (now ENFORCED — no longer inert).
    ///
    /// ENTITLEMENT MODEL (Jacob's chosen mechanism — a VALIDATED IC LINK): an
    /// IdentityCenter connection is licensed iff Conduit has a SUCCESSFUL
    /// AUTHENTICATED HANDSHAKE to a real IC instance for it. The IC connection's
    /// Test (an authenticated GET only an authenticating IC answers) IS the proof;
    /// on success the validation is recorded durably on the Tenant row
    /// (IcEntitlementValidatedAt + IcEntitlementBaseUrl) so it survives restarts.
    /// Pre-existing IC connections are GRANDFATHERED by the V30 migration.
    ///
    /// DEV/OVERRIDE escape hatch (clearly named): a config flag force-unlocks the
    /// gate for ALL IC connections WITHOUT a handshake. Dev only — never enable in
    /// production. Replaces the old inert single-flag check.
    ///   "Conduit:Entitlements:IdentityCenterConnectionDevOverride": true
    ///
    /// All three enforcement points consult the SAME logic (Conduit.Core
    /// <see cref="IcEntitlement"/>): the connection UI (locked-with-upsell), the
    /// server-side tenant create/update use-check, and the orchestrator run-guard.
    /// </summary>
    public class IcEntitlementService
    {
        /// <summary>Back-compat alias of the dev-override key name.</summary>
        public const string ConfigKey = IcEntitlement.DevOverrideConfigKey;

        private readonly IConfiguration _config;
        private readonly TenantRepository _tenants;

        public IcEntitlementService(IConfiguration config, TenantRepository tenants)
        {
            _config = config;
            _tenants = tenants;
        }

        /// <summary>The SystemType string that the gate governs.</summary>
        public const string IdentityCenterSystemType = IcEntitlement.IdentityCenterSystemType;

        /// <summary>
        /// True only when the DEV OVERRIDE is on. This is the escape hatch — it
        /// unlocks the gate for every IC connection without a handshake. Defaults
        /// to FALSE. Production installs leave this off and rely on validated links.
        /// </summary>
        public bool IsDevOverrideEnabled =>
            _config.GetValue<bool>(IcEntitlement.DevOverrideConfigKey, false);

        /// <summary>
        /// True if the system type is governed by the IC gate.
        /// </summary>
        public bool IsIdentityCenterType(string? systemType) =>
            IcEntitlement.IsIdentityCenterType(systemType);

        /// <summary>
        /// Is THIS connection entitled to be USED (sync runs, server-side use)?
        /// Entitled when the dev override is on OR the tenant has a validated link
        /// (handshake-recorded or grandfathered). Non-IC tenants are always "true"
        /// here — the gate only governs IdentityCenter-typed connections.
        /// </summary>
        public async Task<bool> IsConnectionUsableAsync(Guid tenantId)
        {
            var tenant = await _tenants.GetByIdAsync(tenantId);
            if (tenant is null) return false;
            if (!IcEntitlement.IsIdentityCenterType(tenant.SystemType)) return true;
            return IcEntitlement.IsValidated(tenant, IsDevOverrideEnabled);
        }

        /// <summary>
        /// Tenant-object overload (no DB round-trip) for callers that already hold
        /// the row. Same semantics as <see cref="IsConnectionUsableAsync(Guid)"/>.
        /// </summary>
        public bool IsConnectionUsable(Tenant? tenant)
        {
            if (tenant is null) return false;
            if (!IcEntitlement.IsIdentityCenterType(tenant.SystemType)) return true;
            return IcEntitlement.IsValidated(tenant, IsDevOverrideEnabled);
        }

        /// <summary>
        /// Records a validated IC link after a successful handshake (delegates to
        /// the repo). Call ONLY on a genuinely successful Test against an IC-typed
        /// connection — the caller owns confirming the handshake reached an
        /// authenticating IC.
        /// </summary>
        public Task<bool> MarkValidatedAsync(Guid tenantId, string? validatedBaseUrl) =>
            _tenants.SetEntitlementValidatedAsync(tenantId, validatedBaseUrl);
    }
}
