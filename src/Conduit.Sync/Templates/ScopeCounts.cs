namespace Conduit.Sync.Templates;

/// <summary>
/// The sentences that keep the numbers on the Auto-Generate screens honest.
///
/// The widest scope is the narrower ones together, and some classes sit in more than one of them, so
/// the cards' counts do not add up: Active Directory shows Core 7, Infrastructure 20, Full 24, because
/// the three structural classes are in both (Jacob, 2026-11-24: "isn't core plus infrastructure 27 and
/// why is full only 24?"). And a class can need more than one step, so a review that promised 7
/// workflows was followed by "13 per-class workflow(s)", which counted steps.
/// </summary>
public static class ScopeCounts
{
    /// <summary>Null when the narrower counts add up to the widest; otherwise one sentence saying why they do not.</summary>
    public static string? SharedNote(IReadOnlyList<string> narrowerNames, IReadOnlyList<int> narrowerCounts, int widestCount)
    {
        var shared = narrowerCounts.Sum() - widestCount;
        if (shared <= 0 || narrowerNames.Count == 0) return null;
        var names = string.Join(" and ", narrowerNames);
        var where = narrowerNames.Count == 2 ? "both" : "more than one";
        return $"{names} together. {shared} {(shared == 1 ? "class is" : "classes are")} in {where}, so the total is {widestCount}.";
    }

    /// <summary>What was generated, with workflows and steps counted apart.</summary>
    public static string GeneratedSummary(int workflows, int steps, string scope, string source, string target)
    {
        var w = workflows == 1 ? "1 workflow" : $"{workflows} workflows";
        var s = steps == workflows ? string.Empty : steps == 1 ? " (1 step)" : $" ({steps} steps)";
        return $"Created 1 sync project with {w}{s}, one workflow per object class ({scope}), from {source} to {target}.";
    }
}
