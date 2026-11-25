using Conduit.Core.Models;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using IdentityCenter.Brand.Models;

namespace Conduit.Web.Services;

/// <summary>
/// A few real records from a source, for the mapping editor: each field with a sample value, the distinct
/// values of a short list, and a record to preview a row against. It reads through the source's own connector
/// with the step's scope, stops after a handful of records and a short time, and never writes anything. A
/// source that cannot be read gives no samples; the editor then works as it did, without them.
/// </summary>
public sealed class SourceSampleReader(ConnectorRegistry connectors, ILogger<SourceSampleReader> logger)
{
    /// <summary>How many records are read for samples.</summary>
    public const int SampleCount = 25;

    /// <summary>A field with more distinct values than this is not a list to translate from; none are offered.</summary>
    public const int MaximumDistinctValues = 25;

    /// <summary>The longest a sample read may take before the editor goes on without it.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> ReadAsync(
        Tenant source, string? objectClass, SyncProjectScope? scope, CancellationToken ct)
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Limit);
            var reader = connectors.Get(source.SystemType)?.CreateSource(source.Id);
            if (reader is null) return rows;
            await foreach (var record in reader.ReadAsync(string.IsNullOrWhiteSpace(objectClass) ? "user" : objectClass!, SampleScope(scope), timeout.Token))
            {
                rows.Add(ToRow(record));
                if (rows.Count >= SampleCount) break;
            }
        }
        catch (Exception ex)
        {
            // Samples are a convenience: a source that is slow, unreachable or refuses the read costs the editor nothing.
            logger.LogInformation("Reading sample records for the mapping editor failed ({ErrorType})", ex.GetType().Name);
        }
        return rows;
    }

    /// <summary>The step's scope narrowed to a handful of records, asking for every attribute so unmapped fields are offered too.</summary>
    public static SyncProjectScope SampleScope(SyncProjectScope? scope) => new()
    {
        BaseDN = scope?.BaseDN,
        IncludedBaseDNs = scope?.IncludedBaseDNs,
        ExcludedBaseDNs = scope?.ExcludedBaseDNs,
        LdapFilter = scope?.LdapFilter,
        QueryExpression = scope?.QueryExpression,
        ContainerOnly = scope?.ContainerOnly ?? false,
        PageSize = SampleCount,
        MaxObjects = SampleCount,
        RequestedAttributes = null,
        SuppressTombstones = true,
    };

    /// <summary>A record as field name to text. Several values are shown as the first one; a field starting with _ is the connector's own.</summary>
    public static IReadOnlyDictionary<string, string?> ToRow(ConnectorObject record)
    {
        var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in record.Attributes)
        {
            if (name.StartsWith('_')) continue;
            row[name] = value switch
            {
                null => null,
                string text => text,
                byte[] => null,   // a binary value has nothing to read
                System.Collections.IEnumerable many => many.Cast<object?>().Select(v => v?.ToString()).FirstOrDefault(v => !string.IsNullOrEmpty(v)),
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            };
        }
        return row;
    }

    /// <summary>
    /// The fields the builder offers: the given names first, then any others the records carry, each with a sample
    /// value and, when there are few, its distinct values.
    /// </summary>
    public static IReadOnlyList<BrandValueField> Fields(IEnumerable<string> names, IReadOnlyList<IReadOnlyDictionary<string, string?>> rows)
    {
        var ordered = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())
            .Concat(rows.SelectMany(r => r.Keys).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return ordered.Select(name =>
        {
            var values = rows.Select(r => r.TryGetValue(name, out var v) ? v?.Trim() : null)
                .Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList();
            var distinct = values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
            // A list is only a list when values repeat: 25 records with 25 different names are not one.
            var isList = distinct.Count is > 0 and <= MaximumDistinctValues && (distinct.Count < values.Count || values.Count <= 3);
            return new BrandValueField(name, null, values.FirstOrDefault(), isList ? distinct : null);
        }).ToList();
    }
}
