using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Conduit.Emulator.GoogleWorkspace.Auth;
using Conduit.Emulator.GoogleWorkspace.Infrastructure;
using Conduit.Emulator.GoogleWorkspace.Models;
using Conduit.Emulator.GoogleWorkspace.Repositories;

namespace Conduit.Emulator.GoogleWorkspace.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = GoogleBearerDefaults.Scheme)]
[Route("admin/directory/v1/groups/{groupKey}/members")]
public sealed class MembersController : ControllerBase
{
    private const int MaxResults = 200;
    private const int DefaultResults = 200;

    private readonly GwMemberRepository _members;
    private readonly GwGroupRepository _groups;
    private readonly GwUserRepository _users;

    public MembersController(GwMemberRepository members, GwGroupRepository groups, GwUserRepository users)
    {
        _members = members;
        _groups = groups;
        _users = users;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        string groupKey,
        [FromQuery] string? roles = null,
        [FromQuery(Name = "maxResults")] int maxResults = DefaultResults,
        [FromQuery(Name = "pageToken")] string? pageToken = null,
        [FromQuery(Name = "includeDerivedMembership")] bool includeDerivedMembership = false)
    {
        var group = await _groups.ResolveAsync(groupKey);
        if (group is null) return GoogleErrorResult.NotFound("groupKey", $"Resource Not Found: {groupKey}");

        maxResults = Math.Clamp(maxResults, 1, MaxResults);
        var cursor = PageTokenEncoder.Decode(pageToken);
        var offset = cursor?.Offset ?? 0;

        var (members, _) = await _members.ListAsync(group.Id, roles, includeDerivedMembership, offset, maxResults + 1);

        string? nextToken = null;
        List<GwMember> page;
        if (members.Count > maxResults)
        {
            page = members.Take(maxResults).ToList();
            nextToken = PageTokenEncoder.Encode(new PageCursor { Offset = offset + maxResults });
        }
        else
        {
            page = members.ToList();
        }

        return Ok(new GwMembersList
        {
            Etag = EtagGenerator.New(),
            Members = page,
            NextPageToken = nextToken
        });
    }

    [HttpGet("{memberKey}")]
    public async Task<IActionResult> Get(string groupKey, string memberKey)
    {
        var group = await _groups.ResolveAsync(groupKey);
        if (group is null) return GoogleErrorResult.NotFound("groupKey", $"Resource Not Found: {groupKey}");
        var member = await _members.GetAsync(group.Id, memberKey);
        return member is null
            ? GoogleErrorResult.NotFound("memberKey", $"Resource Not Found: {memberKey}")
            : Ok(member);
    }

    [HttpGet("{memberKey}/hasMember")]
    public async Task<IActionResult> HasMember(string groupKey, string memberKey)
    {
        var group = await _groups.ResolveAsync(groupKey);
        if (group is null) return GoogleErrorResult.NotFound("groupKey", $"Resource Not Found: {groupKey}");
        var member = await _members.GetAsync(group.Id, memberKey);
        return Ok(new { isMember = member is not null });
    }

    [HttpPost]
    public async Task<IActionResult> Insert(string groupKey, [FromBody] JObject body)
    {
        var group = await _groups.ResolveAsync(groupKey);
        if (group is null) return GoogleErrorResult.NotFound("groupKey", $"Resource Not Found: {groupKey}");

        var member = body?.ToObject<GwMember>();
        if (member is null || string.IsNullOrEmpty(member.Email))
            return GoogleErrorResult.BadRequest("required", "email is required.");

        if (string.IsNullOrEmpty(member.Id))
        {
            // Resolve member id by email — try users first, then groups.
            var user = await _users.ResolveAsync(member.Email);
            if (user is not null) { member.Id = user.Id; member.Type = "USER"; }
            else
            {
                var referencedGroup = await _groups.ResolveAsync(member.Email);
                if (referencedGroup is not null) { member.Id = referencedGroup.Id; member.Type = "GROUP"; }
                else { member.Id = IdGenerator.NewUserId(); member.Type = "EXTERNAL"; }
            }
        }

        if (string.IsNullOrEmpty(member.Role)) member.Role = "MEMBER";
        if (string.IsNullOrEmpty(member.Type)) member.Type = "USER";
        if (string.IsNullOrEmpty(member.Status)) member.Status = "ACTIVE";

        if (await _members.ExistsAsync(group.Id, member.Id))
            return GoogleErrorResult.Duplicate($"Member already exists: {member.Email}");

        await _members.InsertAsync(group.Id, member);
        await _groups.RefreshMemberCountAsync(group.Id);
        return Ok(member);
    }

    [HttpPut("{memberKey}")]
    [HttpPatch("{memberKey}")]
    public async Task<IActionResult> Update(string groupKey, string memberKey, [FromBody] JObject body)
    {
        var group = await _groups.ResolveAsync(groupKey);
        if (group is null) return GoogleErrorResult.NotFound("groupKey", $"Resource Not Found: {groupKey}");
        var existing = await _members.GetAsync(group.Id, memberKey);
        if (existing is null) return GoogleErrorResult.NotFound("memberKey", $"Resource Not Found: {memberKey}");

        var role = body?["role"]?.Value<string>() ?? existing.Role;
        var delivery = body?["delivery_settings"]?.Value<string>();

        await _members.UpdateAsync(group.Id, existing.Id, role, delivery);
        var updated = await _members.GetAsync(group.Id, existing.Id);
        return Ok(updated!);
    }

    [HttpDelete("{memberKey}")]
    public async Task<IActionResult> Delete(string groupKey, string memberKey)
    {
        var group = await _groups.ResolveAsync(groupKey);
        if (group is null) return GoogleErrorResult.NotFound("groupKey", $"Resource Not Found: {groupKey}");
        var existing = await _members.GetAsync(group.Id, memberKey);
        if (existing is null) return GoogleErrorResult.NotFound("memberKey", $"Resource Not Found: {memberKey}");

        await _members.DeleteAsync(group.Id, existing.Id);
        await _groups.RefreshMemberCountAsync(group.Id);
        return new StatusCodeResult(204);
    }
}
