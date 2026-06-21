using System;

namespace Conduit.Core.Models
{
    /// <summary>
    /// Single source of truth for the IdentityCenter-connection license gate.
    ///
    /// ENTITLEMENT MODEL (Jacob's chosen mechanism — a VALIDATED IC LINK):
    /// an IdentityCenter connection is "licensed" iff Conduit has completed a
    /// SUCCESSFUL AUTHENTICATED HANDSHAKE to a real IdentityCenter instance for
    /// that connection. Owning an IC to validate against IS the entitlement. The
    /// handshake is the IC connection's Test (an authenticated GET that only an
    /// authenticating IC answers); on success the validation is recorded durably
    /// on the Tenant row (<see cref="Tenant.IcEntitlementValidatedAt"/> +
    /// <see cref="Tenant.IcEntitlementBaseUrl"/>) so it survives restarts.
    ///
    /// A DEV/OVERRIDE escape hatch remains: a config flag that force-unlocks the
    /// gate without a handshake (clearly named — for dev only). It must NOT be on
    /// in production.
    ///
    /// SAFE-BY-DEFAULT: existing IC connections are GRANDFATHERED by the V30
    /// migration (it stamps IcEntitlementValidatedAt on every pre-existing
    /// IdentityCenter tenant), so flipping enforcement never breaks a live install
    /// or the parity demo.
    /// </summary>
    public static class IcEntitlement
    {
        /// <summary>The SystemType string the gate governs.</summary>
        public const string IdentityCenterSystemType = "IdentityCenter";

        /// <summary>
        /// DEV/OVERRIDE config key. When true, the gate is force-unlocked for ALL
        /// IC connections WITHOUT a handshake. Dev escape hatch only — never enable
        /// in production. Flip via configuration:
        ///   "Conduit:Entitlements:IdentityCenterConnectionDevOverride": true
        /// or env var Conduit__Entitlements__IdentityCenterConnectionDevOverride=true
        /// </summary>
        public const string DevOverrideConfigKey =
            "Conduit:Entitlements:IdentityCenterConnectionDevOverride";

        /// <summary>
        /// True if the SystemType is governed by the IC gate (case-insensitive).
        /// </summary>
        public static bool IsIdentityCenterType(string? systemType) =>
            string.Equals(systemType, IdentityCenterSystemType, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The core check: is THIS tenant entitled to be USED as an IdentityCenter
        /// connection? Returns true when either
        ///   (a) the dev override is on (escape hatch), OR
        ///   (b) the tenant has a recorded validated link (IcEntitlementValidatedAt
        ///       is set — by a successful handshake or by V30 grandfathering).
        /// A null tenant is never entitled.
        /// </summary>
        public static bool IsValidated(Tenant? tenant, bool devOverride)
        {
            if (devOverride) return true;
            if (tenant is null) return false;
            return tenant.IcEntitlementValidatedAt.HasValue;
        }
    }
}
