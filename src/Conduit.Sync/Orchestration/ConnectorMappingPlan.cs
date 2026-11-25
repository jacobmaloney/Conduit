using Conduit.Core.SyncModels;
using Conduit.Mapping;
using Conduit.Sync.Connectors;

namespace Conduit.Sync.Orchestration;

/// <summary>
/// Adapts saved Conduit definitions to the shared field operation once per run.
/// Connector envelope/structural fields remain host policy; no database or I/O.
/// </summary>
public sealed class ConnectorMappingPlan
{
    private readonly FieldProjectionRule[] rules;
    // Rows written as a pattern read several fields of the record, so they are evaluated against the whole
    // record, after the single-field rows.
    private readonly (string Target, string Pattern, bool AlwaysWrite)[] patterns;

    public ConnectorMappingPlan(IReadOnlyList<AttributeMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        patterns = mappings.Where(m => MappingRowExpression.IsPattern(m.TransformExpr))
            .Select(m => (m.SinkAttribute, MappingRowExpression.PatternOf(m.TransformExpr)!, m.IsRequired)).ToArray();
        rules = mappings.Where(m => !MappingRowExpression.IsPattern(m.TransformExpr)).Select(mapping =>
        {
            // Snapshot values: later edits to a draft cannot alter an in-flight plan.
            var expression = mapping.TransformExpr;
            var hasTransform = !string.IsNullOrWhiteSpace(expression);
            return new FieldProjectionRule(mapping.SourceAttribute, mapping.SinkAttribute)
            {
                Transform = hasTransform ? value => AttributeTransformer.Apply(expression!, value) : null,
                Presence = mapping.IsRequired || hasTransform && AttributeTransformer.ProducesValueWhenSourceMissing(expression!)
                    ? OutputPresence.Always
                    : hasTransform ? OutputPresence.NonNullValue : OutputPresence.SourcePresent
            };
        }).ToArray();
    }

    /// <summary>Evaluates the same saved rules without connector structural passthrough.</summary>
    public IReadOnlyList<ProjectedField> ProjectFields(IReadOnlyDictionary<string, object?> source) =>
        rules.Select(rule => FieldProjection.Project(source, rule)).Concat(patterns.Select(p => ProjectPattern(source, p))).ToArray();

    /// <summary>
    /// A pattern row's field. Written when it has a value; written empty when the record carried one of its
    /// fields and the value still came out empty (so the target is cleared, as a blank single field is), or
    /// when the row is marked always write; otherwise left alone.
    /// </summary>
    private static ProjectedField ProjectPattern(IReadOnlyDictionary<string, object?> source, (string Target, string Pattern, bool AlwaysWrite) row)
    {
        var value = MappingRowExpression.RenderPattern(row.Pattern, source, out var anyFieldPresent);
        var shouldWrite = value.Length > 0 || anyFieldPresent || row.AlwaysWrite;
        return new ProjectedField(row.Target, value, shouldWrite, anyFieldPresent, UsedDefault: false);
    }

    public ConnectorObject Apply(ConnectorObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (rules.Length == 0 && patterns.Length == 0) return source; // Existing no-mapping passthrough contract.

        var target = new ConnectorObject { SourceId = source.SourceId, ObjectClass = source.ObjectClass };
        foreach (var projected in ProjectFields(source.Attributes))
        {
            if (projected.ShouldWrite) target.Attributes[projected.TargetAttribute] = projected.Value;
        }

        // Keep the same key spelling/comparer behavior and never replace an explicit
        // mapping (including an explicit null). These are connector structural fields.
        foreach (var name in new[] { "userName", "UserName", "sAMAccountName" })
        {
            var value = SourceAttributes.Read(source.Attributes, name);
            if (!target.Attributes.ContainsKey(name) && value.Found) target.Attributes[name] = value.Value;
        }

        // IC membership/manager resolution uses DN; a display-only mapping must not
        // lose the source DN. Preserve original distinguishedName-before-DN priority.
        if (!target.Attributes.ContainsKey("DN"))
        {
            var dn = SourceAttributes.Read(source.Attributes, "distinguishedName").Value
                ?? SourceAttributes.Read(source.Attributes, "DN").Value;
            if (dn is not null) target.Attributes["DN"] = dn;
        }
        return target;
    }
}
