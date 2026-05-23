using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Conduit.Core.Models;
using Conduit.Core.Services;
using Conduit.Web.Services;

namespace Conduit.Web.Controllers
{
    /// <summary>
    /// Inbound proxy for Active Roles Server provisioning. ARS PowerShell
    /// workflows post here when a virtual attribute change should create /
    /// modify / disable a real AD user via the ARS Administration Service.
    ///
    /// Phase 4 ships as a *functional stub*: every endpoint validates inputs,
    /// reads ArsProxy.* configuration from SystemConfiguration, logs the
    /// request, and returns the same HTTP status code shape the real
    /// implementation will. When ArsProxy.Server / Username / Password are
    /// not configured (the lab-not-ready case), the response body explicitly
    /// flags that ARS was not called. When all four keys are populated, the
    /// stub still logs and returns 'queued' — actual PowerShell-via-Management
    /// Shell execution is the next iteration once the live ARS environment
    /// is stable.
    ///
    /// Locked down to <see cref="ApiTokenScope.ArsProxy"/> tokens only.
    /// </summary>
    [ApiController]
    [Route("ars/v1")]
    [EnableRateLimiting("scim")]
    [Authorize]
    public class ArsV1Controller : ControllerBase
    {
        private const string KeyServer   = "ArsProxy.Server";
        private const string KeyPort     = "ArsProxy.Port";
        private const string KeyUsername = "ArsProxy.Username";
        private const string KeyPassword = "ArsProxy.Password";

        private readonly SystemConfigurationService _config;
        private readonly ITenantContext _tenant;
        private readonly ILogger<ArsV1Controller> _logger;

        public ArsV1Controller(SystemConfigurationService config, ITenantContext tenant, ILogger<ArsV1Controller> logger)
        {
            _config = config;
            _tenant = tenant;
            _logger = logger;
        }

        public class ArsUserCreate
        {
            public string Username { get; set; } = string.Empty;
            public string? Email { get; set; }
            public string? FirstName { get; set; }
            public string? LastName { get; set; }
            public string? Container { get; set; }   // e.g. "OU=Demo,DC=Domain,DC=Local"
            public bool Active { get; set; } = true;
        }

        public class ArsUserPatch
        {
            public bool? Active { get; set; }
            public string? Email { get; set; }
            public string? Title { get; set; }
        }

        public class ArsMemberAdd
        {
            public string UserId { get; set; } = string.Empty;
        }

        // ─── Endpoints ─────────────────────────────────────────────────────

        [HttpPost("users")]
        public Task<IActionResult> CreateUser([FromBody] ArsUserCreate body) =>
            Forward("POST", "/ars/v1/users", body, () =>
            {
                if (string.IsNullOrWhiteSpace(body.Username))
                {
                    return Task.FromResult<IActionResult>(BadRequest(new { error = "username is required" }));
                }
                return Task.FromResult<IActionResult>(null!);
            });

        [HttpPatch("users/{id}")]
        public Task<IActionResult> PatchUser(string id, [FromBody] ArsUserPatch body) =>
            Forward("PATCH", $"/ars/v1/users/{id}", body, () => Task.FromResult<IActionResult>(null!));

        [HttpDelete("users/{id}")]
        public Task<IActionResult> DeleteUser(string id) =>
            Forward("DELETE", $"/ars/v1/users/{id}", new { id }, () => Task.FromResult<IActionResult>(null!));

        [HttpPost("groups/{id}/members")]
        public Task<IActionResult> AddMember(string id, [FromBody] ArsMemberAdd body) =>
            Forward("POST", $"/ars/v1/groups/{id}/members", body, () =>
            {
                if (string.IsNullOrWhiteSpace(body.UserId))
                {
                    return Task.FromResult<IActionResult>(BadRequest(new { error = "userId is required" }));
                }
                return Task.FromResult<IActionResult>(null!);
            });

        [HttpDelete("groups/{id}/members/{userId}")]
        public Task<IActionResult> RemoveMember(string id, string userId) =>
            Forward("DELETE", $"/ars/v1/groups/{id}/members/{userId}", new { id, userId },
                () => Task.FromResult<IActionResult>(null!));

        // ─── Internal ──────────────────────────────────────────────────────

        private async Task<IActionResult> Forward(string verb, string path, object body, Func<Task<IActionResult>> validate)
        {
            // Hard gate: only ArsProxy-scoped tokens may call /ars/v1.
            if (!_tenant.IsResolved || !_tenant.IsArsProxy)
            {
                return StatusCode(403, new { error = "This endpoint requires an API token with scope = ArsProxy." });
            }

            var earlyReject = await validate();
            // A null sentinel from validate() means "no early rejection" — the helpers return
            // `(IActionResult)null!` for the success path. Keep that contract.
            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            if (earlyReject is not null) return earlyReject;

            var (server, port, username, hasPassword) = await ReadConfigAsync();
            var configured = !string.IsNullOrEmpty(server) && !string.IsNullOrEmpty(username) && hasPassword;

            if (!configured)
            {
                _logger.LogInformation("ARS proxy {Verb} {Path} — not configured; logging only. body={@Body}", verb, path, body);
                return StatusCode(201, new
                {
                    status = "queued",
                    note = "ARS not configured — request logged only",
                    arsServer = server,
                    port,
                    username,
                    received = body
                });
            }

            // Real PowerShell-via-Management-Shell execution is the next iteration.
            // Today we log loudly so the demo can show the call arriving end-to-end.
            _logger.LogInformation("ARS proxy {Verb} {Path} — configured (server={Server}:{Port}, user={Username}); execution stub.",
                verb, path, server, port, username);
            return StatusCode(201, new
            {
                status = "queued",
                note = "ARS configured — execution stub; PowerShell handoff will be wired in the next iteration.",
                arsServer = $"{server}:{port}",
                received = body
            });
        }

        private async Task<(string? server, int port, string? username, bool hasPassword)> ReadConfigAsync()
        {
            var server = await _config.GetAsync(KeyServer);
            var portRaw = await _config.GetAsync(KeyPort);
            var username = await _config.GetAsync(KeyUsername);
            var password = await _config.GetAsync(KeyPassword);
            if (!int.TryParse(portRaw, out var port) || port <= 0) port = 15172;
            return (server, port, username, !string.IsNullOrEmpty(password));
        }
    }
}
