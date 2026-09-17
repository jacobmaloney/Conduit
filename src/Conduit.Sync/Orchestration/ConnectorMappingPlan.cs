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

    public ConnectorMappingPlan(IReadOnlyList<AttributeMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        rules = mappings.Select(mapping =>
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
        rules.Select(rule => FieldProjection.Project(source, rule)).ToArray();

    public ConnectorObject Apply(ConnectorObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (rules.Length == 0) return source; // Existing no-mapping passthrough contract.

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
