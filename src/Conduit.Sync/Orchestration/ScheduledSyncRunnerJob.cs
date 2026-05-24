using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NCrontab;
using Conduit.DataAccess.Repositories;
using Conduit.Scheduling;

namespace Conduit.Sync.Orchestration;

/// <summary>
/// Background loop: every minute, look at SyncProjects with IsEnabled=1 and a
/// CronSchedule set, fire any whose next-scheduled instant has passed since
/// LastRunAt. Phase 1B uses NCrontab for standard 5-field cron expressions and
/// keeps the legacy Phase 1A shorthand for back-compat:
///
///   "@hourly"            — every 60 minutes after LastRunAt
///   "@every:Nm"          — every N minutes after LastRunAt
///   "*/15 * * * *" etc.  — standard 5-field cron (NCrontab)
///   anything unparseable — ignored (no scheduled fire; manual only)
///
/// Manual / API run-now bypasses this entirely.
/// </summary>
public sealed class ScheduledSyncRunnerJob : IScheduledJob
{
    public string JobName => "ScheduledSyncRunner";
    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    private readonly SyncProjectRepository _projectRepo;
    private readonly SyncProjectOrchestrator _orchestrator;
    private readonly ILogger<ScheduledSyncRunnerJob> _logger;

    public ScheduledSyncRunnerJob(
        SyncProjectRepository projectRepo,
        SyncProjectOrchestrator orchestrator,
        ILogger<ScheduledSyncRunnerJob> logger)
    {
        _projectRepo = projectRepo;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var due = await _projectRepo.GetEnabledScheduledAsync();
        var now = DateTime.UtcNow;

        foreach (var project in due)
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (project.IsRunning) continue;
            if (!IsDue(project.CronSchedule, project.LastRunAt, now)) continue;

            try
            {
                _logger.LogInformation("Firing scheduled sync project {ProjectName} ({ProjectId})", project.Name, project.Id);
                _ = _orchestrator.ExecuteAsync(project.Id, "Scheduled", CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fire scheduled sync project {ProjectId}", project.Id);
            }
        }
    }

    // Compiled-cron cache so we don't reparse on every minute-tick. Bounded by
    // the number of distinct expressions across all projects (small).
    private static readonly ConcurrentDictionary<string, CrontabSchedule?> _cronCache = new();

    public static bool IsDue(string? cron, DateTime? lastRunAt, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(cron)) return false;
        var s = cron.Trim();

        // Legacy shorthand kept for back-compat with Phase 1A projects.
        if (s.Equals("@hourly", StringComparison.OrdinalIgnoreCase))
        {
            return !lastRunAt.HasValue || (now - lastRunAt.Value) >= TimeSpan.FromHours(1);
        }

        if (s.StartsWith("@every:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = s["@every:".Length..];
            if (rest.EndsWith("m", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(rest[..^1], out var minutes) && minutes > 0)
            {
                return !lastRunAt.HasValue || (now - lastRunAt.Value) >= TimeSpan.FromMinutes(minutes);
            }
        }

        // Standard cron via NCrontab. Compute the next occurrence after the last
        // run (or "now minus one minute" if never run) and fire if that instant
        // has passed.
        var schedule = _cronCache.GetOrAdd(s, expr =>
        {
            try { return CrontabSchedule.Parse(expr); }
            catch { return null; }
        });
        if (schedule is null) return false;

        var anchor = lastRunAt ?? now.AddMinutes(-1);
        var next = schedule.GetNextOccurrence(anchor);
        return next <= now;
    }

    /// <summary>
    /// True if the expression parses (NCrontab or legacy shorthand). Used by the
    /// UI to validate the operator's input before save.
    /// </summary>
    public static bool TryValidate(string? cron, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(cron)) return true; // blank = manual
        var s = cron.Trim();
        if (s.Equals("@hourly", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("@every:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = s["@every:".Length..];
            if (rest.EndsWith("m", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(rest[..^1], out var minutes) && minutes > 0) return true;
            error = "Invalid @every shorthand. Use @every:Nm where N is minutes.";
            return false;
        }
        try
        {
            CrontabSchedule.Parse(s);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Invalid cron expression: {ex.Message}";
            return false;
        }
    }
}
