using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Conduit.Core.Models;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Web.Services;

namespace Conduit.Web.Controllers
{
    /// <summary>
    /// Generic REST emulator at /api/v1/users. Deliberately *not* SCIM — gives ARS
    /// PowerShell workflows a simpler payload shape to demo "ARS can call any REST."
    /// When the active connection is a writable external target (its adapter
    /// SupportsCreate) the create is PROXIED to that target's sink; otherwise it
    /// persists into the same Users table SCIM uses. Tenant scope is resolved from
    /// the bearer token by ApiTokenAuthMiddleware → TenantContext.
    /// </summary>
    [ApiController]
    [Route("api/v1/users")]
    [Authorize]
    [EnableRateLimiting("scim")]
    public class ApiV1UsersController : ControllerBase
    {
        private readonly UserRepository _users;
        private readonly InboundProxyService _proxy;

        public ApiV1UsersController(UserRepository users, InboundProxyService proxy)
        {
            _users = users;
            _proxy = proxy;
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
        public async Task<IActionResult> Create([FromBody] ApiV1User body, CancellationToken ct)
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
            if (!string.IsNullOrWhiteSpace(body.Id)) scim.ExternalId = body.Id;

            // Inbound proxy: forward to a writable external target's sink, else local.
            var connectorObject = InboundScimMapper.FromScimUser(scim);
            var proxy = await _proxy.TryProxyCreateAsync(connectorObject, "User", ct);
            if (proxy.Decision == InboundProxyService.ProxyDecision.Proxied)
            {
                switch (proxy.Outcome)
                {
                    case ProvisionOutcome.Success:
                    case ProvisionOutcome.Accepted:
                        body.Id = string.IsNullOrEmpty(proxy.ExternalId) ? body.Id : proxy.ExternalId;
                        return StatusCode(201, body);
                    case ProvisionOutcome.NotSupported:
                        return StatusCode(501, new { error = proxy.ErrorMessage ?? $"Connector '{proxy.SystemType}' does not support inbound create." });
                    default:
                        return StatusCode(502, new { error = proxy.ErrorMessage ?? $"Target connector '{proxy.SystemType}' rejected the create." });
                }
            }

            var created = await _users.CreateAsync(scim);
            return StatusCode(201, Project(created));
        }

        [HttpPatch("{id}")]
        public async Task<IActionResult> Patch(Guid id, [FromBody] ApiV1UserPatch body, CancellationToken ct)
        {
            // Inbound proxy: forward a partial update to a writable external target
            // that supports update; else fall through to the local store. The path
            // {id} is the resource id the caller addressed. Only the fields the patch
            // set are mapped, so omitted fields are untouched on the target.
            var patchScim = new ScimUser { Id = id.ToString() };
            if (body.Active.HasValue) patchScim.Active = body.Active.Value;
            if (!string.IsNullOrWhiteSpace(body.Username)) patchScim.UserName = body.Username!;
            if (body.FirstName != null || body.LastName != null)
                patchScim.Name = new ScimName { GivenName = body.FirstName, FamilyName = body.LastName };
            if (!string.IsNullOrWhiteSpace(body.Email))
                patchScim.Emails = new List<ScimEmail> { new() { Value = body.Email, Type = "work", Primary = true } };

            var connectorObject = InboundScimMapper.FromScimUser(patchScim);
            var proxy = await _proxy.TryProxyUpdateAsync(id.ToString(), connectorObject, replace: false, "User", ct);
            if (proxy.Decision == InboundProxyService.ProxyDecision.Proxied)
            {
                switch (proxy.Outcome)
                {
                    case ProvisionOutcome.Success:
                    case ProvisionOutcome.Accepted:
                        return Ok(new { id = string.IsNullOrEmpty(proxy.ExternalId) ? id.ToString() : proxy.ExternalId });
                    case ProvisionOutcome.NotSupported:
                        return StatusCode(501, new { error = proxy.ErrorMessage ?? $"Connector '{proxy.SystemType}' does not support inbound update." });
                    default:
                        return StatusCode(502, new { error = proxy.ErrorMessage ?? $"Target connector '{proxy.SystemType}' rejected the update." });
                }
            }

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
