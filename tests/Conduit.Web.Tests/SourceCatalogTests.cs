using System.Security.Claims;
using System.Text;
using Conduit.Connectors.IdentityCenter;
using Conduit.Core.Models;
using Conduit.Core.SyncModels;
using Conduit.SourceBrowsing;
using Conduit.Web.Services;
using Xunit;

namespace Conduit.Web.Tests;

public class SourceCatalogTests
{
    private static ClaimsPrincipal Admin => new(new ClaimsIdentity([new(ClaimTypes.Role, "Admin")], "Cookies"));
    [Fact] public async Task CatalogFiltersConnectionAndProjectBySourceTenant()
    {
        var store = new Store(); var service = new SourceCatalogService(store, new(Guid.NewGuid(), "fixture"), new Remote());
        var token = new ClaimsPrincipal(new ClaimsIdentity([new("TokenScope", "Tenant"), new("TenantId", store.First.Id.ToString())], "ApiToken"));
        var result = await service.LocalAsync(token, default);
        Assert.Equal(2, result.Items.Count); Assert.All(result.Items, i => Assert.Equal(store.First.Id, i.SourceConnectionId));
        Assert.DoesNotContain(result.Items, i => i.Id == store.Inbound.Id); // owning the sink is insufficient
        var all = await service.LocalAsync(Admin, default); Assert.Equal(4, all.Items.Count);
    }
    [Fact] public async Task NonAdminDoesNotQueryStore()
    {
        var store = new Store(); var service = new SourceCatalogService(store, new(Guid.NewGuid(), "fixture"), new Remote());
        var error = await Assert.ThrowsAsync<SourceBrowseException>(() => service.LocalAsync(new(new ClaimsIdentity([], "Cookies")), default));
        Assert.Equal("AdministratorRequired", error.Code); Assert.Equal(0, store.Reads);
    }
    [Fact] public async Task RemoteIcCatalogUsesOnlyAuthorizedIcConnection()
    {
        var store = new Store(); var remote = new Remote(); var service = new SourceCatalogService(store, new(Guid.NewGuid(), "fixture"), remote);
        var wrongType = await Assert.ThrowsAsync<SourceBrowseException>(() => service.IdentityCenterAsync(Admin, store.First.Id, default));
        Assert.Equal("WrongConnectionType", wrongType.Code); Assert.Equal(0, remote.Reads);
        store.First.SystemType = "IdentityCenter";
        var token = new ClaimsPrincipal(new ClaimsIdentity([new("TokenScope", "Tenant"), new("TenantId", store.Second.Id.ToString())], "ApiToken"));
        Assert.Equal("SourceAccessDenied", (await Assert.ThrowsAsync<SourceBrowseException>(() => service.IdentityCenterAsync(token, store.First.Id, default))).Code);
        Assert.Equal(0, remote.Reads);
        await service.IdentityCenterAsync(Admin, store.First.Id, default); Assert.Equal(1, remote.Reads); Assert.Equal(store.First.Id, remote.Id);
    }
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pass@example.test")]
    [InlineData("https://example.test?redirect=other")]
    [InlineData("https://example.test#fragment")]
    public void UntrustedPortalUrlCannotBecomeAnAction(string url)
    {
        Assert.Null(SourceCatalogLinks.PortalBrowseUrl(url, Item()));
    }
    [Fact] public void PortalLinksPreserveBasePathAndExactIds()
    {
        var item = Item(); var url = SourceCatalogLinks.PortalBrowseUrl("https://ic.example.test/portal/", item)!;
        Assert.StartsWith("https://ic.example.test/portal/admin/system-center/projects?", url);
        Assert.Contains($"browseConnection={item.SourceConnectionId:D}", url); Assert.Contains($"browseProject={item.Id:D}", url);
    }
    [Fact] public async Task ResponseCapWorksWithoutContentLength()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 20)));
        var error = await Assert.ThrowsAsync<SourceBrowseException>(() => SourceBrowseJson.ReadAsync<object>(stream, default, 10));
        Assert.Equal("ResponseTooLarge", error.Code);
        await using var valid = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":1}"));
        Assert.Equal(1, (await SourceBrowseJson.ReadAsync<Dictionary<string, int>>(valid, default, 20))["id"]);
    }
    private static SourceCatalogItem Item() => new(Guid.NewGuid(), "Project", "Fixture", Guid.NewGuid(), "CSV", "Feed", true, true);
    private sealed class Store : ISourceCatalogStore
    {
        public Tenant First = new() { Id = Guid.NewGuid(), Name = "Allowed", SystemType = "CSV" }, Second = new() { Id = Guid.NewGuid(), Name = "Other", SystemType = "CSV" };
        public SyncProject Outbound, Inbound;
        public int Reads;
        public Store() { Outbound = new() { SourceTenantId = First.Id, SinkTenantId = Second.Id }; Inbound = new() { SourceTenantId = Second.Id, SinkTenantId = First.Id }; }
        public Task<IReadOnlyList<Tenant>> ConnectionsAsync() { Reads++; return Task.FromResult<IReadOnlyList<Tenant>>([First, Second]); }
        public Task<IReadOnlyList<SyncProject>> ProjectsAsync() { Reads++; return Task.FromResult<IReadOnlyList<SyncProject>>([Outbound, Inbound]); }
    }
    private sealed class Remote : IIdentityCenterSourceCatalogClient
    {
        public int Reads; public Guid Id;
        public Task<SourceCatalog> ReadAsync(Guid id, CancellationToken ct) { Reads++; Id = id; return Task.FromResult(new SourceCatalog("IC", null, "https://ic.example.test", [])); }
    }
}
