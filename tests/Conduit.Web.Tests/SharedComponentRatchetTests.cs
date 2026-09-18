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
    };

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

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));

    private static string BrandCssDir([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "..",
            "IdentityCenter.Brand", "src", "IdentityCenter.Brand", "wwwroot", "css", "components"));
}
