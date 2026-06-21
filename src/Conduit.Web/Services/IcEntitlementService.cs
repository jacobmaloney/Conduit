using Microsoft.Extensions.Configuration;

namespace Conduit.Web.Services
{
    /// <summary>
    /// SAFE FIRST STEP toward the IdentityCenter-connection license gate (see the
    /// gate plan in PR/commit notes). Conduit is the FREE product and has NO licensing
    /// concept today; this is the single, default-LOCKED entitlement check that future
    /// enforcement points will call.
    ///
    /// IMPORTANT: nothing is enforced yet. This service is INERT until (a) an operator
    /// sets the flag AND (b) the gate is wired at the connection-create UI + a
    /// server-side validation + the orchestrator run-guard (all enumerated in the plan).
    /// Wiring those points is deliberately deferred to Jacob's approval because the
    /// "how do we decide licensed?" mechanism (license key vs. validated IC link vs.
    /// config flag) is a product decision.
    ///
    /// Default: LOCKED (free tier). Flip via configuration:
    ///   "Conduit:Entitlements:IdentityCenterConnection": true
    /// or env var  Conduit__Entitlements__IdentityCenterConnection=true
    /// </summary>
    public class IcEntitlementService
    {
        public const string ConfigKey = "Conduit:Entitlements:IdentityCenterConnection";

        private readonly IConfiguration _config;

        public IcEntitlementService(IConfiguration config) => _config = config;

        /// <summary>
        /// True only when this installation is entitled to create/use IdentityCenter
        /// connections (the licensed IC integration). Defaults to FALSE (free tier).
        /// </summary>
        public bool IsIdentityCenterConnectionLicensed =>
            _config.GetValue<bool>(ConfigKey, false);

        /// <summary>The SystemType string that the gate governs.</summary>
        public const string IdentityCenterSystemType = "IdentityCenter";
    }
}
