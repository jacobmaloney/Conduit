using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Conduit.Emulator.GoogleWorkspace.Auth;
using Conduit.Emulator.GoogleWorkspace.Infrastructure;
using Conduit.Emulator.GoogleWorkspace.Models;
using Conduit.Emulator.GoogleWorkspace.Repositories;

namespace Conduit.Emulator.GoogleWorkspace.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = GoogleBearerDefaults.Scheme)]
[Route("admin/directory/v1/groups")]
public sealed class GroupsController : ControllerBase
{
    private const int MaxResults = 200;  // Admin SDK cap for groups
    private const int DefaultResults = 100;

    private readonly GwGroupRepository _groups;
    private readonly GwCustomerRepository _customers;
    private readonly GoogleWorkspaceOptions _options;

    public GroupsController(GwGroupRepository groups, GwCustomerRepository customers, IOptions<GoogleWorkspaceOptions> options)
    {
        _groups = groups;
        _customers = customers;
        _options = options.Value;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? customer = null,
        [FromQuery] string? domain = null,
        [FromQuery] string? userKey = null,
        [FromQuery] string? query = null,
        [FromQuery(Name = "maxResults")] int maxResults = DefaultResults,
        [FromQuery(Name = "pageToken")] string? pageToken = null)
    {
        if (string.IsNullOrEmpty(customer) && string.IsNullOrEmpty(domain) && string.IsNullOrEmpty(userKey))
            return GoogleErrorResult.BadRequest("required", "One of 'customer', 'domain', or 'userKey' must be specified.");

        var customerId = string.IsNullOrEmpty(customer) ? _options.CustomerId
            : (customer == "my_customer" ? (await _customers.GetDefaultAsync())?.Id ?? _options.CustomerId : customer);

        maxResults = Math.Clamp(maxResults, 1, MaxResults);
        var cursor = PageTokenEncoder.Decode(pageToken);
        var offset = cursor?.Offset ?? 0;

        var (groups, _) = await _groups.ListAsync(customerId, domain, userKey, query, offset, maxResults + 1);

        string? nextToken = null;
        List<GwGroup> page;
        if (groups.Count > maxResults)
        {
            page = groups.Take(maxResults).ToList();
            nextToken = PageTokenEncoder.Encode(new PageCursor { Offset = offset + maxResults });
        }
        else
        {
            page = groups.ToList();
        }

        return Ok(new GwGroupsList
        {
            Etag = EtagGenerator.New(),
            Groups = page,
            NextPageToken = nextToken
        });
    }

    [HttpGet("{groupKey}")]
    public async Task<IActionResult> Get(string groupKey)
    {
        var group = await _groups.ResolveAsync(groupKey);
        if (group is null) return GoogleErrorResult.NotFound("groupKey", $"Resource Not Found: {groupKey}");
        return Ok(group);
    }

    [HttpPost]
    public async Task<IActionResult> Insert([FromBody] JObject body)
    {
        var group = body?.ToObject<GwGroup>();
        if (group is null || string.IsNullOrEmpty(group.Email))
            return GoogleErrorResult.BadRequest("required", "email is required.");

        var existing = await _groups.ResolveAsync(group.Email);
        if (existing is not null)
            return GoogleErrorResult.Duplicate($"Entity already exists: {group.Email}");

        group.Id = IdGenerator.NewGroupId();
        if (string.IsNullOrEmpty(group.Name)) group.Name = group.Email.Split('@')[0];
        var customerId = (await _customers.GetDefaultAsync())?.Id ?? _options.CustomerId;

        var created = await _groups.InsertWithCustomerAsync(group, customerId);
        return Ok(created);
    }

    [HttpPut("{groupKey}")]
    public Task<IActionResult> Update(string groupKey, [FromBody] JObject body)
        => UpdateInternal(groupKey, body, isPatch: false);

    [HttpPatch("{groupKey}")]
    public Task<IActionResult> Patch(string groupKey, [FromBody] JObject body)
        => UpdateInternal(groupKey, body, isPatch: true);

    private async Task<IActionResult> UpdateInternal(string groupKey, JObject body, bool isPatch)
    {
        var existing = await _groups.ResolveAsync(groupKey);
        if (existing is null) return GoogleErrorResult.NotFound("groupKey", $"Resource Not Found: {groupKey}");

        GwGroup merged;
        if (isPatch)
        {
            var asJObject = JObject.FromObject(existing);
            asJObject.Merge(body, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            merged = asJObject.ToObject<GwGroup>()!;
        }
        else
        {
            merged = body.ToObject<GwGroup>()!;
            merged.Id = existing.Id;
        }

        var ifMatch = Request.Headers["If-Match"].FirstOrDefault();
        try
        {
            var updated = await _groups.UpdateAsync(existing.Id, merged, ifMatch);
            return updated is null
                ? GoogleErrorResult.NotFound("groupKey", $"Resource Not Found: {groupKey}")
                : Ok(updated);
        }
        catch (EtagMismatchException)
        {
            return GoogleErrorResult.PreconditionFailed("ETag mismatch.");
        }
    }

    [HttpDelete("{groupKey}")]
    public async Task<IActionResult> Delete(string groupKey)
    {
        var group = await _groups.ResolveAsync(groupKey);
        if (group is null) return GoogleErrorResult.NotFound("groupKey", $"Resource Not Found: {groupKey}");
        await _groups.DeleteAsync(group.Id);
        return new StatusCodeResult(204);
    }
}
