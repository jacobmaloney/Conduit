using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Conduit.Web.Controllers
{
    /// <summary>
    /// SCIM Service Provider Configuration endpoint
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Produces("application/scim+json")]
    [Route("scim/v2/ServiceProviderConfig")]
    [Route("scim/v2/t/{slug}/ServiceProviderConfig")]
    [EnableRateLimiting("anon")]
    public class ServiceProviderConfigController : ControllerBase
    {
        [HttpGet]
        public IActionResult GetConfig()
        {
            var config = new
            {
                schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig" },
                documentationUri = (string?)null,
                patch = new { supported = true },
                bulk = new { supported = false, maxOperations = 0, maxPayloadSize = 0 },
                filter = new { supported = true, maxResults = 1000 },
                changePassword = new { supported = false },
                sort = new { supported = true },
                etag = new { supported = false },
                authenticationSchemes = new[]
                {
                    new
                    {
                        type = "oauthbearertoken",
                        name = "OAuth Bearer Token",
                        description = "Authentication scheme using the OAuth Bearer Token standard",
                        specUri = "https://www.rfc-editor.org/info/rfc6750",
                        primary = true
                    }
                },
                meta = new
                {
                    resourceType = "ServiceProviderConfig",
                    location = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/scim/v2/ServiceProviderConfig"
                }
            };

            return Ok(config);
        }
    }
}
