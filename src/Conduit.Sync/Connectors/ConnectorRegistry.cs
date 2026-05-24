using System;
using System.Collections.Generic;
using System.Linq;

namespace Conduit.Sync.Connectors;

/// <summary>
/// Single registry keyed by SystemType ("ActiveDirectory", "Emulator", ...).
/// Wired in Program.cs via DI: every IConnectorAdapter registered is auto-
/// collected here. The orchestrator looks up adapters per-side.
/// </summary>
public sealed class ConnectorRegistry
{
    private readonly Dictionary<string, IConnectorAdapter> _adapters;

    public ConnectorRegistry(IEnumerable<IConnectorAdapter> adapters)
    {
        _adapters = adapters.ToDictionary(a => a.SystemType, StringComparer.OrdinalIgnoreCase);
    }

    public IConnectorAdapter? Get(string systemType) =>
        _adapters.TryGetValue(systemType, out var a) ? a : null;

    public IConnectorAdapter Require(string systemType) =>
        Get(systemType) ?? throw new InvalidOperationException(
            $"No connector adapter registered for system type '{systemType}'. Registered: {string.Join(", ", _adapters.Keys)}.");

    public IReadOnlyCollection<IConnectorAdapter> All => _adapters.Values;
}
