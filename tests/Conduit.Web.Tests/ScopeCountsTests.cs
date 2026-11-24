using System.Runtime.CompilerServices;
using Conduit.Sync.Templates;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// The Auto-Generate screens say why their numbers are what they are. Jacob, 2026-11-24, looking at
/// Core 7, Infrastructure 20, Full 24: "isn't core plus infrastructure 27 and why is full only 24?";
/// and a review that promised 7 workflows was followed by "13 per-class workflow(s)".
/// </summary>
public class ScopeCountsTests
{
    [Fact]
    public void The_widest_scope_says_how_many_classes_the_others_share()
    {
        var note = ScopeCounts.SharedNote(new[] { "Core", "Infrastructure" }, new[] { 7, 20 }, 24);
        Assert.Equal("Core and Infrastructure together. 3 classes are in both, so the total is 24.", note);
    }

    [Fact]
    public void Counts_that_add_up_need_no_note()
    {
        Assert.Null(ScopeCounts.SharedNote(new[] { "Core", "Security" }, new[] { 2, 5 }, 7));
    }

    [Fact]
    public void One_shared_class_reads_in_the_singular()
    {
        Assert.Contains("1 class is in both", ScopeCounts.SharedNote(new[] { "Core", "Extra" }, new[] { 2, 3 }, 4));
    }

    [Fact]
    public void The_result_counts_workflows_and_steps_apart()
    {
        var text = ScopeCounts.GeneratedSummary(7, 13, "Core", "Domain.local", "Identity Center");
        Assert.Equal("Created 1 sync project with 7 workflows (13 steps), one workflow per object class (Core), from Domain.local to Identity Center.", text);
        Assert.DoesNotContain("(", ScopeCounts.GeneratedSummary(2, 2, "Core", "A", "B").Replace("(Core)", string.Empty));
    }

    [Fact]
    public void The_page_uses_both_sentences()
    {
        var page = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Pages", "Sync", "SyncProjects.razor")));
        Assert.Contains("ScopeCounts.SharedNote(", page);
        Assert.Contains("ScopeCounts.GeneratedSummary(totalWorkflows, totalSteps", page);
        Assert.DoesNotContain("Auto-generated 1 sync project with {totalSteps}", page);
    }

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));
}
