using System.Text.Json;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Orchestration;

namespace Conduit.Web.Services;

/// <summary>
/// Runs a SQL Discovery Sync Project "now", using the same IsRunning compare-and-swap
/// + pre-claimed orchestrator contract the manual Run-Now API endpoint uses. Extracted
/// so BOTH callers share one code path: the IC agent-command poller (on a "RunSqlDiscovery"
/// command) and the SPN watcher (immediately after it detects a newly-registered instance).
/// Owns no scope of its own — creates one per run so it is safe to resolve as a singleton.
/// </summary>
public sealed class SqlDiscoveryRunner
{
    private const string SqlDiscoverySystemType = "SqlDiscovery";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SqlDiscoveryRunner> _logger;

    public SqlDiscoveryRunner(IServiceScopeFactory scopeFactory, ILogger<SqlDiscoveryRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Runs the first enabled SQL Discovery project, or the one named in <paramref name="projectName"/>.
    /// Returns (false, reason) rather than throwing so callers can report cleanly.
    /// </summary>
    public async Task<(bool Success, string Message)> RunAsync(string? projectName, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var projectRepo = scope.ServiceProvider.GetRequiredService<SyncProjectRepository>();
        var tenantRepo = scope.ServiceProvider.GetRequiredService<TenantRepository>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<SyncProjectOrchestrator>();

        var tenants = (await tenantRepo.GetAllAsync(includeInactive: true)).ToDictionary(t => t.Id);
        var candidates = (await projectRepo.GetAllAsync())
            .Where(p => p.IsEnabled
                && tenants.TryGetValue(p.SourceTenantId, out var src)
                && string.Equals(src.SystemType, SqlDiscoverySystemType, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var project = projectName is null
            ? candidates.FirstOrDefault()
            : candidates.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));

        if (project is null)
        {
            return (false, projectName is null
                ? "No enabled Sync Project with a SQL Discovery source exists."
                : $"No enabled SQL Discovery project named '{projectName}' exists.");
        }

        var claimed = await projectRepo.SetRunningAsync(project.Id, Guid.Empty);
        if (!claimed)
            return (false, $"Project '{project.Name}' already has a run in progress.");

        try
        {
            // CancellationToken.None: a host shutdown mid-run is handled by the
            // orchestrator's own cancellation registry; the outcome is best-effort then.
            var runId = await orchestrator.ExecuteAsync(project.Id, "Agent:RunSqlDiscovery", CancellationToken.None, preClaimed: true);
            var run = await scope.ServiceProvider.GetRequiredService<SyncRunRepository>().GetByIdAsync(runId);
            var status = run?.Status ?? "Unknown";
            var ok = status is "Succeeded" or "PartialSuccess";
            return (ok, $"Project '{project.Name}' run {runId}: {status}" +
                        (run is null ? string.Empty : $" (read={run.ObjectsRead}, created={run.ObjectsCreated}, updated={run.ObjectsUpdated}, failed={run.ObjectsFailed})") +
                        (string.IsNullOrEmpty(run?.ErrorMessage) ? string.Empty : $" — {run!.ErrorMessage}"));
        }
        catch (Exception ex)
        {
            try { await projectRepo.ClearRunningAsync(project.Id); }
            catch { /* orchestrator releases on its own failure paths; this is defense in depth */ }
            _logger.LogWarning(ex, "SQL Discovery run threw for project {Project}", project.Name);
            return (false, $"Project '{project.Name}' run threw: {ex.Message}");
        }
    }
}
