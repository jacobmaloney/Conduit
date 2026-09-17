using System.Security.Claims;
using Conduit.Connectors.IdentityCenter;
using Conduit.Core.Models;
using Conduit.Core.SyncModels;
using Conduit.DataAccess.Repositories;
using Conduit.SourceBrowsing;

namespace Conduit.Web.Services;

public interface ISourceCatalogStore
{
    Task<IReadOnlyList<Tenant>> ConnectionsAsync();
    Task<IReadOnlyList<SyncProject>> ProjectsAsync();
}
public sealed class SourceCatalogStore(TenantRepository tenants, SyncProjectRepository projects) : ISourceCatalogStore
{
    public async Task<IReadOnlyList<Tenant>> ConnectionsAsync() => await tenants.GetAllAsync(includeInactive: true);
    public async Task<IReadOnlyList<SyncProject>> ProjectsAsync() => await projects.GetAllAsync();
}

public sealed class SourceCatalogService(ISourceCatalogStore store, SourceBrowseHost host, IIdentityCenterSourceCatalogClient ic)
{
    public async Task<SourceCatalog> LocalAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); SourceBrowseService.RequireAuthenticated(user);
        var connections = (await store.ConnectionsAsync()).Where(c => c.Id != TenantRepository.DefaultTenantId && CanRead(user, c.Id)).ToArray();
        var byId = connections.ToDictionary(c => c.Id);
        var projects = (await store.ProjectsAsync()).Where(p => byId.ContainsKey(p.SourceTenantId)).ToArray();
        ct.ThrowIfCancellationRequested();
        var items = connections.Select(c => new SourceCatalogItem(c.Id, "Connection", c.Name, c.Id, c.SystemType, c.Name, c.IsActive,
            c.IsActive && SourceBrowseService.Supports(c.SystemType), SourceBrowseService.UnavailableReason(c.SystemType, c.IsActive)))
            .Concat(projects.Select(p => { var source = byId[p.SourceTenantId]; return new SourceCatalogItem(p.Id, "Project", p.Name, p.SourceTenantId, source.SystemType, source.Name,
                p.IsEnabled, source.IsActive && SourceBrowseService.Supports(source.SystemType), SourceBrowseService.UnavailableReason(source.SystemType, source.IsActive)); })).ToArray();
        return new($"Conduit · {host.Name}", host.InstanceId, null, items);
    }
    public async Task<SourceCatalog> IdentityCenterAsync(ClaimsPrincipal user, Guid connectionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); SourceBrowseService.AuthorizeSource(user, connectionId);
        var connection = (await store.ConnectionsAsync()).SingleOrDefault(c => c.Id == connectionId && c.IsActive)
            ?? throw new SourceBrowseException("ConnectionUnavailable", "The selected IC connection is unavailable or inactive.");
        if (!connection.SystemType.Equals("IdentityCenter", StringComparison.OrdinalIgnoreCase) && !connection.SystemType.Equals("CertificationCenterSaaS", StringComparison.OrdinalIgnoreCase))
            throw new SourceBrowseException("WrongConnectionType", "Select an IdentityCenter connection to load its catalog.");
        return await ic.ReadAsync(connection.Id, ct);
    }
    private static bool CanRead(ClaimsPrincipal user, Guid id)
    {
        try { SourceBrowseService.AuthorizeSource(user, id); return true; }
        catch (SourceBrowseException) { return false; }
    }
}
