using Xunit;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Conduit.Web.Tests;

/// <summary>
/// A ratchet, not a style guide. Conduit and IdentityCenter share one presentation implementation in
/// IdentityCenter.Brand; Conduit's product identity is data, not separate markup or CSS. Pages that
/// have been migrated onto the shared vocabulary must not quietly acquire raw Bootstrap form controls
/// again, because every one that does re-opens the drift the consolidation closed.
///
/// <para>This asserts only what has ALREADY been migrated. ConnectedSystems.razor, the last page to
/// convert, landed in wave 4b and is listed below, so the ratchet now covers every migrated Conduit
/// page. Add the next one here when it lands.</para>
/// </summary>
public class SharedComponentRatchetTests
{
    /// <summary>Bootstrap control classes that have a shared equivalent, so their presence is drift.
    /// btn-close, btn-group and btn-group-sm are NOT listed: they are a glyph and a layout primitive
    /// with no Brand counterpart yet, and pretending otherwise would force a worse local hack.</summary>
    private static readonly string[] Forbidden =
    {
        "form-control", "form-select", "form-label", "form-check", "form-check-input",
        "form-check-label", "form-text", "form-switch",
        "btn-primary", "btn-secondary", "btn-success", "btn-danger", "btn-warning", "btn-link",
        "btn-outline-primary", "btn-outline-secondary", "btn-outline-success",
        "btn-outline-danger", "btn-outline-info", "btn-outline-light",
    };

    public static TheoryData<string> MigratedPages() => new()
    {
        Path.Combine("Pages", "Sync", "SyncProjects.razor"),
        Path.Combine("Pages", "Sync", "ScheduleManager.razor"),
        Path.Combine("Pages", "Sync", "SyncHistory.razor"),
        Path.Combine("Pages", "ConnectedSystems.razor"),
        Path.Combine("Pages", "Configuration.razor"),
        Path.Combine("Pages", "Setup.razor"),
        Path.Combine("Pages", "DatabaseSettings.razor"),
        Path.Combine("Pages", "IdentityProviders.razor"),
        Path.Combine("Pages", "Tokens.razor"),
        Path.Combine("Pages", "PortalAdmins.razor"),
        Path.Combine("Pages", "AdminRecovery.razor"),
        Path.Combine("Pages", "ApiExplorer.razor"),
        Path.Combine("Pages", "AuditLog.razor"),
        Path.Combine("Pages", "Logs.razor"),
        Path.Combine("Pages", "QuickConnect.razor"),
        Path.Combine("Pages", "ProvisioningBaseDns.razor"),
        // The two product adapters left in Shared/Design after CC-RETIRE-01 (2026-10-16).
        Path.Combine("Shared", "Design", "DirectoryBrowserModal.razor"),
        Path.Combine("Shared", "Design", "SourceDataBrowser.razor"),
    };

    /// <summary>
    /// CC-RETIRE-01 (Jacob, 2026-10-16: "same lists, same buttons, same setup wizard"): the local
    /// Cc* design-system copies are gone. Shared/Design may hold only product adapters that bind
    /// Conduit services to a Brand component; a new Cc* file there is a fork of the shared owner.
    /// </summary>
    [Fact]
    public void No_local_design_system_component_remains()
    {
        var design = RepoFile(Path.Combine("src", "Conduit.Web", "Shared", "Design"));
        var local = Directory.EnumerateFiles(design)
            .Select(Path.GetFileName)
            .Where(n => n!.StartsWith("Cc", StringComparison.Ordinal))
            .ToList();
        Assert.True(local.Count == 0, "Local design-system copies found: " + string.Join(", ", local)
            + ". The shared owner is IdentityCenter.Brand (BrandHelpHint, BrandScheduleEditor, BrandScopeBrowserDialog, ...).");

        // And no page still composes one.
        var pages = Directory.EnumerateFiles(RepoFile(Path.Combine("src", "Conduit.Web", "Pages")), "*.razor", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"<Cc[A-Z]\w*|Cc[A-Z]\w+\."))
            .Select(f => Path.GetFileName(f))
            .ToList();
        Assert.True(pages.Count == 0, "Pages still composing a retired Cc* component: " + string.Join(", ", pages));
    }

    /// <summary>The sync pages render the shared summary row and filter tabs they used to declare and never show.</summary>
    [Theory]
    [InlineData("ScheduleManager.razor", "<BrandStatRow Stats=\"@StatItems\" />")]
    [InlineData("ScheduleManager.razor", "<BrandFilterTabs Tabs=\"@_tabs\" ActiveTab=\"@activeTab\" OnTabChanged=\"HandleTabChanged\"")]
    [InlineData("ScheduleManager.razor", "<BrandScheduleEditor @bind-Value=\"editCron\"")]
    [InlineData("SyncProjects.razor", "<BrandStatRow Stats=\"@statRows\" />")]
    [InlineData("SyncProjects.razor", "<BrandScheduleEditor @bind-Value=\"form.CronSchedule\"")]
    [InlineData("SyncProjects.razor", "<DirectoryBrowserModal @ref=\"baseDnBrowser\"")]
    [InlineData("SyncProjects.razor", "<BrandHelpHint Text=")]
    public void A_sync_page_composes_the_shared_component(string page, string fragment)
    {
        var source = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Pages", "Sync", page)));
        Assert.Contains(fragment, source);
    }

    [Theory]
    [MemberData(nameof(MigratedPages))]
    public void A_migrated_page_uses_the_shared_control_vocabulary(string relativePath)
    {
        var source = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", relativePath)));

        foreach (var token in Forbidden)
        {
            // Whole CSS word only, so ic-btn-primary and icb-form-check never trip their own rule.
            var hits = Regex.Matches(source, $@"(?<![\w-]){Regex.Escape(token)}(?![\w-])").Count;
            Assert.True(hits == 0,
                $"{relativePath} uses raw Bootstrap '{token}' {hits} time(s). Use the IdentityCenter.Brand "
                + "equivalent instead; all CSS lives in the Brand project.");
        }
    }

    [Theory]
    [MemberData(nameof(MigratedPages))]
    public void A_migrated_page_carries_no_retired_cx_classes(string relativePath)
    {
        var source = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", relativePath)));

        // cx-* is the local light-first vocabulary the consolidation is retiring. conduit-brand.css
        // can only be deleted once nothing consumes it.
        var hits = Regex.Matches(source, @"\bcx-[a-z0-9-]+").Count;
        Assert.True(hits == 0, $"{relativePath} still uses {hits} retired cx-* class(es).");
    }

    [Fact]
    public void Every_shared_class_this_product_relies_on_is_defined_in_brand()
    {
        // A class that exists only at the caller is not shared, it is a typo waiting to look fine.
        var css = string.Concat(Directory.EnumerateFiles(BrandCssDir(), "*.css").Select(File.ReadAllText));

        foreach (var cls in new[]
        {
            "icb-form-input", "icb-form-input-sm", "icb-form-input-lg",
            "icb-form-select", "icb-form-select-sm", "icb-form-label", "icb-form-help",
            "icb-form-check", "icb-form-check-label", "icb-form-switch", "icb-form-checkbox",
            "ic-btn", "ic-btn-primary", "ic-btn-ghost", "ic-btn-danger", "ic-btn-sm", "ic-btn-lg",
            "icb-btn-success", "icb-btn-accent", "icb-btn-caution",
        })
        {
            Assert.True(css.Contains("." + cls, StringComparison.Ordinal),
                $"Conduit uses .{cls} but IdentityCenter.Brand does not define it.");
        }
    }

    /// <summary>
    /// Jacob's dashboard screenshot, 2026-10-16: primary list-action buttons rendered blue text on a blue
    /// fill (the light-theme anchor rule outranked .icb-list-action-primary), and six stat tiles laid
    /// value / label / sub-line in a ROW until "No schedule" wrapped mid-word. Both fixes live in the one
    /// Brand sheet; this reads it so neither can be tidied away under green.
    /// </summary>
    [Fact]
    public void Light_theme_links_and_stat_tiles_keep_the_dashboard_readable()
    {
        var css = string.Concat(Directory.EnumerateFiles(BrandCssDir(), "*.css").Select(File.ReadAllText));
        Assert.Contains("html.light-theme a:not(.btn):not(.ic-btn):not(.icb-list-action)", css);
        Assert.Contains("html.light-theme a:hover:not(.btn):not(.ic-btn):not(.icb-list-action)", css);
        Assert.Contains(".icb-stat-row .stat-card { display:flex; flex-direction:column;", css);
        Assert.Contains(".icb-stat-row .stat-number { font-size:28px; font-weight:700; white-space:nowrap; }", css);
        Assert.DoesNotContain(".icb-stat-row .icb-stat-prominent .stat-number { font-size:34px; line-height:1.1; letter-spacing:-.02em; overflow-wrap:anywhere; }", css);
    }

    /// <summary>
    /// The sheet's "Error State" component once put display:flex on the bare .alert, so every
    /// Bootstrap alert with more than one inline child rendered as columns (Run Sync Project,
    /// 2026-10-16). The row layout is scoped to .alert.alert-error; the bare rule keeps spacing only.
    /// </summary>
    [Fact]
    public void A_bootstrap_alert_is_not_a_flex_row()
    {
        var css = string.Concat(Directory.EnumerateFiles(BrandCssDir(), "*.css").Select(File.ReadAllText));
        var normalized = css.Replace("\r\n", "\n");
        Assert.Contains(".alert.alert-error {\n    display: flex;", normalized);
        foreach (Match m in Regex.Matches(normalized, @"(?m)^\.alert\s*\{([^}]*)\}"))
            Assert.DoesNotContain("display", m.Groups[1].Value);
    }

    /// <summary>
    /// Jacob, 2026-10-16: "should use the standard shared schedule selector, no more cron statements".
    /// The shared editor is the visual picker only; no mode toggle, no preset list, no cron field, no
    /// cron chip in the preview. The stored value is still cron, which is the schedulers' business.
    /// </summary>
    [Fact]
    public void The_shared_schedule_editor_shows_no_cron()
    {
        var components = Path.GetFullPath(Path.Combine(BrandCssDir(), "..", "..", "Components"));
        var editor = File.ReadAllText(Path.Combine(components, "Inputs", "BrandScheduleEditor.razor"));
        var picker = File.ReadAllText(Path.Combine(components, "Inputs", "BrandSchedulePicker.razor"));
        Assert.DoesNotContain("Advanced (cron)", editor);
        Assert.DoesNotContain("<input", editor);
        Assert.DoesNotContain("<select", editor);
        Assert.Contains("<BrandSchedulePicker Value=\"@Value\" ValueChanged=\"OnPickerChanged\" />", editor);
        Assert.DoesNotContain("<code", picker);
        var css = string.Concat(Directory.EnumerateFiles(BrandCssDir(), "*.css").Select(File.ReadAllText));
        Assert.DoesNotContain(".cc-schedule-editor .btn-group", css);
    }

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));

    private static string BrandCssDir([CallerFilePath] string thisFile = "") =>
        // css/components is gone: the 2026-09-18 consolidation made Brand one stylesheet.
        // Enumerating wwwroot/css non-recursively now yields identitycenter.css alone, which is
        // exactly the set this ratchet means by "defined in brand".
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "..",
            "IdentityCenter.Brand", "src", "IdentityCenter.Brand", "wwwroot", "css"));
}
