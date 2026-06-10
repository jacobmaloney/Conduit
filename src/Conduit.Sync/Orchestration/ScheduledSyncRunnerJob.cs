using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
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
///
/// Scope ownership (Fix 5): the scheduler tick runs inside a per-tick scope that
/// ScheduledJobScopedWrapper DISPOSES as soon as ExecuteAsync returns — but the
/// sync runs it fires are fire-and-forget and outlive the tick. Each fired run
/// therefore creates, owns, and disposes its OWN DI scope and resolves a fresh
/// orchestrator from it; it never uses the tick's scoped services.
///
/// Run ownership (Fix 3): the tick pre-claims the project's IsRunning flag via
/// the atomic CAS BEFORE firing; a lost CAS means another run is in flight and
/// the tick skips. The fired run gets preClaimed: true so the orchestrator
/// honors (and releases) the claim instead of re-claiming.
/// </summary>
public sealed class ScheduledSyncRunnerJob : IScheduledJob
{
    public string JobName => "ScheduledSyncRunner";
    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    private readonly SyncProjectRepository _projectRepo;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScheduledSyncRunnerJob> _logger;

    public ScheduledSyncRunnerJob(
        SyncProjectRepository projectRepo,
        IServiceScopeFactory scopeFactory,
        ILogger<ScheduledSyncRunnerJob> logger)
    {
        _projectRepo = projectRepo;
        _scopeFactory = scopeFactory;
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

            // Pre-claim the single-run flag (atomic 0→1 CAS). Guid.Empty is the
            // placeholder run id — the orchestrator stamps the real one once the
            // run row exists. A lost CAS = another run started between our read
            // and now; skip this tick.
            bool claimed;
            try
            {
                claimed = await _projectRepo.SetRunningAsync(project.Id, Guid.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pre-claim scheduled sync project {ProjectId}", project.Id);
                continue;
            }
            if (!claimed)
            {
                _logger.LogInformation(
                    "Scheduled sync project {ProjectName} ({ProjectId}) skipped: a run is already in progress.",
                    project.Name, project.Id);
                continue;
            }

            _logger.LogInformation("Firing scheduled sync project {ProjectName} ({ProjectId})", project.Name, project.Id);

            var projectId = project.Id;
            var projectName = project.Name;
            _ = Task.Run(async () =>
            {
                // Own scope for the whole run — the tick scope (and its repos /
                // orchestrator) is disposed by ScheduledJobScopedWrapper right
                // after the tick returns, long before a real sync finishes.
                using var scope = _scopeFactory.CreateScope();
                try
                {
                    var orchestrator = scope.ServiceProvider.GetRequiredService<SyncProjectOrchestrator>();
                    await orchestrator.ExecuteAsync(projectId, "Scheduled", CancellationToken.None, preClaimed: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Scheduled sync project {ProjectId} ({ProjectName}) threw at the scheduler boundary", projectId, projectName);

                    // Defense in depth: the orchestrator releases the flag on its
                    // own failure paths (it owns the pre-claim we handed it), but
                    // if it threw before/around that, free the project so the next
                    // tick isn't blocked forever.
                    try
                    {
                        await scope.ServiceProvider.GetRequiredService<SyncProjectRepository>()
                            .ClearRunningAsync(projectId);
                    }
                    catch (Exception clearEx)
                    {
                        _logger.LogError(clearEx, "Failed to release IsRunning for scheduled project {ProjectId} after a failed run", projectId);
                    }
                }
            });
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
