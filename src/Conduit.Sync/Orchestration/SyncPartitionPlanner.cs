using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;

namespace Conduit.Sync.Orchestration;

/// <summary>One included base of a scope and the containers found one level under it.</summary>
public sealed record SyncPartitionBase(string BaseDn, IReadOnlyList<DirectoryContainerNode> Children);

/// <summary>
/// IdentityCenter SPLIT-ENUM-01 (UC037-D06): turns a scope's included bases and the containers one level under each into
/// the containers a split read runs one at a time. For every base, in order: the base itself one level deep (its own
/// objects), then each child container as a subtree. A container at or under an excluded base is left out, a container
/// seen twice (two bases that overlap) is read once, and the first container of the plan is the one that carries the
/// deleted-objects pass (UC037-D09). Pure: the browse results come from the connector's container browser.
/// </summary>
public static class SyncPartitionPlanner
{
    public static IReadOnlyList<SyncRunPartition> Build(IReadOnlyList<SyncPartitionBase> bases, IReadOnlyList<string>? excludedBases = null)
    {
        if (bases is null) throw new ArgumentNullException(nameof(bases));
        var excluded = (excludedBases ?? Array.Empty<string>()).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var drafts = new List<(string Label, string Dn, bool OneLevel)>();
        foreach (var @base in bases)
        {
            if (@base is null || string.IsNullOrWhiteSpace(@base.BaseDn)) continue;
            var baseDn = @base.BaseDn.Trim();
            if (IsExcluded(baseDn, excluded)) continue;
            if (seen.Add(baseDn))
                drafts.Add((baseDn + " (this level)", baseDn, true));
            foreach (var child in @base.Children ?? Array.Empty<DirectoryContainerNode>())
            {
                if (child is null || string.IsNullOrWhiteSpace(child.DistinguishedName)) continue;
                var dn = child.DistinguishedName.Trim();
                if (IsExcluded(dn, excluded) || !seen.Add(dn)) continue;
                drafts.Add((dn, dn, false));
            }
        }
        var count = drafts.Count;
        return drafts.Select((d, i) => new SyncRunPartition(i + 1, count, d.Label, new[] { d.Dn }, d.OneLevel, CarriesTombstones: i == 0)).ToList();
    }

    /// <summary>A DN at or under one of the excluded bases, compared the way the AD reader compares them.</summary>
    private static bool IsExcluded(string dn, IReadOnlyList<string> excluded) =>
        excluded.Any(parent => Within(dn, parent));

    internal static bool Within(string dn, string parent)
    {
        dn = Normalize(dn); parent = Normalize(parent);
        return dn.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || dn.EndsWith("," + parent, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string dn) =>
        string.Join(",", dn.Split(',').Select(part => part.Trim()));
}
