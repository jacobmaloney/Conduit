using System.Collections.Generic;
using System.Linq;
using Conduit.Sync.Connectors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Conduit.Web.Controllers
{
    /// <summary>
    /// Read-only connector capability catalog. The LIVE authority behind the marketing
    /// site's connector matrix; the static wwwroot/connector-capabilities.json is a
    /// build-time snapshot of the SAME <see cref="ConnectorCapabilityDescriptor.From"/>
    /// mapper, so the endpoint and the file cannot drift.
    ///
    /// Auth mirrors the other read API controllers (<see cref="ApiV1SyncRunsController"/>):
    /// authenticated Bearer <c>scim_*</c> token + the shared <c>scim</c> rate limiter.
    /// The catalog is non-sensitive metadata, so no per-tenant scoping is applied.
    /// </summary>
    [ApiController]
    [Route("api/connectors")]
    [Authorize]
    [EnableRateLimiting("scim")]
    public class ConnectorCapabilitiesController : ControllerBase
    {
        private readonly ConnectorRegistry _registry;

        public ConnectorCapabilitiesController(ConnectorRegistry registry)
        {
            _registry = registry;
        }

        [HttpGet("capabilities")]
        public ActionResult<IEnumerable<ConnectorCapabilityDescriptor>> GetCapabilities() =>
            Ok(_registry.All
                .Select(ConnectorCapabilityDescriptor.From)
                .OrderBy(d => d.SystemType)
                .ToList());
    }
}
