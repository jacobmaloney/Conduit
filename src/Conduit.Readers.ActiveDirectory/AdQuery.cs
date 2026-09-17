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
public sealed record AdPage(IReadOnlyList<AdRecord> Records, byte[]? Cookie, bool HasReferrals = false);
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
    public bool IsComplete { get; internal set; }
    public bool IsTruncated { get; internal set; }
    public int Emitted { get; internal set; }
}
public sealed class AdReadException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
