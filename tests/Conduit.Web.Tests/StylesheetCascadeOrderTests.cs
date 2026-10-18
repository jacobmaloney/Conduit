using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// CONTRAST-02 (Jacob, 2026-10-17 01:30, Conduit Configuration page in the dark theme: "can't see
/// that either"). The card headings and paragraphs rendered #212529 on the dark card: Bootstrap's
/// own <c>body { color: #212529 }</c> was linked AFTER the shared Brand stylesheet, and at equal
/// specificity the later rule wins, so everything that inherits its colour from <c>body</c> took
/// Bootstrap's light-theme ink. Proven in the browser: with Bootstrap linked after the Brand sheet
/// the card title computed rgb(33,37,41); moved before it, rgb(228,237,251) (14.6:1 on the card).
/// IC's _Layout has always linked Bootstrap first. The shared sheet must be the last stylesheet of
/// the vendor set in every product that links it.
/// </summary>
public class StylesheetCascadeOrderTests
{
    [Fact]
    public void The_shared_brand_stylesheet_is_linked_after_bootstrap()
    {
        var layout = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Pages", "_Layout.cshtml")));

        var bootstrap = layout.IndexOf("css/bootstrap/bootstrap.min.css", StringComparison.Ordinal);
        var brand = layout.IndexOf("_content/IdentityCenter.Brand/css/identitycenter.css", StringComparison.Ordinal);

        Assert.True(bootstrap >= 0, "Bootstrap is no longer linked from _Layout.cshtml; the grid classes every page uses would be inert.");
        Assert.True(brand >= 0, "The shared Brand stylesheet is no longer linked from _Layout.cshtml.");
        Assert.True(bootstrap < brand,
            "Bootstrap is linked after the shared Brand stylesheet: its body { color: #212529 } then beats the Brand body colour and dark-theme card text renders dark-on-dark (CONTRAST-02).");
    }

    [Fact]
    public void The_shared_brand_stylesheet_link_is_cache_busted()
    {
        var layout = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Pages", "_Layout.cshtml")));

        // The tag helper is declared in this very file (@addTagHelper), so asp-append-version
        // renders ?v=<hash>; a Brand change must never hide behind a cached sheet.
        Assert.Contains("@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers", layout);
        var brandLink = layout[layout.IndexOf("_content/IdentityCenter.Brand/css/identitycenter.css", StringComparison.Ordinal)..];
        brandLink = brandLink[..brandLink.IndexOf('>')];
        Assert.Contains("asp-append-version=\"true\"", brandLink);
    }

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));
}
