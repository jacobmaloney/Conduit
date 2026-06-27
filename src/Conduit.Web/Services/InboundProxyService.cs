using System;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Core.Models;
using Conduit.Core.Services;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Web.Services
{
    /// <summary>
    /// Phase 1 inbound proxy. Turns "a connection is a writable target" into reality:
    /// an inbound SCIM / REST CREATE for a connection whose adapter can actually
    /// provision an external system is forwarded to that system's sink (via the same
    /// CreateAsync path /api/v1/provision uses) INSTEAD of writing Conduit's local
    /// Users/Groups store.
    ///
    /// PROXY-vs-LOCAL decision (deliberately capability-driven, not a hardcoded type
    /// list): proxy when the resolved connection's adapter advertises
    /// <see cref="ConnectorCapabilities.SupportsCreate"/> — today IdentityCenter,
    /// ActiveDirectory, EntraID. Every other connection (Emulator + the slug-less
    /// default /scim/v2 route, which have no external create) falls through to the
    /// local store unchanged, so DemoSeed / GenerationService / the emulator demo and
    /// the legacy SCIM server keep working byte-for-byte.
    ///
    /// AUTH SCOPE: the active connection is whatever ApiTokenAuthMiddleware already
    /// resolved into TenantContext — the slug-vs-token cross-check there guarantees a
    /// token cannot be aimed at another connection by changing the URL, so the proxy
    /// inherits that guarantee. Every proxied write is audit-logged.
    /// </summary>
    public sealed class InboundProxyService
    {
        private readonly ITenantContext _tenantContext;
        private readonly TenantRepository _tenants;
        private readonly ConnectorRegistry _registry;
        private readonly ApplicationLogService _appLog;
        private readonly ILogger<InboundProxyService> _logger;

        public InboundProxyService(
            ITenantContext tenantContext,
            TenantRepository tenants,
            ConnectorRegistry registry,
            ApplicationLogService appLog,
            ILogger<InboundProxyService> logger)
        {
            _tenantContext = tenantContext;
            _tenants = tenants;
            _registry = registry;
            _appLog = appLog;
            _logger = logger;
        }

        public enum ProxyDecision { Local, Proxied }

        /// <summary>
        /// Outcome the controller translates into an HTTP response.
        ///   Decision == Local   → the caller writes the local store as before.
        ///   Decision == Proxied → the sink handled it; map Outcome to SCIM 201 / error.
        /// </summary>
        public sealed class ProxyResult
        {
            public ProxyDecision Decision { get; init; }
            public ProvisionOutcome Outcome { get; init; }
            public string? ExternalId { get; init; }
            public string? ErrorMessage { get; init; }
            public string SystemType { get; init; } = string.Empty;

            public static ProxyResult Local() => new() { Decision = ProxyDecision.Local };
            public static ProxyResult Proxied(ProvisionOutcome outcome, string systemType, string? externalId, string? error) =>
                new() { Decision = ProxyDecision.Proxied, Outcome = outcome, SystemType = systemType, ExternalId = externalId, ErrorMessage = error };
        }

        /// <summary>
        /// Resolve the active connection and decide whether this CREATE should proxy.
        /// Returns Local immediately (no DB hit beyond the tenant lookup) when there's
        /// no scoped tenant or the adapter can't create. Otherwise forwards the mapped
        /// <paramref name="obj"/> to the sink and returns the provision outcome.
        /// </summary>
        public async Task<ProxyResult> TryProxyCreateAsync(
            ConnectorObject obj,
            string resourceKindForAudit,
            CancellationToken ct)
        {
            // No resolved tenant → the slug-less default route / unscoped admin token.
            // Keep the legacy local-store behavior.
            var tenantId = _tenantContext.TenantId;
            if (tenantId is null) return ProxyResult.Local();

            var tenant = await _tenants.GetByIdAsync(tenantId.Value);
            if (tenant is null || !tenant.IsActive) return ProxyResult.Local();

            var adapter = _registry.Get(tenant.SystemType);
            if (adapter is null || !adapter.Capabilities.SupportsCreate)
                return ProxyResult.Local();

            // This connection is a writable external target → PROXY.
            // Stamp the inbound IC table from the connection's TargetTable (NULL →
            // Objects). No-op for non-IC adapters (they never read the context).
            StampInboundTable(tenant);

            var sink = adapter.CreateSink(tenant.Id);
            if (sink is null)
            {
                // Adapter claims create but exposes no sink for this tenant — surface
                // honestly rather than silently writing the local store.
                return ProxyResult.Proxied(ProvisionOutcome.NotSupported, tenant.SystemType, null,
                    $"Connector '{tenant.SystemType}' exposes no sink for connection {tenant.Id}.");
            }

            ProvisionResult result;
            try
            {
                result = await sink.CreateAsync(obj, ct);
            }
            catch (NotSupportedException ex)
            {
                result = ProvisionResult.NotSupported(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Inbound proxy CREATE threw (connection={ConnId}, type={Type}, sourceId={SrcId})",
                    tenant.Id, tenant.SystemType, obj.SourceId);
                result = ProvisionResult.Failed($"Conduit-side exception: {ex.Message}");
            }

            await AuditAsync(tenant, resourceKindForAudit, obj.SourceId, result);

            return ProxyResult.Proxied(result.Outcome, tenant.SystemType, result.ExternalId, result.ErrorMessage);
        }

        /// <summary>
    /// Phase 2. Resolve the active connection and decide whether this UPDATE
    /// (SCIM PUT/PATCH, /api/v1 PATCH) should proxy. Same scope guarantee as
    /// <see cref="TryProxyCreateAsync"/> (the token↔slug cross-check in
    /// ApiTokenAuthMiddleware bounds which connection this can reach).
    ///
    /// Gates on the adapter's <see cref="ConnectorCapabilities.SupportsUpdate"/> —
    /// NOT SupportsCreate — so a connection that can create but not update
    /// short-circuits to a NotSupported (501) outcome rather than silently writing
    /// the local store. <paramref name="externalId"/> is the SCIM resource id the
    /// caller addressed; <paramref name="replace"/> is true for PUT, false for PATCH.
    /// </summary>
    public async Task<ProxyResult> TryProxyUpdateAsync(
        string externalId,
        ConnectorObject changes,
        bool replace,
        string resourceKindForAudit,
        CancellationToken ct)
    {
        var tenantId = _tenantContext.TenantId;
        if (tenantId is null) return ProxyResult.Local();

        var tenant = await _tenants.GetByIdAsync(tenantId.Value);
        if (tenant is null || !tenant.IsActive) return ProxyResult.Local();

        var adapter = _registry.Get(tenant.SystemType);
        if (adapter is null) return ProxyResult.Local();

        // This connection is a writable external target. If the adapter can't
        // UPDATE, surface NotSupported (501) rather than falling through to the
        // local store — falling through would write Conduit's local Users/Groups
        // for a connection the caller believes is the external target, which is the
        // exact silent-misroute the create path guards against.
        if (!adapter.Capabilities.SupportsUpdate)
        {
            // Only short-circuit for connections that ARE external targets at all
            // (they can create). A pure-local connection (Emulator / no create)
            // keeps the legacy local-store update path.
            if (!adapter.Capabilities.SupportsCreate)
                return ProxyResult.Local();
            return ProxyResult.Proxied(ProvisionOutcome.NotSupported, tenant.SystemType, null,
                $"Connector '{tenant.SystemType}' does not support inbound update.");
        }

        StampInboundTable(tenant);

        var sink = adapter.CreateSink(tenant.Id);
        if (sink is null)
            return ProxyResult.Proxied(ProvisionOutcome.NotSupported, tenant.SystemType, null,
                $"Connector '{tenant.SystemType}' exposes no sink for connection {tenant.Id}.");

        ProvisionResult result;
        try
        {
            result = await sink.UpdateAsync(externalId, changes, replace, ct);
        }
        catch (NotSupportedException ex)
        {
            result = ProvisionResult.NotSupported(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Inbound proxy UPDATE threw (connection={ConnId}, type={Type}, externalId={ExtId})",
                tenant.Id, tenant.SystemType, externalId);
            result = ProvisionResult.Failed($"Conduit-side exception: {ex.Message}");
        }

        await AuditAsync(tenant, resourceKindForAudit, externalId, result, "UPDATE");

        return ProxyResult.Proxied(result.Outcome, tenant.SystemType, result.ExternalId, result.ErrorMessage);
    }

    /// <summary>
    /// Phase 2. Resolve the active connection and decide whether this DELETE
    /// (SCIM DELETE /Users|/Groups/{id}) should proxy. Same scope guarantee as the
    /// create/update paths. <paramref name="externalId"/> is the SCIM id the caller
    /// addressed (for IC the target row GUID; the IC sink resolves it to the row's
    /// SourceUniqueId and tombstone-deletes under the connection's source).
    ///
    /// Gates on <see cref="ConnectorCapabilities.SupportsDelete"/>. A create-capable
    /// connection that can't delete → NotSupported (501), never a silent local
    /// delete. A pure-local connection keeps the legacy local-store delete path.
    /// </summary>
    public async Task<ProxyResult> TryProxyDeleteAsync(
        string externalId,
        string resourceKindForAudit,
        CancellationToken ct)
    {
        var tenantId = _tenantContext.TenantId;
        if (tenantId is null) return ProxyResult.Local();

        var tenant = await _tenants.GetByIdAsync(tenantId.Value);
        if (tenant is null || !tenant.IsActive) return ProxyResult.Local();

        var adapter = _registry.Get(tenant.SystemType);
        if (adapter is null) return ProxyResult.Local();

        if (!adapter.Capabilities.SupportsDelete)
        {
            if (!adapter.Capabilities.SupportsCreate)
                return ProxyResult.Local();   // pure-local connection → local delete
            return ProxyResult.Proxied(ProvisionOutcome.NotSupported, tenant.SystemType, null,
                $"Connector '{tenant.SystemType}' does not support inbound delete.");
        }

        StampInboundTable(tenant);

        var sink = adapter.CreateSink(tenant.Id);
        if (sink is null)
            return ProxyResult.Proxied(ProvisionOutcome.NotSupported, tenant.SystemType, null,
                $"Connector '{tenant.SystemType}' exposes no sink for connection {tenant.Id}.");

        SinkWriteResult writeResult;
        try
        {
            writeResult = await sink.DeleteAsync(externalId, ct);
        }
        catch (NotSupportedException ex)
        {
            var nr = ProvisionResult.NotSupported(ex.Message);
            await AuditAsync(tenant, resourceKindForAudit, externalId, nr, "DELETE");
            return ProxyResult.Proxied(nr.Outcome, tenant.SystemType, null, nr.ErrorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Inbound proxy DELETE threw (connection={ConnId}, type={Type}, externalId={ExtId})",
                tenant.Id, tenant.SystemType, externalId);
            writeResult = SinkWriteResult.Fail($"Conduit-side exception: {ex.Message}");
        }

        // Map the sink's SinkWriteResult to a ProvisionResult. A successful soft/hard
        // delete (Updated) and an idempotent no-op (Skipped — id already gone, or
        // Identities table) both surface as Success so a re-issued DELETE is 204.
        var result = writeResult.Outcome == SinkWriteOutcome.Failed
            ? ProvisionResult.Failed(writeResult.ErrorMessage ?? "Sink rejected the delete.")
            : ProvisionResult.Success(externalId);

        await AuditAsync(tenant, resourceKindForAudit, externalId, result, "DELETE");

        return ProxyResult.Proxied(result.Outcome, tenant.SystemType, result.ExternalId, result.ErrorMessage);
    }

    private async Task AuditAsync(Tenant tenant, string resourceKind, string sourceId, ProvisionResult result, string verb = "CREATE")
        {
            var level = result.Outcome is ProvisionOutcome.Success or ProvisionOutcome.Accepted
                ? ApplicationLogService.LogLevel.Info
                : ApplicationLogService.LogLevel.Warning;
            try
            {
                await _appLog.LogAsync(level, $"InboundProxy/{verb}",
                    $"Proxied {resourceKind} {verb.ToLowerInvariant()} -> {tenant.SystemType} '{tenant.Name}' ({tenant.Slug}): {result.Outcome}",
                    details: $"connectionId={tenant.Id} sourceId={sourceId} externalId={result.ExternalId} table={tenant.TargetTable ?? "Objects"} error={result.ErrorMessage}");
            }
            catch (Exception ex)
            {
                // Audit must never fail the write path.
                _logger.LogWarning(ex, "Inbound proxy audit log failed (connection={ConnId})", tenant.Id);
            }
        }

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
    }
}
