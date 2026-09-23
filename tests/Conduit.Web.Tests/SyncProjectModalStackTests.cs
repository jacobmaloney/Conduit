using Xunit;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Conduit.Web.Tests;

/// <summary>
/// Pins the layering and honesty of the modals that open from INSIDE the Edit Sync Project modal.
///
/// <para>2026-09-17: Bootstrap gives every <c>.modal</c> z-index 1055 and every
/// <c>.modal-backdrop</c> 1050, and relies on its JavaScript to raise those when modals stack.
/// Blazor-rendered markup never runs that JavaScript, so the per-step Attribute Mappings modal and
/// its backdrop landed on exactly the same two planes as the Edit modal underneath. The child's
/// backdrop at 1050 sits BELOW the parent at 1055, so it could not dim it, and the parent kept
/// painting at full brightness behind the child.</para>
/// </summary>
public class SyncProjectModalStackTests
{
    /// <summary>The four modals that render on top of the Edit Sync Project modal, and the level
    /// each one sits at. The scope-discard confirmation opens from inside the scope modal, so it is
    /// a second level, not a first.</summary>
    private static readonly (string Guard, string Level)[] Nested =
    {
        ("mappingModalStepId is { } mmStepId",  "icb-modal-stacked"),
        ("settingsModalStepId is { } smStepId", "icb-modal-stacked"),
        ("scopeModalStepId is { } scStepId",    "icb-modal-stacked"),
        ("showScopeDiscardConfirm",             "icb-modal-stacked-2"),
    };

    [Fact]
    public void Every_modal_opened_from_inside_the_edit_modal_declares_its_stack_level()
    {
        var page = ReadPage();

        foreach (var (guard, level) in Nested)
        {
            var start = page.IndexOf(guard, StringComparison.Ordinal);
            Assert.True(start >= 0, $"Render guard not found: {guard}");

            // The opener is the first modal element after the guard.
            var opener = page.IndexOf("class=\"modal show", start, StringComparison.Ordinal);
            Assert.True(opener > start, $"No modal opener after guard {guard}");
            var openerLine = page[opener..page.IndexOf('>', opener)];
            Assert.Contains(level, openerLine);

            // Its backdrop carries the SAME level, or the backdrop sinks below its own modal.
            var backdrop = page.IndexOf("modal-backdrop fade show", opener, StringComparison.Ordinal);
            Assert.True(backdrop > opener, $"No backdrop after the modal for {guard}");
            var backdropLine = page[backdrop..page.IndexOf('>', backdrop)];
            Assert.Contains(level, backdropLine);
        }
    }

    [Fact]
    public void The_stacking_rule_lives_in_brand_not_in_a_conduit_stylesheet()
    {
        // All CSS lives in IdentityCenter.Brand. A local override here would drift the moment the
        // other product hit the same stacked-modal problem.
        var css = File.ReadAllText(BrandStylesheet());
        Assert.Contains(".modal.icb-modal-stacked", css);
        Assert.Contains(".modal-backdrop.icb-modal-stacked", css);
        Assert.Contains(".modal.icb-modal-stacked-2", css);
        Assert.Contains(".modal-backdrop.icb-modal-stacked-2", css);

        // And the product actually loads it, or the classes above are inert.
        var layout = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Pages", "_Layout.cshtml")));
        // One stylesheet since 2026-09-18, so the link to pin is identitycenter.css - the rule
        // itself is asserted above, which is what stops it being inert.
        Assert.Contains("_content/IdentityCenter.Brand/css/identitycenter.css", layout);
    }

    [Fact]
    public void The_inherit_toggle_binds_two_way_rather_than_setting_checked()
    {
        var page = ReadPage();

        // checked="@x" + @onchange lets Blazor diff the input as unchanged and leave the pixel
        // showing the OLD state. @bind:get/@bind:set is this project's recorded fix.
        Assert.Contains("@bind:get=\"mmInherit\"", page);
        Assert.Contains("SetStepMappingsInherit(mmStepId, v)", page);
        Assert.DoesNotContain("checked=\"@mmInherit\"", page);
        Assert.DoesNotContain("OnStepMappingsInheritChanged", page);
    }

    [Fact]
    public void The_inherit_banner_tells_the_user_something_true_in_both_states()
    {
        var page = ReadPage();

        // The old copy was written for the inheriting case and rendered in both, so a step that
        // already owned its map was still told to "turn the toggle off" to get one.
        Assert.Contains("This step is running the rules from the project", page);
        Assert.Contains("This step has its own attribute map for the", page);

        // Turning inherit back on does NOT discard the rows (SetStepMappingsInherit only sets the
        // flag), so the copy says they are kept. If that handler ever starts deleting, this fails.
        Assert.Contains("kept either way", page);
        var handler = Slice(page, "private void SetStepMappingsInherit", "private void AddStep(");
        Assert.DoesNotContain("Remove", handler);
        Assert.DoesNotContain("Clear", handler);
    }

    private static string ReadPage() => File.ReadAllText(RepoFile(Path.Combine(
        "src", "Conduit.Web", "Pages", "Sync", "SyncProjects.razor")));

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Start marker not found: {start}");
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"End marker not found after start: {end}");
        return source[from..to];
    }

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));

    // There is no css/components directory any more. The 2026-09-18 consolidation collapsed
    // every Brand stylesheet into ONE file, so the rule this test pins now lives in
    // identitycenter.css. The assertions below are unchanged - the rule still has to exist and
    // still has to be in Brand rather than a local Conduit sheet.
    private static string BrandStylesheet([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "..",
            "IdentityCenter.Brand", "src", "IdentityCenter.Brand", "wwwroot", "css", "identitycenter.css"));
}
