using System;
using System.Collections.Generic;
using System.Linq;

namespace Conduit.Sync.Templates;

/// <summary>
/// Phase 2. Auto-maps a source connector's native attributes to a sink
/// connector's native attributes by bridging through the canonical schema in
/// <see cref="AttributeTemplateCatalog"/>.
///
/// Join model: look up the source template and the sink template for the object
/// class, then INNER-JOIN them on the canonical key. For each canonical
/// attribute present in BOTH templates, emit (source native name -> sink native
/// name). When the sink connector has no template (unknown system), fall back
/// to mapping each source native name straight to its canonical name — a
/// sensible default the operator can edit.
/// </summary>
public static class AttributeMapResolver
{
    public sealed class ResolvedMapping
    {
        public string SourceAttribute { get; init; } = string.Empty;
        public string SinkAttribute { get; init; } = string.Empty;
        public bool IsRequired { get; init; }
    }

    public static List<ResolvedMapping> Resolve(string sourceSystemType, string sinkSystemType, string objectClass)
    {
        var source = AttributeTemplateCatalog.Get(sourceSystemType, objectClass);
        if (source is null || source.Count == 0)
            return new List<ResolvedMapping>();

        // Dedupe the source by canonical (first occurrence wins) so the result
        // is 1:1 — some IC templates map two native names to the same canonical.
        var orderedSource = new List<AttributeTemplateCatalog.Entry>();
        var seenSourceCanonical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in source)
        {
            if (seenSourceCanonical.Add(e.Canonical))
                orderedSource.Add(e);
        }

        var sink = AttributeTemplateCatalog.Get(sinkSystemType, objectClass);

        // Unknown sink: map source native -> canonical as the default name.
        if (sink is null || sink.Count == 0)
        {
            return orderedSource
                .Select(e => new ResolvedMapping
                {
                    SourceAttribute = e.SourceAttribute,
                    SinkAttribute = e.Canonical,
                    IsRequired = e.IsRequired
                })
                .ToList();
        }

        var sinkByCanonical = new Dictionary<string, AttributeTemplateCatalog.Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in sink)
        {
            if (!sinkByCanonical.ContainsKey(e.Canonical))
                sinkByCanonical[e.Canonical] = e;
        }

        var result = new List<ResolvedMapping>();
        foreach (var s in orderedSource)
        {
            if (!sinkByCanonical.TryGetValue(s.Canonical, out var sinkEntry))
                continue;

            result.Add(new ResolvedMapping
            {
                SourceAttribute = s.SourceAttribute,
                SinkAttribute = sinkEntry.SourceAttribute,
                IsRequired = s.IsRequired || sinkEntry.IsRequired
            });
        }
        return result;
    }
}
