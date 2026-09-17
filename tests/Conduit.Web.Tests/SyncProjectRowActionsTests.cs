using Xunit;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Conduit.Web.Tests;

/// <summary>
/// Pins the discoverability of the Sync Projects row actions.
///
/// <para>2026-09-17: the only way to edit a sync project is the "Manage" row action, which opens the
/// Setup/Scope/Mappings modal. The Brand list-row migration left it as an icon-only button carrying a
/// bare <c>fa-ellipsis</c> glyph, so it read as an overflow menu rather than as the edit action, while
/// every sibling (Run Now, Full Sync, History, Schedule) kept a visible text label. Jacob could not
/// find how to edit a project. These tests assert the LABEL, not the handler: the handler was never
/// missing, the affordance was.</para>
/// </summary>
public class SyncProjectRowActionsTests
{
    [Fact]
    public void Manage_action_carries_a_visible_label_and_opens_the_edit_modal()
    {
        var page = ReadPage();

        // The visible label, asserted the same way FullSyncMenuTests asserts "> Full Sync".
        Assert.Contains("> Manage", page);
        Assert.Contains("SelectThenEdit(project)", page);

        // Both enabled and disabled rows offer it, so a disabled project is still editable.
        Assert.Equal(2, Regex.Matches(page, Regex.Escape("SelectThenEdit(project)")).Count);
        Assert.Equal(2, Regex.Matches(page, Regex.Escape("> Manage")).Count);
    }

    [Fact]
    public void No_row_action_is_an_unlabelled_ellipsis()
    {
        var page = ReadPage();

        // A bare ellipsis glyph promises a menu that does not exist here; every action is a real verb.
        Assert.DoesNotContain("fa-ellipsis", page);
    }

    private static string ReadPage() => File.ReadAllText(RepoFile(Path.Combine(
        "src", "Conduit.Web", "Pages", "Sync", "SyncProjects.razor")));

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));
}
