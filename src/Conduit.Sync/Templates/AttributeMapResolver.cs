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

    /// <summary>
    /// A source attribute the INNER JOIN discarded because the sink template
    /// declares no entry for its canonical key. Callers MUST report these — a
    /// silently dropped attribute never reaches the sink and, because the source
    /// read only requests mapped attributes, is never even read.
    /// </summary>
    public sealed class DroppedAttribute
    {
        public string SourceAttribute { get; init; } = string.Empty;
        public string Canonical { get; init; } = string.Empty;
    }

    public static List<ResolvedMapping> Resolve(string sourceSystemType, string sinkSystemType, string objectClass)
        => Resolve(sourceSystemType, sinkSystemType, objectClass, out _);

    public static List<ResolvedMapping> Resolve(
        string sourceSystemType, string sinkSystemType, string objectClass,
        out List<DroppedAttribute> dropped)
    {
        dropped = new List<DroppedAttribute>();

        var source = AttributeTemplateCatalog.Get(sourceSystemType, objectClass);
        if (source is null || source.Count == 0)
            return new List<ResolvedMapping>();

        // Dedupe the source by canonical (first occurrence wins) so the result
        // is 1:1 — some IC templates map two native names to the same canonical.
        var orderedSource = new List<AttributeTemplateCatalog.Entry>();
        var seenSourceCanonical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in source)
        {
            // Sink-only entries declare what this system will ACCEPT, never what it
            // hands out. Skipping them here (not recording them as dropped — they were
            // never source-eligible) keeps a template from becoming an authority on
            // attributes it merely stores.
            if (e.SinkOnly) continue;
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
            {
                dropped.Add(new DroppedAttribute
                {
                    SourceAttribute = s.SourceAttribute,
                    Canonical = s.Canonical
                });
                continue;
            }

            result.Add(new ResolvedMapping
            {
                SourceAttribute = s.SourceAttribute,
                SinkAttribute = sinkEntry.SinkName,
                IsRequired = s.IsRequired || sinkEntry.IsRequired
            });
        }
        return result;
    }
}
