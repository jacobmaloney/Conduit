using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// Guards the "wizard refreshes while you're typing" defect: DatabaseOffline.razor used to
/// emit &lt;meta http-equiv="refresh"&gt; inside &lt;HeadContent&gt;, which Blazor projects into the
/// live document.head. That arms a DOCUMENT-level navigation which client-side nav to /setup
/// does not cancel — removing the meta element does not unschedule it — so the wizard got
/// hard-navigated out from under the operator ~10s later, wiping every bound field.
/// Recovery polling must stay component-scoped and disposable. Do not re-add the meta refresh.
/// </summary>
public class DatabaseOfflineMetaRefreshTests
{
    [Fact]
    public void DatabaseOffline_MustNotArmDocumentLevelMetaRefresh()
    {
        var source = File.ReadAllText(FindRepoFile(Path.Combine("src", "Conduit.Web", "Pages", "DatabaseOffline.razor")));

        Assert.DoesNotContain("http-equiv=\"refresh\"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http-equiv='refresh'", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate '{relativePath}' walking up from {AppContext.BaseDirectory}.");
    }
}
