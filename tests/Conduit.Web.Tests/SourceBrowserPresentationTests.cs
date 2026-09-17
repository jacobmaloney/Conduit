using System.Reflection;
using IdentityCenter.Brand.Components.Modals;
using IdentityCenter.Brand.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Conduit.Web.Tests;

public class SourceBrowserPresentationTests
{
    private static readonly Guid UserStep = Guid.NewGuid(), GroupStep = Guid.NewGuid();
    private static BrandSourceContext Context(Guid? id = null) => new("Example AD", "Conduit test host", "Example project",
        id, id == GroupStep ? "Group" : "User", ["User", "Group"], false, id != null, "Choose the saved import step.",
        [new(UserStep, "Import users"), new(GroupStep, "Import groups")], [new("Included containers", "OU=Staff,DC=example")]);

    [Fact]
    public async Task InitialProjectOffersClickableStepsWithoutPrematureScopeOrSampleControls()
    {
        var reads = 0;
        var html = await Render((_, _) => Task.FromResult(Context()), (_, _) => { reads++; throw new Exception("No automatic source reads"); });
        Assert.Contains("What would you like to browse?", html);
        Assert.Matches("<button[^>]+>\\s*<span>Import users</span>", html);
        Assert.Matches("<button[^>]+>\\s*<span>Import groups</span>", html);
        Assert.DoesNotContain("OU=Staff", html);
        Assert.DoesNotContain("Load sample", html);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task SelectedStepShowsItsScopeAndAnExplicitSampleAction()
    {
        var reads = 0;
        var html = await Render((_, _) => Task.FromResult(Context(GroupStep)), (_, _) => { reads++; throw new Exception("No automatic source reads"); });
        Assert.Contains("Object class: Group", html);
        Assert.Contains("Change selection", html);
        Assert.Contains("Load sample", html);
        Assert.Contains("OU=Staff", html);
        Assert.DoesNotContain("What would you like to browse?", html);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task ChoosingAValidStepRequestsOnlyItsMetadataAndUnknownStepsAreIgnored()
    {
        Guid? requested = null;
        var calls = 0;
        var reads = 0;
#pragma warning disable BL0005
        using var component = new BrandSourceBrowser
        {
            Describe = (id, _) => { calls++; requested = id; return Task.FromResult(Context(id)); },
            Read = (_, _) => { reads++; throw new Exception("Must explicitly load sample"); }
        };
#pragma warning restore BL0005
        await Invoke(component, "OnInitializedAsync");
        await Invoke(component, "SelectStepAsync", Guid.NewGuid());
        Assert.Equal(1, calls);
        Assert.Null(requested);
        await Invoke(component, "SelectStepAsync", GroupStep);
        Assert.Equal(GroupStep, requested);
        Assert.Equal(2, calls);
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryMatchesWhetherTheHostSaysFailureCanBeRetried(bool retryable)
    {
        var reads = 0;
        var html = await Render((_, _) => throw new BrandSourceBrowserException("Named capability restriction", retryable),
            (_, _) => { reads++; throw new Exception("Must not read"); });
        Assert.Contains("Named capability restriction", html);
        Assert.Contains(retryable ? "Unable to load source data" : "Source browsing unavailable", html);
        Assert.Equal(retryable, html.Contains(">Retry</button>"));
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData("ActiveDirectory", true, null)]
    [InlineData("CSV", true, null)]
    [InlineData("ActiveDirectory", false, "inactive")]
    [InlineData("EntraID", true, "EntraID")]
    public void BrowseAvailabilityUsesTheSameHostCapability(string type, bool active, string? reason)
    {
        var actual = Services.SourceBrowseService.UnavailableReason(type, active);
        if (reason == null) Assert.Null(actual);
        else Assert.Contains(reason, actual);
    }

    private static Task Invoke(BrandSourceBrowser component, string method, params object?[] args) =>
        (Task)typeof(BrandSourceBrowser).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, args)!;

    private static async Task<string> Render(Func<Guid?, CancellationToken, Task<BrandSourceContext>> describe,
        Func<BrandSourceQuery, CancellationToken, Task<BrandSourcePage>> read)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<BrandSourceBrowser>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["Describe"] = describe, ["Read"] = read }))).ToHtmlString());
    }
}
