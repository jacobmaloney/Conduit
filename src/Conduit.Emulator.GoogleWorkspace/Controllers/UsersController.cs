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
[Route("admin/directory/v1/users")]
public sealed class UsersController : ControllerBase
{
    private const int MaxResults = 500;  // Admin SDK documented cap for users
    private const int DefaultResults = 100;

    private readonly GwUserRepository _users;
    private readonly GwCustomerRepository _customers;
    private readonly GoogleWorkspaceOptions _options;

    public UsersController(GwUserRepository users, GwCustomerRepository customers, IOptions<GoogleWorkspaceOptions> options)
    {
        _users = users;
        _customers = customers;
        _options = options.Value;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? customer = null,
        [FromQuery] string? domain = null,
        [FromQuery] string? query = null,
        [FromQuery(Name = "orderBy")] string? orderBy = null,
        [FromQuery(Name = "sortOrder")] string? sortOrder = null,
        [FromQuery(Name = "orgUnitPath")] string? orgUnitPath = null,
        [FromQuery(Name = "maxResults")] int maxResults = DefaultResults,
        [FromQuery(Name = "pageToken")] string? pageToken = null,
        [FromQuery(Name = "showDeleted")] string? showDeleted = null,
        [FromQuery(Name = "projection")] string? projection = null)
    {
        if (string.IsNullOrEmpty(customer) && string.IsNullOrEmpty(domain))
            return GoogleErrorResult.BadRequest("required", "Either 'customer' or 'domain' must be specified.");

        var customerId = await ResolveCustomerIdAsync(customer);
        if (customerId is null) return GoogleErrorResult.BadRequest("required", "Unknown customer.");

        maxResults = Math.Clamp(maxResults, 1, MaxResults);
        var cursor = PageTokenEncoder.Decode(pageToken);
        var offset = cursor?.Offset ?? 0;

        var (users, _) = await _users.ListAsync(
            customerId,
            domain,
            query,
            orgUnitPath,
            isAdmin: null,
            isSuspended: null,
            showDeleted: string.Equals(showDeleted, "true", StringComparison.OrdinalIgnoreCase),
            orderBy: orderBy ?? "email",
            sortOrder: sortOrder ?? "ASCENDING",
            offset: offset,
            limit: maxResults + 1);

        string? nextToken = null;
        List<GwUser> page;
        if (users.Count > maxResults)
        {
            page = users.Take(maxResults).ToList();
            nextToken = PageTokenEncoder.Encode(new PageCursor { Offset = offset + maxResults });
        }
        else
        {
            page = users.ToList();
        }

        foreach (var u in page) ScrubWritingOnlyFields(u);

        return Ok(new GwUsersList
        {
            Etag = EtagGenerator.New(),
            Users = page,
            NextPageToken = nextToken
        });
    }

    [HttpGet("{userKey}")]
    public async Task<IActionResult> Get(string userKey, [FromQuery(Name = "projection")] string? projection = null)
    {
        var user = await _users.ResolveAsync(userKey);
        if (user is null) return GoogleErrorResult.NotFound("userKey", $"Resource Not Found: {userKey}");
        ScrubWritingOnlyFields(user);
        return Ok(user);
    }

    [HttpPost]
    public async Task<IActionResult> Insert([FromBody] JObject body)
    {
        if (body is null) return GoogleErrorResult.BadRequest("required", "Request body is required.");

        var user = body.ToObject<GwUser>();
        if (user is null) return GoogleErrorResult.BadRequest("invalid", "Invalid user body.");
        if (string.IsNullOrEmpty(user.PrimaryEmail))
            return GoogleErrorResult.BadRequest("required", "primaryEmail is required.");
        if (user.Name is null || string.IsNullOrEmpty(user.Name.GivenName) || string.IsNullOrEmpty(user.Name.FamilyName))
            return GoogleErrorResult.BadRequest("required", "name.givenName and name.familyName are required.");

        var existing = await _users.ResolveAsync(user.PrimaryEmail);
        if (existing is not null) return GoogleErrorResult.Duplicate($"Entity already exists: {user.PrimaryEmail}");

        user.Id = IdGenerator.NewUserId();
        user.CustomerId = (await _customers.GetDefaultAsync())?.Id ?? _options.CustomerId;
        if (string.IsNullOrEmpty(user.OrgUnitPath)) user.OrgUnitPath = "/";
        if (string.IsNullOrEmpty(user.Name.FullName))
            user.Name.FullName = $"{user.Name.GivenName} {user.Name.FamilyName}".Trim();

        var created = await _users.InsertAsync(user);
        ScrubWritingOnlyFields(created);
        return Ok(created);
    }

    [HttpPut("{userKey}")]
    public Task<IActionResult> Update(string userKey, [FromBody] JObject body)
        => UpdateInternal(userKey, body, isPatch: false);

    [HttpPatch("{userKey}")]
    public Task<IActionResult> Patch(string userKey, [FromBody] JObject body)
        => UpdateInternal(userKey, body, isPatch: true);

    private async Task<IActionResult> UpdateInternal(string userKey, JObject body, bool isPatch)
    {
        var existing = await _users.ResolveAsync(userKey);
        if (existing is null) return GoogleErrorResult.NotFound("userKey", $"Resource Not Found: {userKey}");

        GwUser merged;
        if (isPatch)
        {
            var asJObject = JObject.FromObject(existing);
            asJObject.Merge(body, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            merged = asJObject.ToObject<GwUser>()!;
        }
        else
        {
            merged = body.ToObject<GwUser>()!;
            merged.Id = existing.Id;
            merged.CustomerId = existing.CustomerId;
            merged.CreationTime = existing.CreationTime;
        }

        var ifMatch = Request.Headers["If-Match"].FirstOrDefault();
        try
        {
            var updated = await _users.UpdateAsync(existing.Id, merged, ifMatch);
            if (updated is null) return GoogleErrorResult.NotFound("userKey", "Resource Not Found.");
            ScrubWritingOnlyFields(updated);
            return Ok(updated);
        }
        catch (EtagMismatchException)
        {
            return GoogleErrorResult.PreconditionFailed("ETag mismatch.");
        }
    }

    [HttpDelete("{userKey}")]
    public async Task<IActionResult> Delete(string userKey)
    {
        var user = await _users.ResolveAsync(userKey);
        if (user is null) return GoogleErrorResult.NotFound("userKey", $"Resource Not Found: {userKey}");
        await _users.SoftDeleteAsync(user.Id);
        return new StatusCodeResult(204);
    }

    [HttpPost("{userKey}/undelete")]
    public async Task<IActionResult> Undelete(string userKey, [FromBody] UndeleteBody? body)
    {
        var user = await _users.ResolveAsync(userKey, includeDeleted: true);
        if (user is null || user.DeletionTime is null) return GoogleErrorResult.NotFound("userKey", $"Resource Not Found: {userKey}");
        await _users.UndeleteAsync(user.Id);
        return new StatusCodeResult(204);
    }

    [HttpPost("{userKey}/makeAdmin")]
    public async Task<IActionResult> MakeAdmin(string userKey, [FromBody] MakeAdminBody body)
    {
        var user = await _users.ResolveAsync(userKey);
        if (user is null) return GoogleErrorResult.NotFound("userKey", $"Resource Not Found: {userKey}");
        await _users.MakeAdminAsync(user.Id, body.Status);
        return new StatusCodeResult(200);
    }

    private async Task<string?> ResolveCustomerIdAsync(string? customer)
    {
        if (string.IsNullOrEmpty(customer)) return _options.CustomerId;
        if (customer == "my_customer") return (await _customers.GetDefaultAsync())?.Id ?? _options.CustomerId;
        var exact = await _customers.GetAsync(customer);
        return exact?.Id;
    }

    private static void ScrubWritingOnlyFields(GwUser u)
    {
        u.Password = null;
        u.HashFunction = null;
    }

    public sealed class UndeleteBody
    {
        public string? OrgUnitPath { get; set; }
    }

    public sealed class MakeAdminBody
    {
        public bool Status { get; set; }
    }
}
