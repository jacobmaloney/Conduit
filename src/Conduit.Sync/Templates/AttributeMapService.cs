using System;
using System.Collections.Generic;
using System.Linq;
using Conduit.Core.SyncModels;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<AttributeMapService>? _logger;

    public AttributeMapService(ILogger<AttributeMapService>? logger = null) => _logger = logger;

    public List<AttributeMapping> BuildMappings(string sourceSystemType, string sinkSystemType, string objectClass)
    {
        var resolved = AttributeMapResolver.Resolve(sourceSystemType, sinkSystemType, objectClass, out var dropped);

        // Mappings are frozen at generation time, so an attribute discarded here is
        // discarded for the life of the project — and because the source read only
        // requests mapped attributes, it is never even read. Say so at generation,
        // not months later when a compliance rule turns out to have no data.
        if (dropped.Count > 0)
        {
            _logger?.LogWarning(
                "Attribute map {SourceSystem} -> {SinkSystem} ({ObjectClass}): dropped {DroppedCount} source " +
                "attribute(s) with no counterpart in the {SinkSystemTemplate} sink template — {Dropped}. " +
                "These will never reach the sink; add the canonical key(s) to AttributeTemplateCatalog.",
                sourceSystemType, sinkSystemType, objectClass, dropped.Count, sinkSystemType,
                string.Join(", ", dropped.Select(d => d.SourceAttribute + " (canonical " + d.Canonical + ")")));
        }

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
