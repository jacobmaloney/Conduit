using System.DirectoryServices.Protocols;
using System.Runtime.CompilerServices;

namespace Conduit.Readers.ActiveDirectory;

/// <summary>Common live-object paging engine. Completion requires every scope's explicit final cookie.</summary>
public static class AdReader
{
    public static async IAsyncEnumerable<AdRecord> ReadAsync(IAdPageSource source, AdQuery query, AdReadState state,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        state.IsComplete = false; state.IsTruncated = false; state.Emitted = 0;
        ct.ThrowIfCancellationRequested();
        if (query.PageSize is < 1 or > 1000 || query.MaximumRows is <= 0 || query.RequestTimeout <= TimeSpan.Zero ||
            !Enum.IsDefined(query.Depth) || string.IsNullOrWhiteSpace(query.Filter) || query.Filter.Length > 65536 ||
            query.IncludedBases.Any(string.IsNullOrWhiteSpace) || query.ExcludedBases.Any(string.IsNullOrWhiteSpace) || query.Attributes.Any(string.IsNullOrWhiteSpace))
            throw new AdReadException("InvalidScope", "The directory read scope, paging limit or requested attributes are invalid.");
        // Freeze caller-owned collections before awaiting a page.
        query = query with { IncludedBases = query.IncludedBases.ToArray(), ExcludedBases = query.ExcludedBases.ToArray(), Attributes = query.Attributes.ToArray() };
        var bases = query.IncludedBases.Select(b => b.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(b => b.Length).ToList();
        if (bases.Count == 0)
        {
            var root = string.IsNullOrWhiteSpace(query.DefaultBase) ? await source.DefaultNamingContextAsync(query.RequestTimeout, ct) : query.DefaultBase;
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(root)) throw new AdReadException("MissingNamingContext", "No included container or default naming context is available. Choose a source container.");
            bases.Add(root.Trim());
        }
        if (query.Depth == SearchScope.Subtree)
        {
            var optimized = new List<string>();
            foreach (var dn in bases) if (!optimized.Any(parent => AdQuery.Within(dn, parent))) optimized.Add(dn);
            bases = optimized;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dn in bases)
        {
            if (query.ExcludedBases.Any(parent => AdQuery.Within(dn, parent))) continue;
            byte[] cookie = [];
            var pages = new HashSet<string>(StringComparer.Ordinal);
            for (var pageNumber = 0; ; pageNumber++)
            {
                ct.ThrowIfCancellationRequested();
                if (pageNumber >= 10000) throw new AdReadException("PagingLimit", "The directory exceeded the paging safety limit. Narrow the source scope.");
                var page = await source.ReadPageAsync(new(dn, query, cookie), ct);
                ct.ThrowIfCancellationRequested();
                if (page.Cookie == null || page.HasReferrals)
                    throw new AdReadException("IncompleteRead", "The directory returned referrals or did not confirm paging completion. No complete read was established.");
                // RFC 2696 cookies are opaque and may stay constant while the result cursor advances.
                // Refuse a repeated cookie AND page population, not a cookie value alone.
                var population = string.Join("\0", page.Records.Select(r => r.DistinguishedName.ToUpperInvariant()).OrderBy(n => n, StringComparer.Ordinal));
                var signature = Convert.ToHexString(page.Cookie) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(population)));
                if (page.Cookie.Length > 0 && !pages.Add(signature))
                    throw new AdReadException("RepeatedPage", "The directory repeated a page without advancing. The read was stopped without marking it complete.");
                foreach (var record in page.Records)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(record.DistinguishedName)) throw new AdReadException("InvalidEntry", "A directory entry has no distinguished name.");
                    if (query.ExcludedBases.Any(parent => AdQuery.Within(record.DistinguishedName, parent)) || !seen.Add(record.DistinguishedName)) continue;
                    if (query.MaximumRows.HasValue && state.Emitted >= query.MaximumRows.Value) { state.IsTruncated = true; yield break; }
                    var complete = await CompleteRangesAsync(source, record, query.RequestTimeout, ct);
                    state.Emitted++;
                    yield return complete;
                }
                if (page.Cookie.Length == 0) break;
                // A known continuation at the cap is enough to report truncation, without an extra LDAP call.
                if (query.MaximumRows.HasValue && state.Emitted >= query.MaximumRows.Value) { state.IsTruncated = true; yield break; }
                cookie = page.Cookie.ToArray();
            }
        }
        ct.ThrowIfCancellationRequested();
        state.IsComplete = true;
    }

    private static async Task<AdRecord> CompleteRangesAsync(IAdPageSource source, AdRecord record, TimeSpan timeout, CancellationToken ct)
    {
        var ranged = record.Attributes.Keys.Where(k => k.Contains(";range=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (ranged.Length == 0) return record;
        var attributes = new Dictionary<string, object?>(record.Attributes, StringComparer.OrdinalIgnoreCase);
        foreach (var key in ranged)
        {
            var name = key[..key.IndexOf(";range=", StringComparison.OrdinalIgnoreCase)];
            var values = new List<object?>();
            var expected = 0;
            var currentKey = key;
            var current = record;
            for (var part = 0; ; part++)
            {
                ct.ThrowIfCancellationRequested();
                var bounds = currentKey[(currentKey.IndexOf(";range=", StringComparison.OrdinalIgnoreCase) + 7)..].Split('-');
                if (part >= 1000 || bounds.Length != 2 || !int.TryParse(bounds[0], out var start) || start != expected)
                    throw new AdReadException("IncompleteAttribute", "The directory returned a missing or inconsistent attribute range.");
                var raw = current.Attributes[currentKey];
                var batch = raw is object?[] array ? array : raw == null ? [] : new[] { raw };
                if (values.Count + batch.Length > 100000) throw new AdReadException("AttributeLimit", "A directory attribute exceeded the value safety limit.");
                values.AddRange(batch);
                if (bounds[1] == "*") break;
                if (!int.TryParse(bounds[1], out var end) || end < start || end == int.MaxValue || batch.Length != end - start + 1)
                    throw new AdReadException("IncompleteAttribute", "The directory returned an incomplete attribute range.");
                expected = end + 1;
                current = await source.ReadEntryAsync(record.DistinguishedName, [$"{name};range={expected}-*"], timeout, ct);
                if (!record.DistinguishedName.Equals(current.DistinguishedName, StringComparison.OrdinalIgnoreCase))
                    throw new AdReadException("IncompleteAttribute", "The directory returned attribute values for a different entry.");
                currentKey = current.Attributes.Keys.SingleOrDefault(k => k.StartsWith(name + ";range=", StringComparison.OrdinalIgnoreCase))
                    ?? throw new AdReadException("IncompleteAttribute", "The directory omitted the requested attribute continuation.");
            }
            attributes.Remove(key); attributes[name] = values.ToArray();
        }
        return record with { Attributes = attributes };
    }
}
