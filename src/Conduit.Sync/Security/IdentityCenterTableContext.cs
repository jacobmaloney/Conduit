using System.Threading;

namespace Conduit.Sync.Security;

/// <summary>
/// V22 per-endpoint table selection for the IdentityCenter connector. Ambient,
/// async-scoped carrier of the IC table key (e.g. "Objects" | "Identities") the
/// current run's SOURCE and SINK endpoints target. The orchestrator sets
/// <see cref="Source"/> from <c>SyncProject.SourceTable</c> and <see cref="Sink"/>
/// from <c>SyncProject.SinkTable</c> right before creating the connector source /
/// sink — exactly mirroring <see cref="CredentialNameContext"/>.
///
/// This is the seam that moved the table choice OFF the connection credential and
/// ONTO the project endpoint: one IdentityCenter connection can now be
/// source=Identities AND sink=Objects in a single project (IC/Identities →
/// IC/Objects). The IdentityCenter connector reads the value from here per side
/// instead of from the stored credential blob.
///
/// Lives in Conduit.Sync (NOT the connector) because the connector references
/// Conduit.Sync — the orchestrator can't reference the connector without a circular
/// dependency. The value is a bare string here; the IC connector maps it to its own
/// internal table enum. Other connectors simply never read it.
///
/// AsyncLocal is the right primitive — orchestrator and adapter share one logical
/// async chain (CreateSource → EnumerateAsync), and we don't want to widen the
/// IConnectorSource / IConnectorSink interfaces just to carry one IC-specific value.
/// </summary>
public static class IdentityCenterTableContext
{
    private static readonly AsyncLocal<string?> _source = new();
    private static readonly AsyncLocal<string?> _sink = new();

    public static string? Source
    {
        get => _source.Value;
        set => _source.Value = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string? Sink
    {
        get => _sink.Value;
        set => _sink.Value = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Returns the raw table key for the given side, or null when unset. The
    /// connector decides what null / unknown means (it defaults to its Objects
    /// table for back-compat).
    /// </summary>
    public static string? Resolve(CredentialSide side) =>
        side == CredentialSide.Sink ? _sink.Value : _source.Value;
}
