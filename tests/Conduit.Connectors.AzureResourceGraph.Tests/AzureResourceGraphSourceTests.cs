using System.Net;
using System.Text;
using Conduit.Connectors.AzureResourceGraph;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Connectors.AzureResourceGraph.Tests;

/// <summary>
/// Unit tests for the Azure Resource Graph connector. No network: a stub
/// HttpMessageHandler serves canned ARG responses, and the page loop is driven
/// through the internal EnumeratePagesAsync seam (so no CredentialProtector /
/// real token is needed).
/// </summary>
public class AzureResourceGraphSourceTests
{
    private static AzureResourceGraphSource NewSource(HttpMessageHandler handler) =>
        new(Guid.NewGuid(), protector: null!, NullLogger<AzureResourceGraphSource>.Instance, handler);

    private static async Task<List<ConnectorObject>> DrainAsync(
        AzureResourceGraphSource source, HttpClient http, string kql, bool isResource,
        string objectClass, SyncProjectScope? scope = null)
    {
        var list = new List<ConnectorObject>();
        await foreach (var o in source.EnumeratePagesAsync(
            http, "fake-token", kql, isResource, scope ?? new SyncProjectScope(),
            Array.Empty<string>(), Array.Empty<string>(), objectClass, CancellationToken.None))
        {
            list.Add(o);
        }
        return list;
    }

    // ─── IsArmHost guard ────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01", true)]
    [InlineData("https://MANAGEMENT.AZURE.COM/providers/Microsoft.ResourceGraph/resources", true)]
    [InlineData("https://management.azure.com.evil.com/providers/x", false)]
    [InlineData("https://evilmanagement.azure.com/providers/x", false)]
    [InlineData("https://graph.microsoft.com/v1.0/users", false)]
    [InlineData("https://management.usgovcloudapi.net/providers/x", false)]
    [InlineData("https://management.chinacloudapi.cn/providers/x", false)]
    [InlineData("http://management.azure.com/providers/x", false)]
    public void IsArmHost_only_accepts_commercial_arm_over_https(string url, bool expected)
    {
        Assert.Equal(expected, ArgHttp.IsArmHost(new Uri(url)));
    }

    // ─── static KQL guards ──────────────────────────────────────────────────

    [Fact]
    public void Subscriptions_kql_is_the_expected_constant()
    {
        Assert.Equal(
            "ResourceContainers | where type =~ 'microsoft.resources/subscriptions' | project id, name, subscriptionId, tenantId, properties",
            AzureResourceGraphSource.SubscriptionsKql);
    }

    [Fact]
    public void Resources_kql_is_the_expected_constant()
    {
        Assert.Equal(
            "Resources | project id, name, type, location, subscriptionId, resourceGroup, sku, tags, properties",
            AzureResourceGraphSource.ResourcesKql);
    }

    [Fact]
    public void Kql_constants_contain_no_interpolation_holes()
    {
        Assert.DoesNotContain("{", AzureResourceGraphSource.SubscriptionsKql);
        Assert.DoesNotContain("}", AzureResourceGraphSource.SubscriptionsKql);
        Assert.DoesNotContain("{", AzureResourceGraphSource.ResourcesKql);
        Assert.DoesNotContain("}", AzureResourceGraphSource.ResourcesKql);
    }

    // ─── skipToken paging ───────────────────────────────────────────────────

    [Fact]
    public async Task Paging_follows_skipToken_then_stops()
    {
        const string page1 = """
        { "data": [ { "id": "/subs/a", "name": "A" } ], "$skipToken": "TOK2" }
        """;
        const string page2 = """
        { "data": [ { "id": "/subs/b", "name": "B" } ] }
        """;

        var handler = new StubHandler(req =>
        {
            // Assert every request targets the ARM host with a bearer token.
            Assert.Equal("management.azure.com", req.RequestUri!.Host);
            Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return body.Contains("TOK2") ? Json(page2) : Json(page1);
        });

        using var http = new HttpClient(handler);
        var source = NewSource(handler);
        var rows = await DrainAsync(source, http, AzureResourceGraphSource.SubscriptionsKql, isResource: false, AzureResourceGraphSource.SubscriptionClass);

        Assert.Equal(2, rows.Count);
        Assert.Equal("/subs/a", rows[0].SourceId);
        Assert.Equal("/subs/b", rows[1].SourceId);
        Assert.Equal(2, handler.CallCount);
    }

    // ─── resource-row mapping + AHB ─────────────────────────────────────────

    [Fact]
    public async Task Vm_with_windows_server_license_maps_hybrid_benefit_true()
    {
        const string page = """
        { "data": [ {
            "id": "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1",
            "name": "vm1",
            "type": "microsoft.compute/virtualmachines",
            "location": "eastus",
            "subscriptionId": "s",
            "resourceGroup": "rg",
            "sku": { "name": "Standard_D2s_v3" },
            "tags": { "env": "prod", "owner": "team" },
            "properties": { "licenseType": "Windows_Server", "hardwareProfile": { "vmSize": "Standard_D2s_v3" } }
        } ] }
        """;
        var handler = new StubHandler(_ => Json(page));
        using var http = new HttpClient(handler);
        var source = NewSource(handler);
        var rows = await DrainAsync(source, http, AzureResourceGraphSource.ResourcesKql, isResource: true, AzureResourceGraphSource.ResourceClass);

        var o = Assert.Single(rows);
        Assert.Equal("/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1", o.SourceId);
        Assert.Equal("azureresource", o.ObjectClass);
        Assert.Equal("vm1", o.Attributes["name"]);
        Assert.Equal("microsoft.compute/virtualmachines", o.Attributes["resourceType"]);
        Assert.Equal("Windows_Server", o.Attributes["licenseType"]);
        Assert.Equal(true, o.Attributes["azureHybridBenefit"]);
        Assert.Equal("Standard_D2s_v3", o.Attributes["sku"]);
        Assert.Equal("Standard_D2s_v3", o.Attributes["size"]);
        Assert.Equal("env=prod;owner=team", o.Attributes["tags"]);
    }

    [Fact]
    public async Task Payg_license_included_maps_hybrid_benefit_false()
    {
        const string page = """
        { "data": [ {
            "id": "/subscriptions/s/.../sqldb1", "name": "sqldb1",
            "type": "microsoft.sql/servers/databases",
            "properties": { "licenseType": "LicenseIncluded" }
        } ] }
        """;
        var handler = new StubHandler(_ => Json(page));
        using var http = new HttpClient(handler);
        var source = NewSource(handler);
        var rows = await DrainAsync(source, http, AzureResourceGraphSource.ResourcesKql, isResource: true, AzureResourceGraphSource.ResourceClass);

        var o = Assert.Single(rows);
        Assert.Equal("LicenseIncluded", o.Attributes["licenseType"]);
        Assert.Equal(false, o.Attributes["azureHybridBenefit"]);
    }

    [Fact]
    public async Task Resource_without_licenseType_omits_hybrid_benefit()
    {
        const string page = """
        { "data": [ { "id": "/r/x", "name": "x", "type": "microsoft.storage/storageaccounts", "properties": {} } ] }
        """;
        var handler = new StubHandler(_ => Json(page));
        using var http = new HttpClient(handler);
        var source = NewSource(handler);
        var rows = await DrainAsync(source, http, AzureResourceGraphSource.ResourcesKql, isResource: true, AzureResourceGraphSource.ResourceClass);

        var o = Assert.Single(rows);
        Assert.False(o.Attributes.ContainsKey("licenseType"));
        Assert.False(o.Attributes.ContainsKey("azureHybridBenefit"));
    }

    [Theory]
    [InlineData("Windows_Server", true)]
    [InlineData("Windows_Client", true)]
    [InlineData("BasePrice", true)]
    [InlineData("AHUB", true)]
    [InlineData("BYOL", true)]
    [InlineData("LicenseIncluded", false)]
    [InlineData("PayAsYouGo", false)]
    public void IsHybridBenefit_classifies_markers(string licenseType, bool expected)
    {
        Assert.Equal(expected, AzureResourceGraphSource.IsHybridBenefit(licenseType));
    }

    // ─── subscription mapping ───────────────────────────────────────────────

    [Fact]
    public async Task Subscription_row_maps_state_from_properties()
    {
        const string page = """
        { "data": [ {
            "id": "/subscriptions/abc", "name": "Prod Sub",
            "subscriptionId": "abc", "tenantId": "t",
            "properties": { "state": "Enabled" }
        } ] }
        """;
        var handler = new StubHandler(_ => Json(page));
        using var http = new HttpClient(handler);
        var source = NewSource(handler);
        var rows = await DrainAsync(source, http, AzureResourceGraphSource.SubscriptionsKql, isResource: false, AzureResourceGraphSource.SubscriptionClass);

        var o = Assert.Single(rows);
        Assert.Equal("/subscriptions/abc", o.SourceId);
        Assert.Equal("azuresubscription", o.ObjectClass);
        Assert.Equal("Prod Sub", o.Attributes["displayName"]);
        Assert.Equal("abc", o.Attributes["subscriptionId"]);
        Assert.Equal("Enabled", o.Attributes["state"]);
    }

    // ─── ScopeFilter GUID validation ────────────────────────────────────────

    [Fact]
    public void ScopeFilter_valid_guid_is_added_as_subscription()
    {
        var source = NewSource(new StubHandler(_ => Json("{}")));
        var guid = "11111111-1111-1111-1111-111111111111";
        var (subs, mgmt) = source.ParseScopeFilter(guid);
        Assert.Single(subs);
        Assert.Equal(guid, subs[0]);
        Assert.Empty(mgmt);
    }

    [Fact]
    public void ScopeFilter_non_guid_garbage_is_rejected_not_added_to_subscriptions()
    {
        var source = NewSource(new StubHandler(_ => Json("{}")));
        // contains characters illegal in both a GUID and a management-group id
        var (subs, mgmt) = source.ParseScopeFilter("not a guid!; drop table");
        Assert.Empty(subs);
        Assert.Empty(mgmt);
    }

    [Fact]
    public void ScopeFilter_mixed_keeps_guid_routes_name_as_management_group()
    {
        var source = NewSource(new StubHandler(_ => Json("{}")));
        var guid = "22222222-2222-2222-2222-222222222222";
        var (subs, mgmt) = source.ParseScopeFilter($"{guid}, my-mgmt-group");
        Assert.Single(subs);
        Assert.Equal(guid, subs[0]);
        Assert.Single(mgmt);
        Assert.Equal("my-mgmt-group", mgmt[0]);
    }

    // ─── request body: scope is in arrays, never concatenated into KQL ──────

    [Fact]
    public void BuildRequestBody_puts_subscriptions_in_array_and_leaves_kql_verbatim()
    {
        var guid = "33333333-3333-3333-3333-333333333333";
        var body = AzureResourceGraphSource.BuildRequestBody(
            AzureResourceGraphSource.ResourcesKql, new[] { guid }, Array.Empty<string>(), skipToken: null, top: 1000);

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal(AzureResourceGraphSource.ResourcesKql, doc.RootElement.GetProperty("query").GetString());
        var subs = doc.RootElement.GetProperty("subscriptions");
        Assert.Equal(guid, subs[0].GetString());
        Assert.Equal("objectArray", doc.RootElement.GetProperty("options").GetProperty("resultFormat").GetString());
        // GUID never appears inside the query text.
        Assert.DoesNotContain(guid, doc.RootElement.GetProperty("query").GetString());
    }

    // ─── dispatch: unknown class yields nothing ─────────────────────────────

    [Fact]
    public async Task ReadAsync_unknown_class_yields_nothing()
    {
        // Unknown class returns before any credential / network access.
        var source = NewSource(new StubHandler(_ => Json("{}")));
        var list = new List<ConnectorObject>();
        await foreach (var o in source.ReadAsync("notARealClass", new SyncProjectScope(), CancellationToken.None))
            list.Add(o);
        Assert.Empty(list);
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int CallCount { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responder(request));
        }
    }
}
