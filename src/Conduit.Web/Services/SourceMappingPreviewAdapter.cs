using Conduit.Core.SyncModels;
using Conduit.Mapping;
using Conduit.SourceBrowsing;
using Conduit.Sync.Orchestration;

namespace Conduit.Web.Services;

public static class SourceMappingPreviewAdapter
{
    public static SourceMappingContext Context(IEnumerable<AttributeMapping> saved) => SourceMappingPreview.WithSaved(
        new("Conduit field mappings", "Expressions: upper, lower, trim, prefix:text, suffix:text, default:text, const:text, substring:start:length, replace:old:new. Chain with |. Unknown stages pass through in legacy previews but cannot be saved here. Always include target controls output presence, even with a missing source. Supported target fields and sampled values are checked when saving; scripts and full import validation remain separate.", true, false, true, [], []),
        saved.OrderBy(m => m.SortOrder).Select(m => new SourceMappingRule(m.SourceAttribute, m.SinkAttribute, m.TransformExpr ?? "", null, m.IsRequired, m.Id)));

    public static void Validate(IReadOnlyList<SourceMappingRule>? rules)
    {
        if (rules == null) return;
        SourceMappingPreview.Snapshot(rules);
        if (rules.Any(r => r.DefaultValue != null))
            throw new SourceBrowseException("UnsupportedMapping", "Use a default:text expression for Conduit mappings.");
    }

    public static IReadOnlyList<SourceMappedField> Project(IReadOnlyDictionary<string, object?> source, IReadOnlyList<SourceMappingRule> rules, SourceMappingSchema? schema = null)
    {
        Validate(rules);
        return SourceMappingPreview.Render(rules,
            rule => SourceAttributes.Read(source, rule.Source),
            rule => new ConnectorMappingPlan([new AttributeMapping { SourceAttribute = rule.Source, SinkAttribute = rule.Target, TransformExpr = rule.Transformation, IsRequired = rule.Required }]).ProjectFields(source)[0], schema);
    }
}
