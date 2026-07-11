using System.Diagnostics;
using System.Text.Json;

namespace Conduit.Web.Services;

/// <summary>
/// Post-attempt visibility for startup enrollment, so a failed unattended install
/// is diagnosable without console access:
///   (a) a machine-readable status file (enroll-status.json in the Conduit data
///       dir) overwritten after EVERY startup enrollment attempt, and
///   (b) a Windows Application event-log entry (source "Conduit") on enrollment
///       success and failure.
/// NEVER records the enroll code or any key material — outcome, timestamp, error
/// category, and the human-readable state line only. Both sinks are best-effort
/// and fully swallowed: visibility must not break EnrollmentService's
/// "never crashes the host" contract (non-admin runs can't register an event
/// source; non-Windows has no event log at all).
/// </summary>
public static class EnrollmentStatusReporter
{
    public const string OutcomeSuccess = "Success";
    public const string OutcomeFailed = "Failed";
    public const string OutcomeSkippedUnconfigured = "Skipped-unconfigured";
    public const string OutcomeSkippedAlreadyEnrolled = "Skipped-already-enrolled";

    private const string EventSource = "Conduit";

    public static string StatusFilePath => ConduitDataPaths.FilePath("enroll-status.json");

    /// <summary>
    /// <paramref name="statusFilePath"/> and <paramref name="writeEventLog"/>
    /// exist for tests (redirect the status file, keep the machine event log
    /// clean); production callers use the defaults.
    /// </summary>
    public static void Report(string outcome, string? errorCategory, string? detail, ILogger logger,
        string? statusFilePath = null, bool writeEventLog = true)
    {
        try
        {
            var path = statusFilePath ?? StatusFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, BuildStatusJson(outcome, DateTime.UtcNow, errorCategory, detail));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not write enroll-status.json; continuing.");
        }

        if (writeEventLog && outcome is OutcomeSuccess or OutcomeFailed)
            WriteEventLogEntry(outcome == OutcomeSuccess, detail, logger);
    }

    /// <summary>Pure JSON shape builder, split out for unit tests.</summary>
    public static string BuildStatusJson(string outcome, DateTime timestampUtc, string? errorCategory, string? detail) =>
        JsonSerializer.Serialize(new
        {
            Outcome = outcome,
            TimestampUtc = timestampUtc.ToString("O"),
            ErrorCategory = errorCategory,
            Detail = detail
        }, new JsonSerializerOptions { WriteIndented = true });

    private static void WriteEventLogEntry(bool success, string? detail, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            var message = success
                ? $"Conduit enrollment against IdentityCenter succeeded. {detail}"
                : $"Conduit enrollment against IdentityCenter failed. {detail}";
            EventLog.WriteEntry(EventSource, message,
                success ? EventLogEntryType.Information : EventLogEntryType.Error);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not write the enrollment event-log entry (registering source '{Source}' usually needs admin); continuing.", EventSource);
        }
    }
}
