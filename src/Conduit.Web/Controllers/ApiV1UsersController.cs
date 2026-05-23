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
    /// Generic REST emulator at /api/v1/users. Deliberately *not* SCIM — gives ARS
    /// PowerShell workflows a simpler payload shape to demo "ARS can call any REST."
    /// Persists into the same Users table SCIM uses; tenant scope is resolved from
    /// the bearer token by ApiTokenAuthMiddleware → TenantContext → UserRepository.
    /// </summary>
    [ApiController]
    [Route("api/v1/users")]
    [Authorize]
    [EnableRateLimiting("scim")]
    public class ApiV1UsersController : ControllerBase
    {
        private readonly UserRepository _users;

        public ApiV1UsersController(UserRepository users)
        {
            _users = users;
        }

        public class ApiV1User
        {
            public string? Id { get; set; }
            public string Username { get; set; } = string.Empty;
            public string? Email { get; set; }
            public string? FirstName { get; set; }
            public string? LastName { get; set; }
            public bool Active { get; set; } = true;
        }

        public class ApiV1UserPatch
        {
            public string? Username { get; set; }
            public string? Email { get; set; }
            public string? FirstName { get; set; }
            public string? LastName { get; set; }
            public bool? Active { get; set; }
        }

        [HttpGet]
        public async Task<IActionResult> List([FromQuery] int startIndex = 1, [FromQuery] int count = 100)
        {
            var (users, _) = await _users.GetAllAsync(new ScimQueryOptions { StartIndex = startIndex, Count = count });
            return Ok(users.Select(Project).ToList());
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> Get(Guid id)
        {
            var user = await _users.GetByIdAsync(id);
            if (user == null) return NotFound(new { error = "User not found" });
            return Ok(Project(user));
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] ApiV1User body)
        {
            if (string.IsNullOrWhiteSpace(body.Username))
            {
                return BadRequest(new { error = "username is required" });
            }
            var scim = new ScimUser
            {
                UserName = body.Username,
                Active = body.Active,
                Name = new ScimName { GivenName = body.FirstName, FamilyName = body.LastName },
                DisplayName = string.IsNullOrWhiteSpace(body.FirstName) && string.IsNullOrWhiteSpace(body.LastName)
                    ? body.Username
                    : $"{body.FirstName} {body.LastName}".Trim()
            };
            if (!string.IsNullOrWhiteSpace(body.Email))
            {
                scim.Emails = new List<ScimEmail>
                {
                    new() { Value = body.Email, Type = "work", Primary = true }
                };
            }

            var created = await _users.CreateAsync(scim);
            return StatusCode(201, Project(created));
        }

        [HttpPatch("{id}")]
        public async Task<IActionResult> Patch(Guid id, [FromBody] ApiV1UserPatch body)
        {
            var existing = await _users.GetByIdAsync(id);
            if (existing == null) return NotFound(new { error = "User not found" });

            if (body.Active.HasValue) existing.Active = body.Active.Value;
            if (!string.IsNullOrWhiteSpace(body.Username)) existing.UserName = body.Username!;
            if (body.FirstName != null) existing.Name = new ScimName { GivenName = body.FirstName, FamilyName = existing.Name?.FamilyName };
            if (body.LastName != null) existing.Name = new ScimName { GivenName = existing.Name?.GivenName, FamilyName = body.LastName };
            if (!string.IsNullOrWhiteSpace(body.Email))
            {
                existing.Emails ??= new List<ScimEmail>();
                var primary = existing.Emails.FirstOrDefault(e => e.Primary);
                if (primary != null) primary.Value = body.Email!;
                else existing.Emails.Add(new ScimEmail { Value = body.Email, Type = "work", Primary = true });
            }

            var updated = await _users.UpdateAsync(id, existing);
            if (updated == null) return NotFound(new { error = "User not found" });
            return Ok(Project(updated));
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(Guid id)
        {
            var ok = await _users.DeleteAsync(id);
            if (!ok) return NotFound(new { error = "User not found" });
            return NoContent();
        }

        private static ApiV1User Project(ScimUser u)
        {
            var primaryEmail = u.Emails?.FirstOrDefault(e => e.Primary) ?? u.Emails?.FirstOrDefault();
            return new ApiV1User
            {
                Id = u.Id,
                Username = u.UserName ?? string.Empty,
                Email = primaryEmail?.Value,
                FirstName = u.Name?.GivenName,
                LastName = u.Name?.FamilyName,
                Active = u.Active
            };
        }
    }
}
