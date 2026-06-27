using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Conduit.Core.Models;
using Conduit.Core.Services;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;

namespace Conduit.Web.Controllers
{
    /// <summary>
    /// Phase 5. Connector-routed provisioning surface. Callers (CenturyCity Worker,
    /// any other governance-layer consumer) hit these endpoints with a
    /// <c>connectionId</c> in the body and Conduit:
    ///   1. Looks up the Tenant by id.
    ///   2. Resolves the matching <see cref="IConnectorAdapter"/> via the registry.
    ///   3. Checks the adapter's capability flag — short-circuits to <c>NotSupported</c>
    ///      so the caller can mark its workflow step Failed cleanly (rather than
    ///      letting it look silently successful).
    ///   4. Instantiates a sink for the tenant and dispatches the call.
    ///
    /// Auth: bearer <c>scim_*</c> token (Admin or matching Tenant scope) — the same
    /// <see cref="Middleware.ApiTokenAuthMiddleware"/> the rest of the API uses.
    /// When the token is Tenant-scoped, the body's connectionId MUST equal the
    /// token's tenant; otherwise we 403 to prevent token-aimed cross-tenant writes.
    /// </summary>
    [ApiController]
    [Route("api/v1")]
    [Authorize]
    [EnableRateLimiting("scim")]
    public class ProvisioningController : ControllerBase
    {
        private readonly TenantRepository _tenants;
        private readonly ConnectorRegistry _registry;
        private readonly ITenantContext _tenantContext;
        private readonly ILogger<ProvisioningController> _logger;

        public ProvisioningController(
            TenantRepository tenants,
            ConnectorRegistry registry,
            ITenantContext tenantContext,
            ILogger<ProvisioningController> logger)
        {
            _tenants = tenants;
            _registry = registry;
            _tenantContext = tenantContext;
            _logger = logger;
        }

        // ─── Request / response shapes (match CenturyCity ConduitClient on the wire) ─

        /// <summary>
        /// Wire shape for POST /api/v1/provision. Mirrors
        /// <c>CenturyCity.WriteBack.ProvisionIdentityRequest</c> 1:1 — do not
        /// rename properties without updating the CenturyCity client.
        /// </summary>
        public sealed class ProvisionRequest
        {
            public Guid ConnectionId { get; set; }
            public string? ExternalId { get; set; }
            public string Username { get; set; } = string.Empty;
            public string? Email { get; set; }
            public string? FirstName { get; set; }
            public string? LastName { get; set; }
            public string? DisplayName { get; set; }
            public string? Department { get; set; }
            public string? JobTitle { get; set; }
            public string? ManagerExternalId { get; set; }
            /// <summary>AD: target OU DN. Entra: ignored (no OU concept). Okta: group id. CenturyCity sends this as <c>targetContainer</c>.</summary>
            public string? TargetContainer { get; set; }
            public bool Active { get; set; } = true;
            /// <summary>Arbitrary additional attributes the sink may project to its native schema.</summary>
            public Dictionary<string, string?>? Attributes { get; set; }
        }

        /// <summary>Wire shape for POST /api/v1/users/{externalId}/move. Mirrors <c>CenturyCity.WriteBack.ConduitMoveRequest</c>.</summary>
        public sealed class MoveRequest
        {
            public Guid ConnectionId { get; set; }
            /// <summary>New parent container. AD: parent OU DN.</summary>
            public string NewContainer { get; set; } = string.Empty;
        }

        /// <summary>
        /// Wire shape for POST /api/v1/users/{externalId}/reset-password. Mirrors
        /// <c>CenturyCity.WriteBack.ConduitResetPasswordRequest</c>.
        /// <para>
        /// Password is plaintext over TLS — Conduit never logs it (no Debug echo,
        /// no exception payload include). Treat the model instance as transient.
        /// </para>
        /// </summary>
        public sealed class ResetPasswordRequest
        {
            public Guid ConnectionId { get; set; }
            public string Password { get; set; } = string.Empty;
            public bool RequireChangeAtNextLogin { get; set; } = true;
        }

        /// <summary>
        /// Wire shape for the response. The four-valued <c>status</c> matches
        /// CenturyCity's <c>ConduitProvisioningOutcome</c> (Success / Accepted /
        /// Failed / NotSupported). The string form is what the client deserializes.
        /// </summary>
        public sealed class ProvisionResponse
        {
            public string Status { get; set; } = string.Empty;
            public string? ExternalId { get; set; }
            public string? TaskId { get; set; }
            public string? Id { get; set; }            // alias of ExternalId — CenturyCity reads either.
            public string? ErrorMessage { get; set; }
        }

        // ─── Endpoints ────────────────────────────────────────────────────────────

        /// <summary>POST /api/v1/provision — create a new user via the tenant's connector.</summary>
        [HttpPost("provision")]
        public async Task<IActionResult> Provision([FromBody] ProvisionRequest body, CancellationToken ct)
        {
            if (body is null) return BadRequest(new { error = "Request body is required." });
            if (body.ConnectionId == Guid.Empty)
                return BadRequest(new { error = "connectionId is required." });
            if (string.IsNullOrWhiteSpace(body.Username))
                return BadRequest(new { error = "username is required." });

            var auth = AuthorizeForTenant(body.ConnectionId);
            if (auth is not null) return auth;

            var (adapter, tenant, errResult) = await ResolveAdapterAsync(body.ConnectionId);
            if (errResult is not null) return errResult;

            if (!adapter!.Capabilities.SupportsCreate)
            {
                return Ok(new ProvisionResponse
                {
                    Status = nameof(ProvisionOutcome.NotSupported),
                    ErrorMessage = $"Connector '{adapter.SystemType}' does not implement provisioning (CreateAsync)."
                });
            }

            StampInboundTable(tenant!);
            var sink = adapter.CreateSink(body.ConnectionId);
            if (sink is null)
            {
                return Ok(new ProvisionResponse
                {
                    Status = nameof(ProvisionOutcome.NotSupported),
                    ErrorMessage = $"Connector '{adapter.SystemType}' does not expose a sink for tenant {body.ConnectionId}."
                });
            }

            var connectorObject = BuildConnectorObject(body);
            ProvisionResult result;
            try
            {
                result = await sink.CreateAsync(connectorObject, ct);
            }
            catch (NotSupportedException ex)
            {
                // Capability flag was true but the sink threw anyway — surface honestly.
                result = ProvisionResult.NotSupported(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProvisioningController.Provision threw for connection {ConnId} / user {User}",
                    body.ConnectionId, body.Username);
                result = ProvisionResult.Failed($"Conduit-side exception: {ex.Message}");
            }

            return Ok(ToResponse(result));
        }

        /// <summary>POST /api/v1/users/{externalId}/move — relocate an object to a new container.</summary>
        [HttpPost("users/{externalId}/move")]
        public async Task<IActionResult> Move(string externalId, [FromBody] MoveRequest body, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(externalId))
                return BadRequest(new { error = "externalId path segment is required." });
            if (body is null) return BadRequest(new { error = "Request body is required." });
            if (body.ConnectionId == Guid.Empty)
                return BadRequest(new { error = "connectionId is required." });
            if (string.IsNullOrWhiteSpace(body.NewContainer))
                return BadRequest(new { error = "newContainer is required." });

            var auth = AuthorizeForTenant(body.ConnectionId);
            if (auth is not null) return auth;

            var (adapter, tenant, errResult) = await ResolveAdapterAsync(body.ConnectionId);
            if (errResult is not null) return errResult;

            if (!adapter!.Capabilities.SupportsMove)
            {
                return Ok(new ProvisionResponse
                {
                    Status = nameof(ProvisionOutcome.NotSupported),
                    ErrorMessage = $"Connector '{adapter.SystemType}' does not implement Move."
                });
            }

            StampInboundTable(tenant!);
            var sink = adapter.CreateSink(body.ConnectionId);
            if (sink is null)
            {
                return Ok(new ProvisionResponse
                {
                    Status = nameof(ProvisionOutcome.NotSupported),
                    ErrorMessage = $"Connector '{adapter.SystemType}' does not expose a sink for tenant {body.ConnectionId}."
                });
            }

            ProvisionResult result;
            try
            {
                result = await sink.MoveAsync(externalId, body.NewContainer, ct);
            }
            catch (NotSupportedException ex)
            {
                result = ProvisionResult.NotSupported(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProvisioningController.Move threw for connection {ConnId} / target {Target}",
                    body.ConnectionId, externalId);
                result = ProvisionResult.Failed($"Conduit-side exception: {ex.Message}");
            }

            return Ok(ToResponse(result));
        }

        /// <summary>POST /api/v1/users/{externalId}/reset-password — set a new password.</summary>
        [HttpPost("users/{externalId}/reset-password")]
        public async Task<IActionResult> ResetPassword(string externalId, [FromBody] ResetPasswordRequest body, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(externalId))
                return BadRequest(new { error = "externalId path segment is required." });
            if (body is null) return BadRequest(new { error = "Request body is required." });
            if (body.ConnectionId == Guid.Empty)
                return BadRequest(new { error = "connectionId is required." });
            if (string.IsNullOrEmpty(body.Password))
                return BadRequest(new { error = "password is required." });

            var auth = AuthorizeForTenant(body.ConnectionId);
            if (auth is not null) return auth;

            var (adapter, tenant, errResult) = await ResolveAdapterAsync(body.ConnectionId);
            if (errResult is not null) return errResult;

            if (!adapter!.Capabilities.SupportsResetPassword)
            {
                return Ok(new ProvisionResponse
                {
                    Status = nameof(ProvisionOutcome.NotSupported),
                    ErrorMessage = $"Connector '{adapter.SystemType}' does not implement ResetPassword."
                });
            }

            StampInboundTable(tenant!);
            var sink = adapter.CreateSink(body.ConnectionId);
            if (sink is null)
            {
                return Ok(new ProvisionResponse
                {
                    Status = nameof(ProvisionOutcome.NotSupported),
                    ErrorMessage = $"Connector '{adapter.SystemType}' does not expose a sink for tenant {body.ConnectionId}."
                });
            }

            ProvisionResult result;
            try
            {
                result = await sink.ResetPasswordAsync(externalId, body.Password, body.RequireChangeAtNextLogin, ct);
            }
            catch (NotSupportedException ex)
            {
                result = ProvisionResult.NotSupported(ex.Message);
            }
            catch (Exception ex)
            {
                // No payload — exception messages may rarely include offending input.
                _logger.LogError(ex, "ProvisioningController.ResetPassword threw for connection {ConnId} / target {Target}",
                    body.ConnectionId, externalId);
                result = ProvisionResult.Failed("Conduit-side exception (see server log).");
            }

            return Ok(ToResponse(result));
        }

        // ─── Helpers ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Tenant-scope guard. If the token is Tenant-scoped, body.connectionId
        /// MUST match the token's TenantId — otherwise we 403. Admin-scope tokens
        /// can drive any tenant.
        /// </summary>
        private IActionResult? AuthorizeForTenant(Guid connectionId)
        {
            if (_tenantContext.IsAdmin) return null;
            var tokenTenant = _tenantContext.TenantId;
            if (tokenTenant is null)
                return Unauthorized(new { error = "Token has no tenant scope." });
            if (tokenTenant.Value != connectionId)
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = "Token is not scoped to the requested connectionId."
                });
            return null;
        }

        /// <summary>
        /// Resolve the tenant + its adapter. Returns BOTH on success (the tenant is
        /// needed to stamp the inbound IC target table), or an IActionResult to
        /// short-circuit (404 / NotSupported response).
        /// </summary>
        private async Task<(IConnectorAdapter? adapter, Tenant? tenant, IActionResult? error)> ResolveAdapterAsync(Guid connectionId)
        {
            var tenant = await _tenants.GetByIdAsync(connectionId);
            if (tenant is null)
                return (null, null, NotFound(new { error = $"Connection {connectionId} not found." }));
            if (!tenant.IsActive)
                return (null, null, BadRequest(new { error = $"Connection {connectionId} is not active." }));

            var adapter = _registry.Get(tenant.SystemType);
            if (adapter is null)
            {
                return (null, null, Ok(new ProvisionResponse
                {
                    Status = nameof(ProvisionOutcome.NotSupported),
                    ErrorMessage = $"No connector adapter registered for system type '{tenant.SystemType}'."
                }));
            }
            return (adapter, tenant, null);
        }

        /// <summary>
        /// INBOUND-PATH ambient table stamp. The IdentityCenter connector reads the
        /// target table (Objects | Identities) from <see cref="IdentityCenterTableContext"/>.
        /// On the SYNC path the orchestrator stamps it per side from the project's
        /// V22 SourceTable/SinkTable. On THIS inbound proxy path there is no project,
        /// so we stamp the SINK side from the connection's <see cref="Tenant.TargetTable"/>
        /// (NULL → Objects, back-compat). No-op for non-IC connectors (they never read it).
        /// </summary>
        private static void StampInboundTable(Tenant tenant)
        {
            if (string.Equals(tenant.SystemType, "IdentityCenter", StringComparison.OrdinalIgnoreCase))
            {
                IdentityCenterTableContext.Sink =
                    string.Equals(tenant.TargetTable, "Identities", StringComparison.OrdinalIgnoreCase)
                        ? "Identities"
                        : "Objects";
            }
        }

        private static ConnectorObject BuildConnectorObject(ProvisionRequest body)
        {
            var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            // Canonical SCIM-ish keys.
            attrs["userName"] = body.Username;
            attrs["sAMAccountName"] = body.Username;
            if (!string.IsNullOrEmpty(body.Email))
            {
                attrs["mail"] = body.Email;
                // Default UPN to email if caller didn't supply attributes["userPrincipalName"].
                attrs["userPrincipalName"] = body.Email;
            }
            if (!string.IsNullOrEmpty(body.DisplayName)) attrs["displayName"] = body.DisplayName;
            if (!string.IsNullOrEmpty(body.FirstName)) attrs["givenName"] = body.FirstName;
            if (!string.IsNullOrEmpty(body.LastName)) attrs["sn"] = body.LastName;
            if (!string.IsNullOrEmpty(body.Department)) attrs["department"] = body.Department;
            if (!string.IsNullOrEmpty(body.JobTitle)) attrs["title"] = body.JobTitle;
            if (!string.IsNullOrEmpty(body.ManagerExternalId)) attrs["manager"] = body.ManagerExternalId;
            if (!string.IsNullOrEmpty(body.TargetContainer)) attrs["targetOU"] = body.TargetContainer;
            attrs["active"] = body.Active;

            // Caller-supplied attributes win on collisions (raw connector hints).
            if (body.Attributes is not null)
            {
                foreach (var (k, v) in body.Attributes)
                {
                    if (string.IsNullOrEmpty(k)) continue;
                    attrs[k] = v;
                }
            }

            return new ConnectorObject
            {
                SourceId = body.ExternalId ?? string.Empty,
                ObjectClass = "User",
                Attributes = attrs
            };
        }

        private static ProvisionResponse ToResponse(ProvisionResult result) => new()
        {
            Status = result.Outcome.ToString(),
            ExternalId = result.ExternalId,
            Id = result.ExternalId,           // alias — CenturyCity's TryReadExternalIdAsync looks at both.
            TaskId = result.TaskId,
            ErrorMessage = result.ErrorMessage
        };
    }
}
