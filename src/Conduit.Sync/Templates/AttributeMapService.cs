using System;
using System.Collections.Generic;
using System.Linq;
using Conduit.Core.SyncModels;

namespace Conduit.Sync.Templates;

/// <summary>
/// Phase 2. Turns the resolver output into <see cref="AttributeMapping"/> rows
/// ready to persist. The caller sets SyncProjectId / WorkflowStepId.
/// </summary>
public interface IAttributeMapService
{
    /// <summary>
    /// Builds suggested mappings for a source -> sink pair and object class.
    /// SyncProjectId / WorkflowStepId are left for the caller to stamp.
    /// </summary>
    List<AttributeMapping> BuildMappings(string sourceSystemType, string sinkSystemType, string objectClass);
}

public sealed class AttributeMapService : IAttributeMapService
{
    public List<AttributeMapping> BuildMappings(string sourceSystemType, string sinkSystemType, string objectClass)
    {
        var resolved = AttributeMapResolver.Resolve(sourceSystemType, sinkSystemType, objectClass);
        var order = 0;
        return resolved
            .Select(r => new AttributeMapping
            {
                Id = Guid.NewGuid(),
                SourceAttribute = r.SourceAttribute,
                SinkAttribute = r.SinkAttribute,
                IsRequired = r.IsRequired,
                SortOrder = order++
            })
            .ToList();
    }
}
