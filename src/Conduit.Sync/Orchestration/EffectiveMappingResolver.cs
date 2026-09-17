using Conduit.Core.SyncModels;
using Conduit.Sync.Templates;

namespace Conduit.Sync.Orchestration;

public enum EffectiveMappingOrigin { Step, Project, Template }

public sealed record EffectiveMappingSet(IReadOnlyList<AttributeMapping> Mappings, EffectiveMappingOrigin Origin,
    IReadOnlyList<AttributeMapResolver.DroppedAttribute>? DroppedAttributes = null);

/// <summary>The existing no-step-rows inheritance convention, shared by execution and preview.</summary>
public static class EffectiveMappingResolver
{
    public static EffectiveMappingSet Resolve(IReadOnlyList<AttributeMapping> stepMappings,
        IReadOnlyList<AttributeMapping> projectMappings, string sourceSystemType, string sinkSystemType, string objectClass)
    {
        if (stepMappings.Count > 0) return new(Snapshot(stepMappings), EffectiveMappingOrigin.Step);
        if (projectMappings.Count > 0) return new(Snapshot(projectMappings), EffectiveMappingOrigin.Project);
        var template = AttributeMapResolver.Resolve(sourceSystemType, sinkSystemType, objectClass, out var dropped);
        return new(template.Select((m, order) => new AttributeMapping
        {
            SourceAttribute = m.SourceAttribute, SinkAttribute = m.SinkAttribute,
            IsRequired = m.IsRequired, SortOrder = order
        }).ToArray(), EffectiveMappingOrigin.Template, dropped);
    }

    private static IReadOnlyList<AttributeMapping> Snapshot(IEnumerable<AttributeMapping> mappings) =>
        mappings.OrderBy(m => m.SortOrder).ThenBy(m => m.SinkAttribute, StringComparer.Ordinal).Select(m => new AttributeMapping
        {
            Id = m.Id, SyncProjectId = m.SyncProjectId, WorkflowStepId = m.WorkflowStepId,
            SourceAttribute = m.SourceAttribute, SinkAttribute = m.SinkAttribute,
            TransformExpr = m.TransformExpr, IsRequired = m.IsRequired, SortOrder = m.SortOrder
        }).ToArray();
}
