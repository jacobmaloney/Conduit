using System;
using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.Emulator;

/// <summary>
/// Emulator adapter — sink-only for Phase 1A. Writes flow into the existing
/// SCIMServer Users/Groups tables under the sink TenantId. Source is irrelevant
/// because the emulator tenants ARE the upstream system for their own data —
/// nothing to pull from.
/// </summary>
public sealed class EmulatorAdapter : IConnectorAdapter
{
    public string SystemType => "Emulator";
    public string DisplayName => "Conduit Emulator";
    public bool SupportsSource => false;
    public bool SupportsSink => true;

    private readonly DatabaseConfig _config;
    private readonly ILoggerFactory _loggerFactory;

    public EmulatorAdapter(DatabaseConfig config, ILoggerFactory loggerFactory)
    {
        _config = config;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) => null;

    public IConnectorSink? CreateSink(Guid tenantId) =>
        new EmulatorSink(tenantId, new EmulatorSinkRepository(_config), _loggerFactory.CreateLogger<EmulatorSink>());
}
