using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Conduit.Core.Models;
using Conduit.Core.Services;
using Conduit.Core.SyncModels;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Orchestration;
using Microsoft.Extensions.Logging;

namespace Conduit.Web.Controllers
{
    /// <summary>
    /// F-2.8e — Sync Runs JSON surface for governance-layer consumers
    /// (CenturyCity Census, Marshal Run-Now, future Operations Center deep-link).
    /// Four endpoints:
    ///   GET  /api/v1/sync-projects                              — list projects + last-run summary
    ///   GET  /api/v1/sync-projects/{projectId}/runs?limit=&amp;since= — recent runs for one project
    ///   GET  /api/v1/sync-runs/{runId}                          — single run + step-level log breakdown
    ///   POST /api/v1/sync-projects/{projectId}/runs             — fire a manual run (F-2 Marshal Run-Now)
    ///
    /// Auth: same Bearer <c>scim_*</c> token + <see cref="Middleware.ApiTokenAuthMiddleware"/>
    /// the rest of the API uses. When the token is Tenant-scoped, results are
    /// filtered to projects where that tenant participates as source OR sink.
    /// Admin-scope tokens see everything. The POST endpoint goes through the
    /// same <see cref="AuthorizeForProject"/> tenant guard the GETs use.
    /// </summary>
    [ApiController]
    [Route("api/v1")]
    [Authorize]
    [EnableRateLimiting("scim")]
    public class ApiV1SyncRunsController : ControllerBase
    {
        private readonly SyncProjectRepository _projects;
        private readonly SyncRunRepository _runs;
        private readonly TenantRepository _tenants;
        private readonly ITenantContext _tenantContext;
        private readonly SyncProjectOrchestrator _orchestrator;
        private readonly ILogger<ApiV1SyncRunsController> _logger;

        public ApiV1SyncRunsController(
            SyncProjectRepository projects,
            SyncRunRepository runs,
            TenantRepository tenants,
            ITenantContext tenantContext,
            SyncProjectOrchestrator orchestrator,
            ILogger<ApiV1SyncRunsController> logger)
        {
            _projects = projects;
            _runs = runs;
            _tenants = tenants;
            _tenantContext = tenantContext;
            _orchestrator = orchestrator;
            _logger = logger;
        }

        // ─── Wire shapes (kept stable for the CenturyCity client) ────────────────

        public sealed class SyncProjectSummaryDto
        {
            public Guid Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public string? Description { get; set; }
            public Guid SourceTenantId { get; set; }
            public string? SourceTenantName { get; set; }
            public string? SourceSystemType { get; set; }
            public Guid SinkTenantId { get; set; }
            public string? SinkTenantName { get; set; }
            public string? SinkSystemType { get; set; }
            public string ObjectClass { get; set; } = string.Empty;
            public string? Schedule { get; set; }
            public bool IsEnabled { get; set; }
            public bool IsRunning { get; set; }
            public DateTime? LastRunAt { get; set; }
            public string? LastRunStatus { get; set; }
            public Guid? LastRunId { get; set; }
            public int TotalRuns { get; set; }
            public int SuccessfulRuns { get; set; }
            public int FailedRuns { get; set; }
        }

        public class SyncRunSummaryDto
        {
            public Guid Id { get; set; }
            public Guid ProjectId { get; set; }
            public string? ProjectName { get; set; }
            public string Status { get; set; } = string.Empty;
            public string TriggeredBy { get; set; } = string.Empty;
            public DateTime StartedAt { get; set; }
            public DateTime? CompletedAt { get; set; }
            public long? DurationMs { get; set; }
            public string? Source { get; set; }
            public string? Sink { get; set; }
            public int RecordsProcessed { get; set; }
            public int RecordsCreated { get; set; }
            public int RecordsUpdated { get; set; }
            public int RecordsSkipped { get; set; }
            public int RecordsFailed { get; set; }
            public string? ErrorMessage { get; set; }
            public bool IsIncremental { get; set; }
        }

        public sealed class SyncRunDetailDto : SyncRunSummaryDto
        {
            public Guid SourceTenantId { get; set; }
            public string? SourceSystemType { get; set; }
            public Guid SinkTenantId { get; set; }
            public string? SinkSystemType { get; set; }
            public string ObjectClass { get; set; } = string.Empty;
            public string? Cursor { get; set; }
            public IReadOnlyList<SyncRunLogDto> Logs { get; set; } = Array.Empty<SyncRunLogDto>();
            public IReadOnlyList<SyncRunStepDto> Steps { get; set; } = Array.Empty<SyncRunStepDto>();
        }

        public sealed class SyncRunLogDto
        {
            public long Id { get; set; }
            public string Level { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Phase 7+ workflow tree is recorded inline in run logs — there's no
        /// separate per-step table. We surface a derived "step breakdown" by
        /// grouping logs whose message starts with <c>Step: </c> (the orchestrator's
        /// canonical step boundary marker). Sinks that don't emit step markers
        /// just return an empty list — the run still shows full counters + logs.
        /// </summary>
        public sealed class SyncRunStepDto
        {
            public string Name { get; set; } = string.Empty;
            public string? StepType { get; set; }
            public DateTime? StartedAt { get; set; }
            public DateTime? CompletedAt { get; set; }
            public int LogCount { get; set; }
            public int ErrorCount { get; set; }
            public int WarningCount { get; set; }
        }

        // ─── GET /api/v1/sync-projects ────────────────────────────────────────────

        [HttpGet("sync-projects")]
        public async Task<IActionResult> ListProjects(CancellationToken ct)
        {
            var projects = await _projects.GetAllAsync().ConfigureAwait(false);
            var tenants = (await _tenants.GetAllAsync(includeInactive: true).ConfigureAwait(false))
                .ToDictionary(t => t.Id);

            var filtered = FilterByTokenScope(projects);

            var dtos = filtered.Select(p => new SyncProjectSummaryDto
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                SourceTenantId = p.SourceTenantId,
                SourceTenantName = tenants.TryGetValue(p.SourceTenantId, out var s) ? s.Name : null,
                SourceSystemType = tenants.TryGetValue(p.SourceTenantId, out var s2) ? s2.SystemType : null,
                SinkTenantId = p.SinkTenantId,
                SinkTenantName = tenants.TryGetValue(p.SinkTenantId, out var k) ? k.Name : null,
                SinkSystemType = tenants.TryGetValue(p.SinkTenantId, out var k2) ? k2.SystemType : null,
                ObjectClass = p.ObjectClass,
                Schedule = p.CronSchedule,
                IsEnabled = p.IsEnabled,
                IsRunning = p.IsRunning,
                LastRunAt = p.LastRunAt,
                LastRunStatus = p.LastRunStatus,
                LastRunId = p.LastRunId,
                TotalRuns = p.TotalRuns,
                SuccessfulRuns = p.SuccessfulRuns,
                FailedRuns = p.FailedRuns,
            }).ToList();

            return Ok(dtos);
        }

        // ─── GET /api/v1/sync-projects/{projectId}/runs ──────────────────────────

        [HttpGet("sync-projects/{projectId:guid}/runs")]
        public async Task<IActionResult> ListRunsByProject(
            Guid projectId,
            [FromQuery] int limit = 100,
            [FromQuery] DateTime? since = null,
            [FromQuery] string? status = null,
            CancellationToken ct = default)
        {
            if (limit <= 0 || limit > 1000) limit = 100;

            var project = await _projects.GetByIdAsync(projectId).ConfigureAwait(false);
            if (project is null)
                return NotFound(new { error = $"SyncProject {projectId} not found." });

            var scopeError = AuthorizeForProject(project);
            if (scopeError is not null) return scopeError;

            // GetRecentAsync handles project + status filtering server-side. We
            // apply `since` and the project-name decoration in-process — the
            // run table is small enough per project that this is fine.
            var rows = await _runs.GetRecentAsync(limit, projectId, status).ConfigureAwait(false);
            if (since is { } sinceUtc)
            {
                rows = rows.Where(r => r.StartedAt > sinceUtc).ToList();
            }

            var tenants = await LoadTenantsForProjectsAsync(new[] { project }).ConfigureAwait(false);
            var dtos = rows.Select(r => ProjectRun(r, project, tenants)).ToList();
            return Ok(dtos);
        }

        // ─── GET /api/v1/sync-runs/{runId} ────────────────────────────────────────

        [HttpGet("sync-runs/{runId:guid}")]
        public async Task<IActionResult> GetRunDetail(Guid runId, CancellationToken ct)
        {
            var run = await _runs.GetByIdAsync(runId).ConfigureAwait(false);
            if (run is null)
                return NotFound(new { error = $"SyncRun {runId} not found." });

            var project = await _projects.GetByIdAsync(run.SyncProjectId).ConfigureAwait(false);
            if (project is null)
                return NotFound(new { error = $"SyncProject {run.SyncProjectId} for run not found." });

            var scopeError = AuthorizeForProject(project);
            if (scopeError is not null) return scopeError;

            var tenants = await LoadTenantsForProjectsAsync(new[] { project }).ConfigureAwait(false);
            var logs = await _runs.GetLogsAsync(runId, take: 2000).ConfigureAwait(false);

            var srcName = tenants.TryGetValue(project.SourceTenantId, out var s) ? s.Name : null;
            var sinkName = tenants.TryGetValue(project.SinkTenantId, out var k) ? k.Name : null;

            var detail = new SyncRunDetailDto
            {
                Id = run.Id,
                ProjectId = run.SyncProjectId,
                ProjectName = project.Name,
                Status = run.Status,
                TriggeredBy = run.TriggeredBy,
                StartedAt = run.StartedAt,
                CompletedAt = run.CompletedAt,
                DurationMs = run.DurationMs,
                Source = srcName,
                Sink = sinkName,
                RecordsProcessed = run.ObjectsRead,
                RecordsCreated = run.ObjectsCreated,
                RecordsUpdated = run.ObjectsUpdated,
                RecordsSkipped = run.ObjectsSkipped,
                RecordsFailed = run.ObjectsFailed,
                ErrorMessage = run.ErrorMessage,
                IsIncremental = run.IsIncremental,
                SourceTenantId = project.SourceTenantId,
                SourceSystemType = tenants.TryGetValue(project.SourceTenantId, out var s2) ? s2.SystemType : null,
                SinkTenantId = project.SinkTenantId,
                SinkSystemType = tenants.TryGetValue(project.SinkTenantId, out var k2) ? k2.SystemType : null,
                ObjectClass = project.ObjectClass,
                Cursor = run.Cursor,
                Logs = logs.Select(l => new SyncRunLogDto
                {
                    Id = l.Id,
                    Level = l.Level,
                    Message = l.Message,
                    Timestamp = l.Timestamp,
                }).ToList(),
                Steps = DeriveSteps(logs),
            };

            return Ok(detail);
        }

        // ─── POST /api/v1/sync-projects/{projectId}/runs ──────────────────────────

        /// <summary>
        /// Body for <see cref="StartRun"/>. Empty object is allowed; <c>reason</c>
        /// is plumbed into the TriggeredBy stamp so run history shows why a
        /// Marshal operator (or other governance-layer caller) fired the run.
        /// </summary>
        public sealed class StartRunRequest
        {
            public string? Reason { get; set; }
        }

        [HttpPost("sync-projects/{projectId:guid}/runs")]
        public async Task<IActionResult> StartRun(
            Guid projectId,
            [FromBody] StartRunRequest? body,
            CancellationToken ct)
        {
            var project = await _projects.GetByIdAsync(projectId).ConfigureAwait(false);
            if (project is null)
                return NotFound(new { error = $"SyncProject {projectId} not found." });

            var scopeError = AuthorizeForProject(project);
            if (scopeError is not null) return scopeError;

            var triggeredBy = SanitizeTriggeredBy(User.Identity?.Name, body?.Reason);

            // Atomic IsRunning 0 → 1 claim BEFORE firing the orchestrator
            // (Worf HIGH-1). Two concurrent POSTs cannot both win this swap;
            // the loser gets 409. The orchestrator's own SetRunningAsync call
            // becomes a no-op for the winner (the SQL guard already matched).
            var claimed = await _projects.SetRunningAsync(projectId, Guid.Empty).ConfigureAwait(false);
            if (!claimed)
                return Conflict(new { error = "Project run already in progress" });

            // Fire-and-forget. Errors inside ExecuteAsync are caught and stamped
            // onto the SyncRun row by the orchestrator itself; we log at the
            // controller boundary in case the task scheduler itself rejects.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _orchestrator.ExecuteAsync(projectId, triggeredBy, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Manual run for project {ProjectId} (triggered by {TriggeredBy}) threw at the controller boundary",
                        projectId, triggeredBy);

                    // Defense in depth (Worf HIGH-1): the orchestrator already
                    // releases IsRunning on its own failure paths, but the
                    // controller owns the pre-claim, so guarantee the flag is
                    // freed here too even if that guard ever regresses. A
                    // best-effort clear that itself fails is just logged — we
                    // never want the cleanup to mask the original error.
                    try
                    {
                        await _projects.ClearRunningAsync(projectId).ConfigureAwait(false);
                    }
                    catch (Exception clearEx)
                    {
                        _logger.LogError(clearEx,
                            "Failed to release IsRunning for project {ProjectId} after a failed manual run",
                            projectId);
                    }
                }
            });

            return Accepted(new { projectId = projectId });
        }

        /// <summary>
        /// Builds the <c>TriggeredBy</c> stamp for a manual run. Strips CRLF,
        /// tabs, and all C0 control chars (0x00–0x1F) from both fields, caps
        /// caller at 40 chars and reason at 50 chars so the full
        /// <c>Manual:{caller}:{reason}</c> string fits well under the 100-char
        /// SyncRuns.TriggeredBy column limit. Worf HIGH-2.
        /// </summary>
        internal static string SanitizeTriggeredBy(string? caller, string? reason)
        {
            var safeCaller = StripControl(caller);
            if (safeCaller.Length > 40) safeCaller = safeCaller.Substring(0, 40);
            if (string.IsNullOrEmpty(safeCaller)) safeCaller = "api";

            var safeReason = StripControl(reason);
            if (safeReason.Length > 50) safeReason = safeReason.Substring(0, 50);

            return string.IsNullOrEmpty(safeReason)
                ? $"Manual:{safeCaller}"
                : $"Manual:{safeCaller}:{safeReason}";
        }

        private static string StripControl(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var sb = new System.Text.StringBuilder(input.Length);
            foreach (var c in input)
            {
                if (c <= '') continue; // C0 controls incl. CR, LF, tab
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        // ─── Helpers ──────────────────────────────────────────────────────────────

        private SyncRunSummaryDto ProjectRun(SyncRun r, SyncProject p, IDictionary<Guid, Tenant> tenants) => new()
        {
            Id = r.Id,
            ProjectId = r.SyncProjectId,
            ProjectName = p.Name,
            Status = r.Status,
            TriggeredBy = r.TriggeredBy,
            StartedAt = r.StartedAt,
            CompletedAt = r.CompletedAt,
            DurationMs = r.DurationMs,
            Source = tenants.TryGetValue(p.SourceTenantId, out var s) ? s.Name : null,
            Sink = tenants.TryGetValue(p.SinkTenantId, out var k) ? k.Name : null,
            RecordsProcessed = r.ObjectsRead,
            RecordsCreated = r.ObjectsCreated,
            RecordsUpdated = r.ObjectsUpdated,
            RecordsSkipped = r.ObjectsSkipped,
            RecordsFailed = r.ObjectsFailed,
            ErrorMessage = r.ErrorMessage,
            IsIncremental = r.IsIncremental,
        };

        private async Task<Dictionary<Guid, Tenant>> LoadTenantsForProjectsAsync(IEnumerable<SyncProject> projects)
        {
            // Cheap: load all tenants once and index. Conduit installs typically
            // have tens, not thousands.
            var all = await _tenants.GetAllAsync(includeInactive: true).ConfigureAwait(false);
            return all.ToDictionary(t => t.Id);
        }

        /// <summary>
        /// Tenant-scope guard for list endpoints. Returns the visible subset.
        /// Admin tokens see everything.
        /// </summary>
        private List<SyncProject> FilterByTokenScope(IEnumerable<SyncProject> projects)
        {
            if (_tenantContext.IsAdmin) return projects.ToList();
            var tid = _tenantContext.TenantId;
            if (tid is null) return new List<SyncProject>();
            return projects.Where(p => p.SourceTenantId == tid.Value || p.SinkTenantId == tid.Value).ToList();
        }

        /// <summary>
        /// Tenant-scope guard for project-or-run endpoints. Returns 403 if the
        /// token is Tenant-scoped but the project doesn't involve that tenant.
        /// </summary>
        private IActionResult? AuthorizeForProject(SyncProject project)
        {
            if (_tenantContext.IsAdmin) return null;
            var tid = _tenantContext.TenantId;
            if (tid is null)
                return Unauthorized(new { error = "Token has no tenant scope." });
            if (project.SourceTenantId != tid.Value && project.SinkTenantId != tid.Value)
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = "Token is not scoped to this SyncProject."
                });
            return null;
        }

        /// <summary>
        /// Derive a per-step breakdown from the run log stream. The orchestrator
        /// writes a <c>"Step: &lt;name&gt; [&lt;StepType&gt;]"</c> marker at the
        /// start of each workflow step (Phase 7+); we treat each marker as a
        /// boundary and aggregate the logs between it and the next marker (or
        /// the end of the stream). Pre-Phase-7 runs simply have no markers and
        /// return an empty list — the caller still gets the full log + counters.
        /// </summary>
        private static List<SyncRunStepDto> DeriveSteps(IEnumerable<SyncRunLog> logs)
        {
            var steps = new List<SyncRunStepDto>();
            SyncRunStepDto? current = null;

            foreach (var log in logs)
            {
                if (IsStepMarker(log.Message, out var name, out var stepType))
                {
                    if (current is not null)
                    {
                        current.CompletedAt = log.Timestamp;
                        steps.Add(current);
                    }
                    current = new SyncRunStepDto
                    {
                        Name = name,
                        StepType = stepType,
                        StartedAt = log.Timestamp,
                    };
                    continue;
                }

                if (current is null) continue;
                current.LogCount++;
                if (string.Equals(log.Level, "Error", StringComparison.OrdinalIgnoreCase))
                    current.ErrorCount++;
                else if (string.Equals(log.Level, "Warning", StringComparison.OrdinalIgnoreCase))
                    current.WarningCount++;
            }

            if (current is not null)
                steps.Add(current);

            return steps;
        }

        /// <summary>
        /// Matches orchestrator-emitted "Step: <name> [<StepType>]" markers.
        /// Tolerant of either bracketed or trailing-dash forms and of plain
        /// "Step: <name>" without the type suffix.
        /// </summary>
        private static bool IsStepMarker(string message, out string name, out string? stepType)
        {
            name = string.Empty;
            stepType = null;
            if (string.IsNullOrWhiteSpace(message)) return false;
            const string prefix = "Step: ";
            if (!message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

            var rest = message.Substring(prefix.Length).Trim();
            // Try "<name> [<type>]" form.
            var open = rest.LastIndexOf('[');
            var close = rest.LastIndexOf(']');
            if (open > 0 && close > open && close == rest.Length - 1)
            {
                name = rest.Substring(0, open).Trim();
                stepType = rest.Substring(open + 1, close - open - 1).Trim();
                return true;
            }
            name = rest;
            return true;
        }
    }
}
