using System.Runtime.CompilerServices;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// The sidebar's Connections badge and the Connections page must count the same rows. The page
/// hides the built-in Default sandbox tenant (TenantRepository.DefaultTenantId); on a fresh catalog
/// the badge read "1" over an empty list (Jacob, 2026-10-16) because the rail counted every row.
/// </summary>
public class NavRailCountTests
{
    [Fact]
    public void The_connections_badge_excludes_the_default_sandbox_tenant_like_the_page_does()
    {
        var nav = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Shared", "NavMenu.razor")));
        var page = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Pages", "ConnectedSystems.razor")));

        Assert.Contains("connectionCount = tenants.Count(t => t.Id != TenantRepository.DefaultTenantId);", nav);
        Assert.Contains(".Where(s => s.TenantId != TenantRepository.DefaultTenantId)", page);
    }

    /// <summary>
    /// The dashboard tile read "2 connections, 2 active" beside a navigation count of 1 (Jacob, 2026-11-24:
    /// "connections say 2 active, there's only 1"); the Sync Center tile counted the same way.
    /// </summary>
    [Fact]
    public void The_dashboard_and_sync_center_tiles_exclude_the_default_sandbox_tenant_too()
    {
        var dashboard = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Pages", "Index.razor")));
        var center = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Pages", "Sync", "SynchronizationCenter.razor")));

        Assert.Contains("tenants.Where(t => t.Id != Conduit.DataAccess.Repositories.TenantRepository.DefaultTenantId)", dashboard);
        Assert.DoesNotContain("connectionCount = tenants.Count;", dashboard);
        Assert.Contains("tenants.Count(t => t.Id != Conduit.DataAccess.Repositories.TenantRepository.DefaultTenantId)", center);
    }

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));
}
