using Conduit.Core.SyncModels;
using Conduit.Core.Models;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Security;

namespace Conduit.Sync.Provisioning;

/// <summary>
/// Establishes the local IdentityCenter connection-name to Conduit credential route
/// from project configuration. Provisioning readiness must not depend on a sync run.
/// </summary>
public sealed class ProvisioningRouteRegistrar
{
    private readonly TenantRepository _tenants;
    private readonly SinkConnectionCredentialMapRepository _routes;

    public ProvisioningRouteRegistrar(TenantRepository tenants, SinkConnectionCredentialMapRepository routes)
    {
        _tenants = tenants;
        _routes = routes;
    }

    /// <returns>The registered directory-connection name, or null when the sink is not IdentityCenter.</returns>
    public async Task<string?> EnsureForProjectAsync(SyncProject project)
    {
        var sink = await _tenants.GetByIdAsync(project.SinkTenantId)
            ?? throw new InvalidOperationException($"Sink connection {project.SinkTenantId} was not found.");
        var source = await _tenants.GetByIdAsync(project.SourceTenantId)
            ?? throw new InvalidOperationException($"Source connection {project.SourceTenantId} was not found.");
        var name = ResolveRouteName(source, sink);
        if (name is null)
            return null;

        await _routes.UpsertAsync(name, source.Id);
        return name;
    }

    /// <summary>Pure route decision used by configuration validation and tests.</summary>
    public static string? ResolveRouteName(Tenant source, Tenant sink) =>
        string.Equals(sink.SystemType, "IdentityCenter", StringComparison.OrdinalIgnoreCase)
            ? IdentityCenterSourceName.Sanitize(source.Name)
            : null;
}
