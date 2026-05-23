using System.Collections.Generic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Conduit.Web.Controllers
{
    /// <summary>
    /// SCIM Resource Types discovery endpoint
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Produces("application/scim+json")]
    [Route("scim/v2/ResourceTypes")]
    [Route("scim/v2/t/{slug}/ResourceTypes")]
    [EnableRateLimiting("anon")]
    public class ResourceTypesController : ControllerBase
    {
        [HttpGet]
        public IActionResult GetResourceTypes()
        {
            var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
            var resourceTypes = new List<object>
            {
                BuildUserResourceType(baseUrl),
                BuildGroupResourceType(baseUrl)
            };

            var response = new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
                totalResults = resourceTypes.Count,
                itemsPerPage = resourceTypes.Count,
                startIndex = 1,
                Resources = resourceTypes
            };

            return Ok(response);
        }

        [HttpGet("{name}")]
        public IActionResult GetResourceType(string name)
        {
            var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

            return name switch
            {
                "User" => Ok(BuildUserResourceType(baseUrl)),
                "Group" => Ok(BuildGroupResourceType(baseUrl)),
                _ => NotFound(new { schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:Error" }, status = 404, detail = $"ResourceType '{name}' not found" })
            };
        }

        private static object BuildUserResourceType(string baseUrl)
        {
            return new
            {
                schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ResourceType" },
                id = "User",
                name = "User",
                endpoint = "/scim/v2/Users",
                description = "User Account",
                schema = "urn:ietf:params:scim:schemas:core:2.0:User",
                schemaExtensions = new[]
                {
                    new
                    {
                        schema = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User",
                        required = false
                    }
                },
                meta = new
                {
                    resourceType = "ResourceType",
                    location = $"{baseUrl}/scim/v2/ResourceTypes/User"
                }
            };
        }

        private static object BuildGroupResourceType(string baseUrl)
        {
            return new
            {
                schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ResourceType" },
                id = "Group",
                name = "Group",
                endpoint = "/scim/v2/Groups",
                description = "Group",
                schema = "urn:ietf:params:scim:schemas:core:2.0:Group",
                schemaExtensions = Array.Empty<object>(),
                meta = new
                {
                    resourceType = "ResourceType",
                    location = $"{baseUrl}/scim/v2/ResourceTypes/Group"
                }
            };
        }
    }
}
