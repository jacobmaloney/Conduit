using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Conduit.Core.Models;

namespace Conduit.Web.Controllers
{
    /// <summary>
    /// Base controller for SCIM endpoints
    /// </summary>
    [ApiController]
    [Authorize]
    [Produces("application/scim+json")]
    public abstract class BaseScimController : ControllerBase
    {
        /// <summary>
        /// Creates a SCIM error response
        /// </summary>
        /// <param name="status">HTTP status code</param>
        /// <param name="scimType">SCIM error type</param>
        /// <param name="detail">Error detail message</param>
        /// <returns>Object result with SCIM error</returns>
        protected ObjectResult ScimError(int status, string? scimType, string detail)
        {
            var error = new ScimError
            {
                Status = status,
                ScimType = scimType,
                Detail = detail
            };

            return StatusCode(status, error);
        }

        /// <summary>
        /// Creates a 404 Not Found SCIM error response
        /// </summary>
        /// <param name="resourceType">The resource type</param>
        /// <param name="id">The resource ID</param>
        /// <returns>Not found result</returns>
        protected NotFoundObjectResult ScimNotFound(string resourceType, string id)
        {
            var error = new ScimError
            {
                Status = 404,
                ScimType = ScimErrorType.NotFound,
                Detail = $"{resourceType} with ID '{id}' not found"
            };

            return NotFound(error);
        }

        /// <summary>
        /// Creates a 400 Bad Request SCIM error response
        /// </summary>
        /// <param name="detail">Error detail message</param>
        /// <param name="scimType">SCIM error type</param>
        /// <returns>Bad request result</returns>
        protected BadRequestObjectResult ScimBadRequest(string detail, string? scimType = null)
        {
            var error = new ScimError
            {
                Status = 400,
                ScimType = scimType ?? ScimErrorType.InvalidSyntax,
                Detail = detail
            };

            return BadRequest(error);
        }

        /// <summary>
        /// Creates a 409 Conflict SCIM error response
        /// </summary>
        /// <param name="detail">Error detail message</param>
        /// <returns>Conflict result</returns>
        protected ConflictObjectResult ScimConflict(string detail)
        {
            var error = new ScimError
            {
                Status = 409,
                ScimType = ScimErrorType.Uniqueness,
                Detail = detail
            };

            return Conflict(error);
        }

        /// <summary>
        /// Gets the base URL for resource locations
        /// </summary>
        /// <returns>Base URL</returns>
        protected string GetBaseUrl()
        {
            var request = HttpContext.Request;
            return $"{request.Scheme}://{request.Host}{request.PathBase}";
        }

        /// <summary>
        /// Returns the SCIM v2 path prefix for this request. When the route includes
        /// a tenant slug (e.g. /scim/v2/t/acme/Users), the slug segment is preserved
        /// so that Meta.Location and Location headers round-trip correctly back to
        /// the same connection.
        /// </summary>
        protected string ScimPrefix()
        {
            if (RouteData.Values.TryGetValue("slug", out var slugObj) && slugObj is string slug && !string.IsNullOrEmpty(slug))
            {
                return $"/scim/v2/t/{slug}";
            }
            return "/scim/v2";
        }

        /// <summary>
        /// Sets the location header for a created resource
        /// </summary>
        /// <param name="resourceType">The resource type</param>
        /// <param name="id">The resource ID</param>
        protected void SetLocationHeader(string resourceType, string id)
        {
            var location = $"{GetBaseUrl()}{ScimPrefix()}/{resourceType}/{id}";
            Response.Headers.Append("Location", location);
        }
    }
}