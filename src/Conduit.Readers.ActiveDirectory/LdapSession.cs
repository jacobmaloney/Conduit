using System.DirectoryServices.Protocols;

namespace Conduit.Readers.ActiveDirectory;

/// <summary>Protocol adapter over a host-owned, already bound connection. Never reads credentials or changes bind options.</summary>
public sealed class LdapSession(LdapConnection connection) : IAdPageSource
{
    public async Task<string?> DefaultNamingContextAsync(TimeSpan timeout, CancellationToken ct)
    {
        var response = await SendAsync(new SearchRequest("", "(objectClass=*)", SearchScope.Base, "defaultNamingContext"), timeout, ct);
        if (response.References.Count > 0 || response.Entries.Count != 1) throw new AdReadException("MissingNamingContext", "The directory did not return one default naming context.");
        var values = response.Entries[0].Attributes["defaultNamingContext"];
        return values is { Count: > 0 } ? values[0]?.ToString() : null;
    }
    public async Task<AdPage> ReadPageAsync(AdPageRequest request, CancellationToken ct)
    {
        var search = new SearchRequest(request.BaseDn, request.Query.Filter, request.Query.Depth, request.Query.Attributes.ToArray());
        search.Controls.Add(new PageResultRequestControl(request.Query.PageSize) { Cookie = request.Cookie });
        var response = await SendAsync(search, request.Query.RequestTimeout, ct);
        var records = new List<AdRecord>();
        foreach (SearchResultEntry entry in response.Entries)
        {
            ct.ThrowIfCancellationRequested();
            records.Add(ConvertEntry(entry));
        }
        var control = response.Controls.OfType<PageResultResponseControl>().SingleOrDefault();
        var (subordinate, external) = ClassifyReferences(
            response.References.Cast<SearchResultReference>().SelectMany(r => r.Reference ?? []), request.BaseDn);
        return new(records, control?.Cookie, external, subordinate);
    }

    /// <summary>
    /// Splits continuation references into the two kinds a reader must treat differently: a reference whose
    /// DN lies within <paramref name="baseDn"/> names a SUBORDINATE naming context (every domain root returns
    /// DomainDnsZones and ForestDnsZones, a forest root adds Configuration, a parent domain adds each child);
    /// those hold no objects of the searched partition, so the read lists them and carries on. Any other
    /// reference (another forest, an unparseable URL) is <c>External</c> and the read must refuse completion.
    /// </summary>
    public static (IReadOnlyList<string> Subordinate, bool External) ClassifyReferences(IEnumerable<Uri> references, string baseDn)
    {
        var subordinate = new List<string>();
        var external = false;
        foreach (var uri in references)
        {
            string dn;
            try { dn = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')); }
            catch (InvalidOperationException) { external = true; continue; }
            if (string.IsNullOrWhiteSpace(dn) || !AdQuery.Within(dn, baseDn)) { external = true; continue; }
            if (!subordinate.Contains(dn, StringComparer.OrdinalIgnoreCase)) subordinate.Add(dn);
        }
        return (subordinate, external);
    }
    public async Task<AdRecord> ReadEntryAsync(string dn, IReadOnlyList<string> attributes, TimeSpan timeout, CancellationToken ct)
    {
        var response = await SendAsync(new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, attributes.ToArray()), timeout, ct);
        if (response.References.Count > 0 || response.Entries.Count != 1 || !response.Entries[0].DistinguishedName.Equals(dn, StringComparison.OrdinalIgnoreCase))
            throw new AdReadException("IncompleteAttribute", "The directory did not return the requested entry's remaining attribute values.");
        return ConvertEntry(response.Entries[0]);
    }
    private static AdRecord ConvertEntry(SearchResultEntry entry)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["distinguishedName"] = entry.DistinguishedName };
        foreach (string name in entry.Attributes.AttributeNames)
        {
            var attribute = entry.Attributes[name];
            values[name] = attribute.Count switch { 0 => null, 1 => attribute[0], _ => attribute.Cast<object?>().ToArray() };
        }
        return new(entry.DistinguishedName, values);
    }
    private async Task<SearchResponse> SendAsync(SearchRequest request, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Native requests have their own timeout; cancellation is checked on both sides and never returns stale data.
        var response = await Task.Run(() => (SearchResponse)connection.SendRequest(request, timeout), ct);
        ct.ThrowIfCancellationRequested();
        if (response.ResultCode != ResultCode.Success) throw new AdReadException("IncompleteRead", "The directory did not complete the requested search successfully.");
        return response;
    }
}
