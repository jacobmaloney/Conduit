using System.Threading;

namespace Conduit.Sync.Security;

/// <summary>
/// Phase 2 multi-credential UX: ambient credential-name override scoped to the
/// current async execution context. The orchestrator sets <see cref="Source"/>
/// before calling <c>adapter.CreateSource</c> and <see cref="Sink"/> before
/// calling <c>adapter.CreateSink</c>. Connector credential readers consult
/// <see cref="Resolve"/> to honor the override, falling back to the per-adapter
/// default name when nothing is set.
///
/// AsyncLocal is the right primitive here — orchestrator and adapter live on
/// the same logical async chain (CreateSource then EnumerateAsync), and we
/// don't want to thread the name through every internal method.
/// </summary>
public static class CredentialNameContext
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
    /// Resolve the effective credential name for a connector cred reader.
    /// Pass the per-adapter default (e.g. "scim", "okta") and the side
    /// (source vs sink). Returns the override when one is set, otherwise the
    /// default. Cred readers should always call through here.
    /// </summary>
    public static string Resolve(string defaultName, CredentialSide side) => side switch
    {
        CredentialSide.Source => string.IsNullOrWhiteSpace(_source.Value) ? defaultName : _source.Value!,
        CredentialSide.Sink => string.IsNullOrWhiteSpace(_sink.Value) ? defaultName : _sink.Value!,
        _ => defaultName
    };

    /// <summary>
    /// Convenience: set both source + sink for the duration of a scope, restore
    /// on dispose. Use in the orchestrator's per-run setup.
    /// </summary>
    public static System.IDisposable Push(string? source, string? sink)
    {
        var prevSource = _source.Value;
        var prevSink = _sink.Value;
        _source.Value = string.IsNullOrWhiteSpace(source) ? null : source;
        _sink.Value = string.IsNullOrWhiteSpace(sink) ? null : sink;
        return new Restore(prevSource, prevSink);
    }

    private sealed class Restore : System.IDisposable
    {
        private readonly string? _source;
        private readonly string? _sink;
        public Restore(string? source, string? sink) { _source = source; _sink = sink; }
        public void Dispose()
        {
            CredentialNameContext._source.Value = _source;
            CredentialNameContext._sink.Value = _sink;
        }
    }
}

public enum CredentialSide { Source, Sink }
