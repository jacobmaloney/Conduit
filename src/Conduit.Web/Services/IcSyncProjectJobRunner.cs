using System.Text.Json;
using Conduit.Core.SyncModels;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Orchestration;

namespace Conduit.Web.Services;

/// <summary>A progress sample taken from the live SyncRun row, in IdentityCenter's counting vocabulary.</summary>
public sealed record IcSyncRunProgress(
    Guid RunId,
    string Status,
    int ItemsProcessed,
    int ItemsSucceeded,
    int ItemsFailed,
    string? Message);

/// <summary>
/// The finished run as IdentityCenter's completion call needs it. <see cref="Success"/> is the
/// RUN's verdict, never "the HTTP call worked": a cancelled or failed run completes unsuccessfully.
/// </summary>
public sealed record IcSyncRunOutcome(
    bool Success,
    Guid? RunId,
    string Status,
    int ItemsProcessed,
    int ItemsSucceeded,
    int ItemsFailed,
    string Message,
    string? ResultJson);

/// <summary>
/// The seam between "an IdentityCenter job was claimed" and "this installation ran a sync".
/// Split in two deliberately: <see cref="ResolveAsync"/> answers what (if anything) this job
/// names here, and <see cref="ExecuteAsync"/> is the ONLY way a run starts — so a refusal is
/// provably a path on which nothing executed.
/// </summary>
public interface IIcSyncProjectJobRunner
{
    /// <summary>The local project bound to an IdentityCenter project, or null when nothing is bound.</summary>
    Task<SyncProject?> ResolveAsync(Guid identityCenterProjectId);

    /// <summary>
    /// Admits and runs the resolved project. <paramref name="onProgress"/> is invoked on a timer
    /// while the run is in flight; cancelling <paramref name="ct"/> cancels the local run.
    /// </summary>
    Task<IcSyncRunOutcome> ExecuteAsync(
        SyncProject project,
        string triggeredBy,
        Func<IcSyncRunProgress, CancellationToken, Task> onProgress,
        CancellationToken ct);
}

/// <summary>
/// Runs an IdentityCenter-pinned sync through the SAME admission CAS and orchestrator contract
/// the manual Run-Now API and <see cref="SqlDiscoveryRunner"/> use: one IsRunning 0→1 swap,
/// the exact owner handed to the orchestrator as <c>claimedRunId</c>, and the persisted SyncRun
/// row as the single source of counts. No second execution path, and no progress mechanism of
/// its own — progress is read from the run row the orchestrator already updates per step.
///
/// Owns no scope — it creates one per call, so it is safe as a singleton beside the hosted service.
/// </summary>
public sealed class IcSyncProjectJobRunner : IIcSyncProjectJobRunner
{
    private static readonly IReadOnlyList<string> SuccessStatuses = new[] { "Succeeded", "PartialSuccess" };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _progressInterval;
    private readonly ILogger<IcSyncProjectJobRunner> _logger;

    public IcSyncProjectJobRunner(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<IcSyncProjectJobRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _progressInterval = TimeSpan.FromSeconds(Math.Clamp(config.GetValue("IcSyncJobs:ProgressIntervalSeconds", 10), 1, 300));
        _logger = logger;
    }

    public async Task<SyncProject?> ResolveAsync(Guid identityCenterProjectId)
    {
        if (identityCenterProjectId == Guid.Empty) return null;
        using var scope = _scopeFactory.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<SyncProjectRepository>();
        return await projects.GetByIdentityCenterProjectIdAsync(identityCenterProjectId);
    }

    public async Task<IcSyncRunOutcome> ExecuteAsync(
        SyncProject project,
        string triggeredBy,
        Func<IcSyncRunProgress, CancellationToken, Task> onProgress,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<SyncProjectRepository>();
        var runs = scope.ServiceProvider.GetRequiredService<SyncRunRepository>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<SyncProjectOrchestrator>();

        // Single-run admission. The disabled/already-running refusals the caller reports by name
        // are decided on the read row; this swap is the authority that settles the race, and a
        // lost swap must never execute connectors.
        var ownerRunId = Guid.NewGuid();
        var admission = await projects.TryAdmitRunAsync(
            project.Id, ownerRunId, requireEnabled: true, requireActiveConnections: true);
        if (admission != SyncAdmissionOutcome.Admitted)
        {
            // AdmissionLost says "another run started first", which is only true of the race. A
            // deactivated Connected System reported that way would be completed back to
            // IdentityCenter with a reason naming a run that never existed.
            var reason = admission == SyncAdmissionOutcome.RefusedAlreadyRunning
                ? IcSyncJobReasons.AdmissionLost(project)
                : SyncAdmissionRefusal.Describe(admission, project.Name);
            return new IcSyncRunOutcome(false, null, "NotAdmitted", 0, 0, 0, reason, null);
        }

        using var progressStop = new CancellationTokenSource();
        var progressPump = PumpProgressAsync(runs, ownerRunId, onProgress, progressStop.Token);

        try
        {
            // The orchestrator links this token into its own SyncCancellationRegistry
            // registration, so an IdentityCenter cancellation trips the SAME token the
            // local Stop Sync button does — there is no second cancellation path.
            await orchestrator.ExecuteAsync(project.Id, triggeredBy, ct, claimedRunId: ownerRunId);
        }
        catch (Exception ex)
        {
            // The orchestrator stamps its own failures; this covers a throw outside that
            // stamping so the project never stays wedged as Running.
            try { await projects.ClearRunningAsync(project.Id, ownerRunId); }
            catch { /* the orchestrator releases on its own failure paths; this is defense in depth */ }
            _logger.LogWarning(ex, "IdentityCenter-pinned run threw for Conduit project {ProjectId}", project.Id);

            var thrown = await ReadRunAsync(runs, ownerRunId);
            return Build(project, ownerRunId, thrown, forcedFailure:
                $"Conduit run {ownerRunId} for project '{project.Name}' threw: {ex.Message}");
        }
        finally
        {
            progressStop.Cancel();
            try { await progressPump; } catch { /* progress reporting never decides an outcome */ }
        }

        var run = await ReadRunAsync(runs, ownerRunId);
        return Build(project, ownerRunId, run, forcedFailure: null);
    }

    private async Task PumpProgressAsync(
        SyncRunRepository runs,
        Guid runId,
        Func<IcSyncRunProgress, CancellationToken, Task> onProgress,
        CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_progressInterval);
        while (await SafeWaitAsync(timer, stop))
        {
            SyncRun? run;
            try { run = await runs.GetByIdAsync(runId); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Progress read failed for run {RunId}; continuing.", runId);
                continue;
            }
            if (run is null) continue;

            try { await onProgress(ToProgress(run), stop); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Progress report failed for run {RunId}; continuing.", runId);
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stop)
    {
        try { return await timer.WaitForNextTickAsync(stop); }
        catch (OperationCanceledException) { return false; }
    }

    private static async Task<SyncRun?> ReadRunAsync(SyncRunRepository runs, Guid runId)
    {
        try { return await runs.GetByIdAsync(runId); }
        catch { return null; }
    }

    internal static IcSyncRunProgress ToProgress(SyncRun run) => new(
        run.Id,
        run.Status,
        run.ObjectsRead,
        run.ObjectsCreated + run.ObjectsUpdated,
        run.ObjectsFailed,
        $"Conduit run {run.Id}: {run.Status} (read={run.ObjectsRead}, created={run.ObjectsCreated}, updated={run.ObjectsUpdated}, skipped={run.ObjectsSkipped}, failed={run.ObjectsFailed})");

    /// <summary>
    /// Maps the persisted run to IdentityCenter's completion shape. Read is what was processed;
    /// created+updated is what succeeded; skipped is reported in the receipt rather than counted
    /// as a success. A missing run row is a failure with a named reason, never a silent zero.
    /// </summary>
    internal static IcSyncRunOutcome Build(SyncProject project, Guid ownerRunId, SyncRun? run, string? forcedFailure)
    {
        if (run is null)
        {
            return new IcSyncRunOutcome(false, ownerRunId, "Unknown", 0, 0, 0,
                forcedFailure ?? $"Conduit run {ownerRunId} for project '{project.Name}' left no run record; its outcome is unknown.",
                BuildResultJson(project, ownerRunId, null, "Unknown"));
        }

        var success = forcedFailure is null && SuccessStatuses.Contains(run.Status, StringComparer.Ordinal);
        var message = forcedFailure
            ?? $"Conduit run {run.Id} for project '{project.Name}': {run.Status} (read={run.ObjectsRead}, created={run.ObjectsCreated}, updated={run.ObjectsUpdated}, skipped={run.ObjectsSkipped}, failed={run.ObjectsFailed})"
               + (string.IsNullOrWhiteSpace(run.ErrorMessage) ? string.Empty : $" — {run.ErrorMessage}");

        return new IcSyncRunOutcome(
            success,
            run.Id,
            run.Status,
            run.ObjectsRead,
            run.ObjectsCreated + run.ObjectsUpdated,
            run.ObjectsFailed,
            message,
            BuildResultJson(project, run.Id, run, run.Status));
    }

    /// <summary>
    /// The completion receipt. camelCase <c>runId</c>/<c>runStatus</c>/<c>resultMessage</c> match
    /// IdentityCenter's <c>SyncJobResult</c> so its console can read the receipt it already knows;
    /// the run id in it is a CONDUIT run id, which is why the Conduit project and installation are
    /// named alongside it rather than left to be guessed.
    /// </summary>
    internal static string BuildResultJson(SyncProject project, Guid runId, SyncRun? run, string status) =>
        JsonSerializer.Serialize(new
        {
            runId,
            runStatus = status,
            resultMessage = run is null
                ? $"Conduit run {runId} left no run record."
                : $"Conduit run {runId}: {status}",
            executor = "Conduit",
            conduitProjectId = project.Id,
            conduitProjectName = project.Name,
            identityCenterProjectId = project.IdentityCenterProjectId,
            objectsRead = run?.ObjectsRead ?? 0,
            objectsCreated = run?.ObjectsCreated ?? 0,
            objectsUpdated = run?.ObjectsUpdated ?? 0,
            objectsSkipped = run?.ObjectsSkipped ?? 0,
            objectsFailed = run?.ObjectsFailed ?? 0,
            durationMs = run?.DurationMs,
            errorMessage = run?.ErrorMessage
        });
}
