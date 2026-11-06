using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// BRAND-MODAL-01 (Jacob, 2026-11-06: "conduit should be using brand like a mofo too"). Conduit's dialogs are Brand's
/// BrandModal; sixteen hand-built Bootstrap shells were replaced. A page that writes its own modal shell or backdrop again
/// fails here, naming the file, so the next dialog extends the shared one instead.
/// </summary>
public class BrandModalAdoptionTests
{
    private static string WebRoot([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "src", "Conduit.Web"));

    [Fact]
    public void No_Conduit_page_hand_builds_a_modal_shell()
    {
        var shell = new Regex(@"class=""modal(\s|"")|modal-backdrop|class=""modal-dialog");
        var razor = Directory.EnumerateFiles(WebRoot(), "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.True(razor.Count > 20, "the scan must see Conduit's pages");   // an empty scope is not a pass
        var offenders = razor.Where(f => shell.IsMatch(File.ReadAllText(f))).Select(f => Path.GetRelativePath(WebRoot(), f)).ToList();
        Assert.True(offenders.Count == 0, "Use <BrandModal> (IdentityCenter.Brand.Components.Modals): " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_sync_pages_render_their_dialogs_through_BrandModal()
    {
        var sync = Path.Combine(WebRoot(), "Pages", "Sync");
        Assert.Equal(14, Regex.Matches(File.ReadAllText(Path.Combine(sync, "SyncProjects.razor")), @"<BrandModal\b").Count);
        Assert.Equal(2, Regex.Matches(File.ReadAllText(Path.Combine(sync, "ScheduleManager.razor")), @"<BrandModal\b").Count);
    }
}
