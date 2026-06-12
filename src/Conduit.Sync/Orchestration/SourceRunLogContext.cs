using System;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit.Sync.Orchestration;

/// <summary>
/// Ambient per-run log appender for SOURCE connectors. The orchestrator stamps
/// it at the start of a run (same AsyncLocal pattern as CredentialNameContext /
/// IdentityCenterTableContext) so a source that produces per-record findings —
/// e.g. SQL Discovery's per-server Success / AuthFailed / Unreachable / Timeout
/// outcomes — can append them to the run's SyncRunLogs without the connector
/// contract growing a runId parameter. Outside a run (TestConnection, wizard
/// probes) the appender is unset and <see cref="LogAsync"/> is a no-op.
/// </summary>
public static class SourceRunLogContext
{
    private static readonly AsyncLocal<Func<string, string, Task>?> _append = new();

    /// <summary>(level, message) appender bound to the current run. Null outside a run.</summary>
    public static Func<string, string, Task>? Append
    {
        get => _append.Value;
        set => _append.Value = value;
    }

    /// <summary>Append one line to the current run's log, or no-op when not in a run.</summary>
    public static Task LogAsync(string level, string message)
    {
        var appender = _append.Value;
        return appender is null ? Task.CompletedTask : appender(level, message);
    }
}
