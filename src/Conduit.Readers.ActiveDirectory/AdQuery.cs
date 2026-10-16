using System.DirectoryServices.Protocols;

namespace Conduit.Readers.ActiveDirectory;

public sealed record AdQuery
{
    public IReadOnlyList<string> IncludedBases { get; init; } = [];
    public IReadOnlyList<string> ExcludedBases { get; init; } = [];
    public string? DefaultBase { get; init; }
    public string Filter { get; init; } = "(objectClass=*)";
    public SearchScope Depth { get; init; } = SearchScope.Subtree;
    public IReadOnlyList<string> Attributes { get; init; } = [];
    public int PageSize { get; init; } = 500;
    public int? MaximumRows { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public static string FilterForClass(string objectClass) => objectClass.ToLowerInvariant() switch
    {
        "user" => "(&(objectClass=user)(objectCategory=person))",
        "group" => "(objectCategory=group)",
        "computer" => "(objectCategory=computer)",
        _ => $"(objectClass={Escape(objectClass)})"
    };
    public static string Escape(string value) => value.Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");

    public static bool Within(string dn, string parent)
    {
        dn = dn.Trim(); parent = parent.Trim();
        if (dn.Equals(parent, StringComparison.OrdinalIgnoreCase)) return true;
        if (!dn.EndsWith("," + parent, StringComparison.OrdinalIgnoreCase)) return false;
        var comma = dn.Length - parent.Length - 1;
        var escapes = 0;
        for (var i = comma - 1; i >= 0 && dn[i] == '\\'; i--) escapes++;
        return escapes % 2 == 0;
    }
}

public sealed record AdRecord(string DistinguishedName, IReadOnlyDictionary<string, object?> Attributes);
/// <summary>
/// One page of a paged search. <paramref name="HasReferrals"/> is true only for a referral the read cannot
/// account for (a naming context outside the searched base); a referral to a SUBORDINATE naming context
/// (DomainDnsZones, ForestDnsZones, Configuration, a child domain) is listed in
/// <paramref name="SubordinatePartitions"/> instead, because every Active Directory domain root returns those
/// and they hold no objects of the searched partition.
/// </summary>
public sealed record AdPage(IReadOnlyList<AdRecord> Records, byte[]? Cookie, bool HasReferrals = false,
    IReadOnlyList<string>? SubordinatePartitions = null);
public sealed record AdPageRequest(string BaseDn, AdQuery Query, byte[] Cookie);
public interface IAdPageSource
{
    Task<string?> DefaultNamingContextAsync(TimeSpan timeout, CancellationToken ct);
    Task<AdPage> ReadPageAsync(AdPageRequest request, CancellationToken ct);
    Task<AdRecord> ReadEntryAsync(string dn, IReadOnlyList<string> attributes, TimeSpan timeout, CancellationToken ct) =>
        throw new AdReadException("IncompleteAttribute", "The directory returned a ranged attribute that could not be read completely.");
}
public sealed class AdReadState
{
    private readonly List<string> _subordinatePartitions = [];
    public bool IsComplete { get; internal set; }
    public bool IsTruncated { get; internal set; }
    public int Emitted { get; internal set; }
    /// <summary>
    /// Subordinate naming contexts the directory referred to and this read did NOT enter, distinct, in the
    /// order first seen. A complete read of the searched partition may still list these; the host names them
    /// in its completion evidence so nobody mistakes "complete" for "every partition".
    /// </summary>
    public IReadOnlyList<string> SubordinatePartitions => _subordinatePartitions;
    internal void AddSubordinatePartition(string dn)
    {
        if (!_subordinatePartitions.Contains(dn, StringComparer.OrdinalIgnoreCase)) _subordinatePartitions.Add(dn);
    }
}
public sealed class AdReadException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
