using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Conduit.Core.Models;
using Conduit.DataAccess.Repositories;

namespace Conduit.Web.Controllers
{
    /// <summary>
    /// Generic REST emulator at /api/v1/groups. Same data store as SCIM Groups,
    /// simpler payload shape for ARS PowerShell workflows.
    /// </summary>
    [ApiController]
    [Route("api/v1/groups")]
    [Authorize]
    [EnableRateLimiting("scim")]
    public class ApiV1GroupsController : ControllerBase
    {
        private readonly GroupRepository _groups;

        public ApiV1GroupsController(GroupRepository groups)
        {
            _groups = groups;
        }

        public class ApiV1Group
        {
            public string? Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public string? Description { get; set; }
            public int MemberCount { get; set; }
        }

        public class ApiV1MemberAdd
        {
            public string UserId { get; set; } = string.Empty;
        }

        [HttpGet]
        public async Task<IActionResult> List([FromQuery] int startIndex = 1, [FromQuery] int count = 100)
        {
            var (groups, _) = await _groups.GetAllAsync(new ScimQueryOptions { StartIndex = startIndex, Count = count });
            return Ok(groups.Select(Project).ToList());
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> Get(Guid id)
        {
            var g = await _groups.GetByIdAsync(id);
            if (g == null) return NotFound(new { error = "Group not found" });
            return Ok(Project(g));
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] ApiV1Group body)
        {
            if (string.IsNullOrWhiteSpace(body.Name))
            {
                return BadRequest(new { error = "name is required" });
            }
            var scim = new ScimGroup
            {
                DisplayName = body.Name,
                Description = body.Description,
                Members = new List<ScimGroupMember>()
            };
            var created = await _groups.CreateAsync(scim);
            return StatusCode(201, Project(created));
        }

        [HttpPost("{id}/members")]
        public async Task<IActionResult> AddMember(Guid id, [FromBody] ApiV1MemberAdd body)
        {
            if (string.IsNullOrWhiteSpace(body.UserId) || !Guid.TryParse(body.UserId, out var userGuid))
            {
                return BadRequest(new { error = "userId is required and must be a GUID" });
            }
            var group = await _groups.GetByIdAsync(id);
            if (group == null) return NotFound(new { error = "Group not found" });
            group.Members ??= new List<ScimGroupMember>();
            if (group.Members.All(m => !string.Equals(m.Value, userGuid.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                group.Members.Add(new ScimGroupMember { Value = userGuid.ToString(), Type = "User" });
                await _groups.UpdateAsync(id, group);
            }
            return Ok(new { groupId = id, userId = userGuid });
        }

        [HttpDelete("{id}/members/{userId}")]
        public async Task<IActionResult> RemoveMember(Guid id, Guid userId)
        {
            var group = await _groups.GetByIdAsync(id);
            if (group == null) return NotFound(new { error = "Group not found" });
            var removed = group.Members?.RemoveAll(m =>
                string.Equals(m.Value, userId.ToString(), StringComparison.OrdinalIgnoreCase)) ?? 0;
            if (removed == 0)
            {
                return NotFound(new { error = "Member not found in group" });
            }
            await _groups.UpdateAsync(id, group);
            return NoContent();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(Guid id)
        {
            var ok = await _groups.DeleteAsync(id);
            if (!ok) return NotFound(new { error = "Group not found" });
            return NoContent();
        }

        private static ApiV1Group Project(ScimGroup g) => new()
        {
            Id = g.Id,
            Name = g.DisplayName,
            Description = g.Description,
            MemberCount = g.Members?.Count ?? 0
        };
    }
}
