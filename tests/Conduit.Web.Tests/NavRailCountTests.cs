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

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));
}
