using System;
using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.Emulator;

/// <summary>
/// Local Directory connector — a first-class connection type whose SOURCE and SINK
/// are Conduit's OWN Users / Groups store (the same tables SCIM and the Emulator
/// use). Unlike the Emulator (sink-only test target), Local Directory is symmetric:
/// it can be a sync SOURCE (read this Conduit instance's local users/groups out to
/// another system) AND a sink (write into them), and it is reachable by the inbound
/// REST/SCIM proxy as a writable + readable target.
///
/// Reuse: the SINK is the proven <see cref="EmulatorSink"/> (writes into local
/// Users/Groups by tenant id via <see cref="EmulatorSinkRepository"/>); the SOURCE
/// reads the same tables by tenant id via <see cref="LocalDirectoryRepository"/>.
/// Nothing here touches DemoSeed / Generation / parity / the emulator demo — those
/// continue to use the "Emulator" SystemType unchanged.
/// </summary>
public sealed class LocalDirectoryAdapter : IConnectorAdapter
{
    public string SystemType => "LocalDirectory";
    public string DisplayName => "Local Directory (this Conduit)";
    public bool SupportsSource => true;
    public bool SupportsSink => true;

    public ConnectorCapabilities Capabilities { get; } = new()
    {
        // Single-record DB upserts; no native multi-record API beyond looping.
        SupportsBulk = false,
        MaxBatchSize = 1,
        // No delta cursor — a local read is always a full enumeration.
        SupportsIncremental = false,
        // Inbound proxy: a Local Directory connection is a writable + readable
        // target. Create/Update/Delete land in the local Users/Groups store;
        // GET read-through enumerates them.
        SupportsCreate = true,
        SupportsUpdate = true,
        SupportsDelete = true,
    };

    private readonly DatabaseConfig _config;
    private readonly ILoggerFactory _loggerFactory;

    public LocalDirectoryAdapter(DatabaseConfig config, ILoggerFactory loggerFactory)
    {
        _config = config;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new LocalDirectorySource(tenantId, new LocalDirectoryRepository(_config),
            _loggerFactory.CreateLogger<LocalDirectorySource>());

    // The sink IS the Emulator sink — the identical, proven write path into the
    // local Users / Groups / GroupMembers / UserEmails tables under this tenant id.
    public IConnectorSink? CreateSink(Guid tenantId) =>
        new EmulatorSink(tenantId, new EmulatorSinkRepository(_config),
            _loggerFactory.CreateLogger<EmulatorSink>());
}
