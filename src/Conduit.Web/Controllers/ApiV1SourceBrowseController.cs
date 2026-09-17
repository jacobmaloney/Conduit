using Conduit.SourceBrowsing;
using Conduit.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Conduit.Web.Controllers;

[ApiController, Route("api/v1/source-browser"), Authorize, EnableRateLimiting("scim")]
public sealed class ApiV1SourceBrowseController(SourceBrowseService service, SourceCatalogService catalog) : ControllerBase
{
    [HttpGet("catalog")]
    public Task<IActionResult> Catalog(CancellationToken ct) => Execute(() => catalog.LocalAsync(User, ct));

    [HttpGet("context")]
    public Task<IActionResult> Describe([FromQuery] Guid connectionId, [FromQuery] Guid? projectId, [FromQuery] Guid? stepId, [FromQuery] Guid? expectedInstanceId, CancellationToken ct) =>
        Execute(() => service.DescribeAsync(User, new(connectionId, projectId, stepId), ct, expectedInstanceId));

    [HttpPost("sample"), RequestSizeLimit(524288)]
    public Task<IActionResult> Sample([FromBody] SourceBrowseRequest request, CancellationToken ct) =>
        Execute(() => service.ReadAsync(User, request, ct));

    [HttpPost("mappings"), RequestSizeLimit(524288)]
    public Task<IActionResult> SaveMappings([FromBody] SourceMappingSaveRequest request, CancellationToken ct) =>
        Execute(() => service.SaveMappingsAsync(User, request, ct));

    private async Task<IActionResult> Execute<T>(Func<Task<T>> read)
    {
        // Do not expose this data API to a browser cookie (CSRF). Portal UI calls the same service directly.
        if (User.Identity?.AuthenticationType != "ApiToken") return Forbid();
        try { return Ok(await read()); }
        catch (SourceBrowseException ex) { return StatusCode(ex.Code is "SourceAccessDenied" or "AdministratorRequired" or "MappingAdministratorRequired" ? 403 : 400, new SourceBrowseFailure(ex.Code, ex.Message)); }
    }
}
