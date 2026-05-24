using System.Collections.Generic;

namespace Conduit.Sync.Connectors;

/// <summary>
/// Tenant-neutral envelope for a single object read from a source or pushed to
/// a sink. Conduit does NOT persist these — they flow through the orchestrator
/// from source → mapping → sink and are dropped after each loop.
///
/// SourceId is the unique key from the source system (objectGuid, externalId,
/// id, etc). Sinks use it to decide create vs update via their own lookup.
/// </summary>
public sealed class ConnectorObject
{
    public string SourceId { get; init; } = string.Empty;
    public string ObjectClass { get; init; } = "User";
    public Dictionary<string, object?> Attributes { get; init; } = new();
}
