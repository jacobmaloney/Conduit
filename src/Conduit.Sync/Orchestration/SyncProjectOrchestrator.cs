using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Conduit.Core.SyncModels;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;

namespace Conduit.Sync.Orchestration;

/// <summary>
/// Pump. Reads from a source connector, applies the per-step AttributeMapping
/// table, writes to a sink connector. One pass per call; the caller decides
/// scheduling (Quartz job + manual Run-Now).
///
/// This is the SLIM Conduit version of IC's 2700-line SyncProjectOrchestrator —
/// person matching, identity resolution, governance hooks, write-back audit,
/// and step-based routing are deliberately gone. Conduit is a pump, not a
/// governance engine.
///
/// Phase 7 layering:
///   - A project owns 1..N Workflows (executed in Ordinal order).
///   - Each Workflow owns 1..N WorkflowSteps (executed in Ordinal order).
///   - A WorkflowStep has a StepType: Mapping / PersonMatch / PersonCreate /
///     AssignManager / AssignGroupOwner / Custom. The Mapping step runs the
///     classic Phase-2 source→mapping→sink loop using the step's own mappings
///     + optional per-step scope. The four person-aware step types call the
///     matching IConnectorSink method directly (without re-enumerating source).
///   - V17 backfill guarantees every existing project gets a Default workflow
///     with a Default Mapping step, so pre-Phase-7 projects keep working with
///     no edit form changes required.
///
/// Phase 2 additions (still in force inside Mapping steps):
///   - Bulk path: accumulator flushes at Capabilities.MaxBatchSize when the
///     sink advertises SupportsBulk. Falls through to per-record loop otherwise.
///   - Incremental: source.EnumerateAsync is called with the prior run's
///     cursor; the new cursor is persisted at the end of a successful run.
/// </summary>
public sealed class SyncProjectOrchestrator
{
    private readonly SyncProjectRepository _projectRepo;
    private readonly SyncRunRepository _runRepo;
    private readonly TenantRepository _tenantRepo;
    private readonly ConnectorRegistry _connectors;
    private readonly SyncRunAsyncJobRepository _asyncJobRepo;
    private readonly WorkflowRepository _workflowRepo;
    private readonly SinkRecordHashRepository _hashRepo;
    private readonly SinkConnectionCredentialMapRepository _credentialMapRepo;
    private readonly SyncCancellationRegistry _cancellation;
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;
    private readonly ILogger<SyncProjectOrchestrator> _logger;

    public SyncProjectOrchestrator(
        SyncProjectRepository projectRepo,
        SyncRunRepository runRepo,
        TenantRepository tenantRepo,
        ConnectorRegistry connectors,
        SyncRunAsyncJobRepository asyncJobRepo,
        WorkflowRepository workflowRepo,
        SinkRecordHashRepository hashRepo,
        SinkConnectionCredentialMapRepository credentialMapRepo,
        SyncCancellationRegistry cancellation,
        Microsoft.Extensions.Configuration.IConfiguration config,
        ILogger<SyncProjectOrchestrator> logger)
    {
        _projectRepo = projectRepo;
        _runRepo = runRepo;
        _tenantRepo = tenantRepo;
        _connectors = connectors;
        _asyncJobRepo = asyncJobRepo;
        _workflowRepo = workflowRepo;
        _hashRepo = hashRepo;
        _credentialMapRepo = credentialMapRepo;
        _cancellation = cancellation;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Run a project once. Creates a SyncRun row, walks the workflow tree,
    /// updates counters per step, marks the run Succeeded or Failed at the end.
    /// Returns the SyncRun.Id so callers (UI, Quartz) can deep-link to history.
    ///
    /// <paramref name="preClaimed"/>: pass true ONLY when the caller already won
    /// the IsRunning CAS (controller StartRun, UI Run-Now, scheduler) — we then
    /// own the release but never re-claim. When false (default) WE perform the
    /// CAS here; losing it means another run is in flight, so the just-created
    /// SyncRun is stamped "Skipped" and we return WITHOUT executing and WITHOUT
    /// touching the flag (it belongs to the other run).
    /// </summary>
    public async Task<Guid> ExecuteAsync(Guid projectId, string triggeredBy, CancellationToken cancellationToken, bool preClaimed = false)
    {
        // Worf HIGH-1: the IsRunning flag must be released on EVERY exit path,
        // including the early throws that happen before a SyncRun row exists
        // (GetById null, CreateAsync, tenant resolution) — but ONLY when this
        // invocation actually owns the flag (won the CAS or was pre-claimed).
        // A lost CAS means another run owns it; we must never clear theirs.
        SyncRun? run = null;
        bool ownsFlag = preClaimed;

        try
        {
            var project = await _projectRepo.GetByIdAsync(projectId)
                ?? throw new InvalidOperationException($"SyncProject {projectId} not found.");

            run = await _runRepo.CreateAsync(new SyncRun
            {
                SyncProjectId = project.Id,
                Status = "Running",
                TriggeredBy = triggeredBy,
                StartedAt = DateTime.UtcNow
            });

            if (preClaimed)
            {
                // The caller's CAS used a placeholder run id (the run row didn't
                // exist yet). Stamp the real run id so LastRunId deep-links work.
                await _projectRepo.StampLastRunIdAsync(project.Id, run.Id);
            }
            else
            {
                // Single-run ownership CAS. Losing means a run is already in
                // flight for this project (scheduler overlap, double-click, …):
                // record the skip honestly and leave the other run's flag alone.
                var won = await _projectRepo.SetRunningAsync(project.Id, run.Id);
                if (!won)
                {
                    const string skipReason = "A run is already in progress for this project.";
                    await _runRepo.FinishAsync(run.Id, "Skipped", skipReason, 0);
                    await Log(run.Id, "Warning", $"Run skipped: {skipReason}");
                    return run.Id;
                }
                ownsFlag = true;
            }
            await Log(run.Id, "Info", $"Run started by {triggeredBy} for project '{project.Name}'.");

            // ── IC-connection license gate (ENFORCEMENT POINT 3 — run-guard) ──────
            // This is the convergence point for EVERY trigger path (UI Run-Now,
            // scheduler, API POST): all of them flow through ExecuteAsync. If the
            // project's SINK is an IdentityCenter connection that is NOT entitled
            // (no validated handshake link recorded, not grandfathered, and the dev
            // override is off), we finish the run "Skipped" with a clear message and
            // do NOT execute. Grandfathered/validated IC sinks and the dev override
            // pass straight through. Non-IC sinks are never gated here.
            var sinkForGate = await _tenantRepo.GetByIdAsync(project.SinkTenantId);
            if (sinkForGate is not null
                && Conduit.Core.Models.IcEntitlement.IsIdentityCenterType(sinkForGate.SystemType))
            {
                // Read via the indexer (no Configuration.Binder dependency needed):
                // the dev-override flag is true only for an explicit truthy value.
                var overrideRaw = _config[Conduit.Core.Models.IcEntitlement.DevOverrideConfigKey];
                var devOverride = bool.TryParse(overrideRaw, out var ov) && ov;
                if (!Conduit.Core.Models.IcEntitlement.IsValidated(sinkForGate, devOverride))
                {
                    const string gateMsg =
                        "IdentityCenter sink is not licensed: this connection has no validated " +
                        "link to a real IdentityCenter instance. Open the connection in Connected " +
                        "Systems and run Test Connection to validate it (included with an " +
                        "IdentityCenter license). Run skipped.";
                    await _runRepo.FinishAsync(run.Id, "Skipped", gateMsg, 0);
                    await Log(run.Id, "Warning", $"Run skipped: {gateMsg}");
                    if (ownsFlag)
                        await _projectRepo.FinishRunAsync(project.Id, "Skipped");
                    return run.Id;
                }
            }

            // Register this run with the in-process cancellation registry so the
            // "Stop Sync" button can trip the SAME token the host-shutdown path
            // uses. The linked token combines the caller's token (host shutdown /
            // scheduler stop) with the registry's per-project cancel signal. We
            // unregister in a finally so a fast re-run gets a clean slot.
            var runToken = _cancellation.Register(project.Id, cancellationToken);
            try
            {
                return await RunCoreAsync(project, run, runToken);
            }
            finally
            {
                _cancellation.Unregister(project.Id);
            }
        }
        catch (Exception ex)
        {
            // Early failure before/around run creation. Make sure the project is
            // unstuck so the next Run-Now isn't a permanent 409, and mark the run
            // Failed if it got created. RunCoreAsync owns its own success/failure
            // stamping, so this only fires for throws OUTSIDE that method.
            _logger.LogError(ex, "Sync project {ProjectId} failed before run execution", projectId);
            if (run is not null)
            {
                await _runRepo.FinishAsync(run.Id, "Failed", ex.Message, 0);
            }
            if (ownsFlag)
            {
                await _projectRepo.FinishRunAsync(projectId, "Failed");
            }
            // When we do NOT own the flag (lost/never-attempted CAS) we must not
            // touch it — it belongs to another in-flight run. Stale flags from a
            // crashed process are recovered by the startup sweep and the UI's
            // Force-release action, not by this path.
            throw;
        }
    }

    /// <summary>
    /// The run pass itself, once the project + run row + IsRunning flag are in
    /// place. Owns its own try/finally so IsRunning + run stats are stamped on
    /// EVERY outcome of the pass (success, cancel, mid-run throw).
    /// </summary>
    private async Task<Guid> RunCoreAsync(SyncProject project, SyncRun run, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var totals = new RunCounters();
        string status = "Succeeded";
        string? errorMessage = null;
        SyncCursor? newCursor = null;
        bool wasIncremental = false;

        // ── Step-outcome rollup state (false-success fix) ──────────────────────
        // IC's SyncProjectOrchestrator derives the run outcome from persisted
        // SyncStepRuns. Conduit has no per-step table — step results live in
        // memory — so we roll up from the in-loop StepResult outcomes instead of
        // blindly stamping "Succeeded". A run whose steps/records failed must be
        // reported Failed/PartialSuccess with the real reason surfaced, NOT a
        // green success with 0 records.
        int failedSteps = 0;       // steps whose StepResult classified as Failed
        int succeededSteps = 0;    // steps that did real work with no failures
        string? firstStepError = null; // first surfaced step-level error message
        bool anyMappingRan = false;    // at least one Mapping/legacy pass executed
        bool ranAnyStep = false;       // at least one workflow step executed at all

        try
        {
            // Source & sink tenants → adapters via registry → instances.
            var sourceTenant = await _tenantRepo.GetByIdAsync(project.SourceTenantId)
                ?? throw new InvalidOperationException($"Source tenant {project.SourceTenantId} not found.");
            var sinkTenant = await _tenantRepo.GetByIdAsync(project.SinkTenantId)
                ?? throw new InvalidOperationException($"Sink tenant {project.SinkTenantId} not found.");

            var sourceAdapter = _connectors.Require(sourceTenant.SystemType);
            var sinkAdapter = _connectors.Require(sinkTenant.SystemType);

            // Phase 2 multi-cred UX: push the project's credential-name overrides
            // onto the AsyncLocal context so per-connector cred readers can honor
            // them via CredentialNameContext.Resolve(). Falls through to the
            // per-adapter default when the project leaves these null.
            CredentialNameContext.Source = project.SourceCredentialName;
            CredentialNameContext.Sink = project.SinkCredentialName;

            // V22: per-endpoint IdentityCenter table selection. Push the project's
            // SourceTable / SinkTable onto the ambient table context so the IC
            // connector reads the right table per side (Objects | Identities). Set
            // by string key (no compile-time dep on the connector enum) — the
            // IdentityCenter connector resolves it; other connectors ignore it.
            // Null/unset → the connector defaults to Objects (back-compat).
            IdentityCenterTableContext.Source = project.SourceTable;
            IdentityCenterTableContext.Sink = project.SinkTable;

            // Ambient per-run log appender for source connectors that produce
            // per-record findings (SQL Discovery's per-server scan outcomes).
            // AsyncLocal: flows into the source enumeration on this async chain,
            // never leaks outside this run.
            var runIdForSourceLog = run.Id;
            SourceRunLogContext.Append = (level, message) => Log(runIdForSourceLog, level, message);

            var source = sourceAdapter.CreateSource(sourceTenant.Id)
                ?? throw new InvalidOperationException($"Source tenant '{sourceTenant.Name}' ({sourceTenant.SystemType}) does not support source operations.");
            var sink = sinkAdapter.CreateSink(sinkTenant.Id)
                ?? throw new InvalidOperationException($"Sink tenant '{sinkTenant.Name}' ({sinkTenant.SystemType}) does not support sink operations.");

            // Self-register the source-connection → Conduit-tenant credential mapping
            // whenever we push to an IdentityCenter sink. IC auto-seeds a
            // DirectoryConnection named SanitizeSource(sourceTenant.Name) — the SAME
            // value used as the per-record upsert Source — so form-driven write-back can
            // resolve THIS source tenant's credential from that name alone. The
            // orchestrator's own SourceTenant is the only writer (the name is never taken
            // from IC). A collision (same name, different tenant) is logged and rejected.
            if (string.Equals(sinkAdapter.SystemType, "IdentityCenter", StringComparison.OrdinalIgnoreCase))
            {
                var mappedName = IdentityCenterSourceName.Sanitize(sourceTenant.Name);
                try
                {
                    await _credentialMapRepo.UpsertAsync(mappedName, sourceTenant.Id);
                }
                catch (SinkConnectionCredentialMapCollisionException ex)
                {
                    _logger.LogWarning(ex,
                        "Source-connection credential mapping NOT updated for '{Name}' — it already resolves to a different tenant. Write-back will continue using the existing mapping.",
                        mappedName);
                }
            }

            // Phase 7: load workflow tree. Backfill (V17) guarantees at least
            // one Default workflow + one Default Mapping step exists for every
            // project — the defensive fallback below only kicks in if a freshly
            // created project hasn't been persisted yet.
            var workflows = (await _workflowRepo.GetByProjectAsync(project.Id))
                .Where(w => w.Enabled)
                .OrderBy(w => w.Ordinal)
                .ToList();

            if (workflows.Count == 0)
            {
                await Log(run.Id, "Warning", "Project has no Workflows defined; falling back to legacy single-pass Mapping using project-level mappings + scope.");
                var ctx = new RunContext(project, run.Id, sourceAdapter, sinkAdapter, source, sink, sourceTenant, sinkTenant);
                var legacyResult = await ExecuteLegacyMappingAsync(ctx, cancellationToken);
                totals.Add(legacyResult.Delta);
                newCursor = legacyResult.NewCursor;
                wasIncremental = legacyResult.WasIncremental;

                // Roll up the single legacy Mapping pass into the step tally.
                anyMappingRan = true;
                ranAnyStep = true;
                if (legacyResult.Delta.Failed > 0)
                {
                    failedSteps++;
                    firstStepError ??= $"Mapping pass reported {legacyResult.Delta.Failed} failed record(s). See run logs for the per-record reason.";
                }
                else
                {
                    succeededSteps++;
                }
            }
            else
            {
                var ctx = new RunContext(project, run.Id, sourceAdapter, sinkAdapter, source, sink, sourceTenant, sinkTenant);

                foreach (var wf in workflows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Log(run.Id, "Info", $"Workflow '{wf.Name}' starting (ordinal {wf.Ordinal}).");

                    var steps = (await _workflowRepo.GetStepsAsync(wf.Id))
                        .Where(s => s.Enabled)
                        .OrderBy(s => s.Ordinal)
                        .ToList();

                    // The "last step's match results" — consumed by PersonCreate
                    // when it follows a PersonMatch step in the same workflow.
                    Dictionary<string, PersonMatchResult>? lastMatches = null;
                    // The "last step's emitted objects" — what the previous step
                    // produced. PersonMatch needs the source objects; PersonCreate
                    // needs the misses; AssignManager needs an objectExternalId.
                    List<ConnectorObject>? lastBatch = null;

                    for (int stepIndex = 0; stepIndex < steps.Count; stepIndex++)
                    {
                        var step = steps[stepIndex];
                        cancellationToken.ThrowIfCancellationRequested();
                        await Log(run.Id, "Info", $"  Step '{step.Name}' [{step.StepType}] starting (ordinal {step.Ordinal}).");

                        // Fix 6 (hot-loop memory): a Mapping step only needs to
                        // accumulate its full emitted batch when a LATER enabled
                        // step in this workflow consumes EmittedBatch. Otherwise
                        // the pump skips the per-record List.Add entirely.
                        bool needEmitted = false;
                        for (int j = stepIndex + 1; j < steps.Count; j++)
                        {
                            var t = steps[j].StepType;
                            if (t == WorkflowStepTypes.PersonMatch
                                || t == WorkflowStepTypes.PersonCreate
                                || t == WorkflowStepTypes.AssignManager
                                || t == WorkflowStepTypes.AssignGroupOwner
                                // Lookup (IC-parity relationship resolution) consumes the
                                // upstream Mapping batch to read each object's manager /
                                // managedBy reference, so the prior Mapping step must
                                // accumulate its emitted batch when a Lookup follows it.
                                || t == WorkflowStepTypes.Lookup)
                            {
                                needEmitted = true;
                                break;
                            }
                        }

                        StepResult stepResult;
                        try
                        {
                            stepResult = step.StepType switch
                            {
                                WorkflowStepTypes.Mapping            => await ExecuteMappingStepAsync(ctx, step, needEmitted, cancellationToken),
                                WorkflowStepTypes.PersonMatch        => await ExecutePersonMatchStepAsync(ctx, step, lastBatch, cancellationToken),
                                WorkflowStepTypes.PersonCreate       => await ExecutePersonCreateStepAsync(ctx, step, lastBatch, lastMatches, cancellationToken),
                                WorkflowStepTypes.AssignManager      => await ExecuteAssignManagerStepAsync(ctx, step, lastBatch, cancellationToken),
                                WorkflowStepTypes.AssignGroupOwner   => await ExecuteAssignGroupOwnerStepAsync(ctx, step, lastBatch, cancellationToken),
                                WorkflowStepTypes.Lookup             => await ExecuteLookupStepAsync(ctx, step, lastBatch, cancellationToken),
                                WorkflowStepTypes.Custom             => await ExecuteCustomStepAsync(ctx, step, cancellationToken),
                                _                                    => StepResult.Skipped($"Unknown StepType '{step.StepType}' — skipped.")
                            };
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            await Log(run.Id, "Error", $"  Step '{step.Name}' threw: {ex.Message}");
                            stepResult = new StepResult { Delta = new RunDelta(0, 0, 0, 0, 0, 1) };
                        }

                        totals.Add(stepResult.Delta);

                        // Step-outcome rollup (false-success fix). A step that
                        // reported any failed records is a failed step; otherwise
                        // it counts as a real success. Pure-skip steps (capability
                        // missing / no upstream batch) are neither — they don't
                        // make the run green on their own, and they don't fail it.
                        ranAnyStep = true;
                        if (step.StepType == WorkflowStepTypes.Mapping)
                            anyMappingRan = true;

                        if (stepResult.Delta.Failed > 0)
                        {
                            failedSteps++;
                            firstStepError ??= $"Step '{step.Name}' [{step.StepType}] reported {stepResult.Delta.Failed} failure(s). See run logs for the reason.";
                        }
                        else if (stepResult.Delta.Created > 0 || stepResult.Delta.Updated > 0 || stepResult.Delta.Read > 0)
                        {
                            succeededSteps++;
                        }

                        // V25: per-class Mapping steps now persist their OWN cursor
                        // (per WorkflowStepId, inside ExecuteMappingStepAsync) and
                        // return NewCursor = null, so nothing is hoisted to the single
                        // run-level value anymore — that hoist is what clobbered N
                        // per-class cursors into one. Track wasIncremental for logging,
                        // but never let a step's cursor become the run-level cursor.
                        if (stepResult.WasIncremental)
                            wasIncremental = true;

                        // Forward inter-step state.
                        if (stepResult.EmittedBatch is not null) lastBatch = stepResult.EmittedBatch;
                        if (stepResult.PersonMatches is not null) lastMatches = stepResult.PersonMatches;

                        await Log(run.Id, "Info",
                            $"  Step '{step.Name}' done. +Read={stepResult.Delta.Read} +Created={stepResult.Delta.Created} +Updated={stepResult.Delta.Updated} +Skipped={stepResult.Delta.Skipped} +Failed={stepResult.Delta.Failed}.");

                        await _runRepo.UpdateCountersAsync(run.Id, totals.Read, totals.Created, totals.Updated, totals.Skipped, totals.Failed);
                    }

                    await Log(run.Id, "Info", $"Workflow '{wf.Name}' done.");
                }
            }

            await Log(run.Id, "Info",
                $"Run finished. Read={totals.Read} Created={totals.Created} Updated={totals.Updated} Skipped={totals.Skipped} Failed={totals.Failed}.");

            // ── Roll up the true run status from step outcomes ─────────────────
            // No top-level exception escaped, but individual steps/records may have
            // failed. Derive Failed / PartialSuccess / Succeeded from the tally so
            // a run that moved zero records (or whose mapping never returned) is not
            // reported as a green success.
            if (failedSteps > 0 && succeededSteps > 0)
            {
                status = "PartialSuccess";
                errorMessage = firstStepError;
            }
            else if (failedSteps > 0)
            {
                status = "Failed";
                errorMessage = firstStepError;
            }
            else if (anyMappingRan && totals.Read == 0 && !wasIncremental)
            {
                // "Query never returned" sentinel normalization. A FULL Mapping pass
                // that enumerated the source but read ZERO objects is almost always
                // a misconfiguration (bad BaseDN/filter, bind that silently returned
                // nothing) rather than a legitimately empty directory. Surface it as
                // a failure with a real reason instead of a silent green / 0-records
                // success — the long-standing false-success symptom.
                //
                // INCREMENTAL passes are exempt: a delta read that found 0 changes
                // since the cursor is a legitimate, common SUCCESS, not a bad bind.
                status = "Failed";
                errorMessage = "Source enumeration returned 0 objects. Check the connection credential, BaseDN, and LDAP filter — a truthful 0-record run is treated as a failure to avoid a silent green success.";
                await Log(run.Id, "Error", errorMessage);
            }
            else if (!ranAnyStep)
            {
                // Nothing executed at all (no workflows/steps, no legacy pass).
                status = "Failed";
                errorMessage = "No workflow steps executed — the project has no enabled Mapping step. Nothing was synced.";
                await Log(run.Id, "Error", errorMessage);
            }
            else
            {
                status = "Succeeded";
            }

            await Log(run.Id, status == "Succeeded" ? "Info" : "Warning",
                $"Run status rolled up to '{status}' (succeededSteps={succeededSteps}, failedSteps={failedSteps}, read={totals.Read}).");
        }
        catch (OperationCanceledException)
        {
            status = "Cancelled";
            errorMessage = "Cancelled by host.";
            await Log(run.Id, "Warning", "Run cancelled.");
        }
        catch (Exception ex)
        {
            status = "Failed";
            // ex.Message can be empty for some directory/bind failures; fall back
            // to the exception type name so the run never shows a blank reason.
            errorMessage = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            _logger.LogError(ex, "Sync project {ProjectId} failed", project.Id);
            await Log(run.Id, "Error", $"Run failed: {errorMessage}");
        }
        finally
        {
            sw.Stop();

            // Force ObjectsFailed >= 1 whenever the run did NOT succeed so the
            // counter agrees with the status (IC parity). Without this a Failed /
            // PartialSuccess run driven by a 0-record enumeration or a top-level
            // throw would show "0 failed", contradicting its own red status.
            int persistedFailed = totals.Failed;
            if (status is "Failed" or "PartialSuccess" && persistedFailed < 1)
                persistedFailed = 1;

            await _runRepo.UpdateCountersAsync(run.Id, totals.Read, totals.Created, totals.Updated, totals.Skipped, persistedFailed);
            // Persist the RUN-level cursor on success. V25: per-class Mapping steps now
            // own their cursors (saved per-step in ExecuteMappingStepAsync), so newCursor
            // is only non-null on the LEGACY single-pass path (no Workflows) — there it
            // still records the run's cursor on SyncRuns.[Cursor] for that path's resume
            // and for run history. Only persist on success so a failed run doesn't let
            // the next run silently skip data it never wrote downstream.
            if (status == "Succeeded" && newCursor is not null)
            {
                await _runRepo.SetCursorAsync(run.Id, newCursor.Token, wasIncremental);
            }
            else if (status == "Succeeded")
            {
                await _runRepo.SetCursorAsync(run.Id, null, wasIncremental);
            }
            await _runRepo.FinishAsync(run.Id, status, errorMessage, sw.ElapsedMilliseconds);
            await _projectRepo.FinishRunAsync(project.Id, status);
        }

        return run.Id;
    }

    // ─── Run-scoped context ──────────────────────────────────────────────────

    private sealed record RunContext(
        SyncProject Project,
        Guid RunId,
        IConnectorAdapter SourceAdapter,
        IConnectorAdapter SinkAdapter,
        IConnectorSource Source,
        IConnectorSink Sink,
        Core.Models.Tenant SourceTenant,
        Core.Models.Tenant SinkTenant);

    private sealed class RunCounters
    {
        public int Read, Created, Updated, Skipped, Failed, Other;
        public void Add(RunDelta d)
        {
            Read += d.Read;
            Created += d.Created;
            Updated += d.Updated;
            Skipped += d.Skipped;
            Failed += d.Failed;
            Other += d.Other;
        }
    }

    private readonly record struct RunDelta(int Read, int Created, int Updated, int Skipped, int Failed, int Other);

    /// <summary>What a single step returns. Most steps populate only Delta.</summary>
    private sealed class StepResult
    {
        public RunDelta Delta { get; init; }
        public SyncCursor? NewCursor { get; init; }
        public bool WasIncremental { get; init; }
        /// <summary>Source objects this step produced; consumed by the next step.</summary>
        public List<ConnectorObject>? EmittedBatch { get; init; }
        /// <summary>Match results keyed by ConnectorObject.SourceId. Set by PersonMatch.</summary>
        public Dictionary<string, PersonMatchResult>? PersonMatches { get; init; }

        public static StepResult Skipped(string _) => new() { Delta = new RunDelta(0, 0, 0, 1, 0, 0) };
    }

    // ─── Step executors ──────────────────────────────────────────────────────

    /// <summary>
    /// Phase 7 Mapping step: classic source→mapping→sink loop using the step's
    /// own per-step mappings + optional per-step scope. Falls through to the
    /// project-level scope when the step has none.
    /// </summary>
    private async Task<StepResult> ExecuteMappingStepAsync(RunContext ctx, WorkflowStep step, bool needEmitted, CancellationToken ct)
    {
        var mappings = await _workflowRepo.GetMappingsByStepAsync(step.Id);
        // Per-step scope first, then project scope, then default.
        var scope = await _workflowRepo.GetScopeByStepAsync(step.Id)
                 ?? await _projectRepo.GetProjectScopeAsync(ctx.Project.Id)
                 ?? new SyncProjectScope { SyncProjectId = ctx.Project.Id, PageSize = 1000 };

        // V23: per-step object class, falling back to the project's class for legacy
        // single-class steps whose ObjectClass was backfilled (or for any step that
        // genuinely has none). This is the resolution the plan calls load-bearing.
        var objectClass = !string.IsNullOrWhiteSpace(step.ObjectClass)
            ? step.ObjectClass!
            : ctx.Project.ObjectClass;

        // Resilience: a step persisted with ZERO mappings would silently read nothing
        // useful (the sink writes only the structural baseline) and the run shows
        // "Mappings=0". This happens when a project was created against a build whose
        // AttributeTemplateCatalog had no template for the source/class yet (the
        // mappings are frozen at creation, never re-derived) — e.g. an ARS-source
        // project created before the ActiveRoles templates shipped. Rather than fail
        // a red run, resolve mappings LIVE from the now-complete catalog using the
        // run's source/sink SystemType + this step's class. These are NOT persisted
        // (the operator can still edit/persist via the Workflows tab); they just keep
        // the run honest. If the catalog also has nothing, we fall through with the
        // empty set and the existing "Mappings=0" log line makes the gap visible.
        if (mappings.Count == 0)
        {
            var resolved = Templates.AttributeMapResolver.Resolve(
                ctx.SourceTenant.SystemType, ctx.SinkTenant.SystemType, objectClass);
            if (resolved.Count > 0)
            {
                mappings = resolved
                    .Select(r => new AttributeMapping
                    {
                        Id = Guid.NewGuid(),
                        SyncProjectId = ctx.Project.Id,
                        WorkflowStepId = step.Id,
                        SourceAttribute = r.SourceAttribute,
                        SinkAttribute = r.SinkAttribute,
                        IsRequired = r.IsRequired
                    })
                    .ToList();
                await Log(ctx.RunId, "Warning",
                    $"    Step '{step.Name}' had no saved mappings; auto-resolved {mappings.Count} from the " +
                    $"attribute-template catalog ({ctx.SourceTenant.SystemType} {objectClass} -> {ctx.SinkTenant.SystemType}). " +
                    "Open the step in the Workflows tab and Save to persist them.");
            }
        }

        var pump = await PumpAsync(ctx, mappings, scope, objectClass, step, needEmitted, ct);

        // V25 per-STEP cursor save. Persist the advanced cursor back to THIS step —
        // and ONLY this step — so each per-class read advances its own high-water mark
        // independently. (No cross-step leakage: the save is keyed to step.Id.)
        //
        // Fix 2 (incremental actually engages): persist the resolved cursor whenever
        // the pump produced one from a trustworthy pass — not only when the pass was
        // already incremental. That SEEDS the cursor on the first successful FULL run
        // so run 2+ resumes incrementally; before this, the cursor was only saved on
        // a WasIncremental pass, which could never happen without a stored cursor —
        // the chicken-and-egg that kept incremental permanently disengaged.
        //
        // Trust gates (all required):
        //   - a NewCursor was resolved (source supports cursors at all);
        //   - ZERO step-level failed records (a failed write means the data at this
        //     high-water mark was NOT durably applied — advancing would skip it);
        //   - the read is trustworthy: a COMPLETE read (proven by the source), or an
        //     already-incremental pass (today's behavior, kept so sources that don't
        //     implement WasCompleteRead don't regress their existing incremental).
        var advanceCursor = pump.NewCursor is not null
            && pump.Delta.Failed == 0
            && (pump.ReadWasComplete || pump.WasIncremental);
        if (advanceCursor)
        {
            await _workflowRepo.SetStepCursorAsync(step.Id, pump.NewCursor!.Token);
            await Log(ctx.RunId, "Info", pump.WasIncremental
                ? $"    Incremental: advanced step '{step.Name}' cursor."
                : $"    Incremental: seeded step '{step.Name}' cursor from this complete full read — next run resumes incrementally.");
        }

        return new StepResult
        {
            Delta = pump.Delta,
            // V25: the cursor is now persisted per-step above. Do NOT bubble it up to
            // the run-level newCursor (that path clobbered N steps into one value).
            NewCursor = null,
            WasIncremental = pump.WasIncremental,
            EmittedBatch = needEmitted ? pump.EmittedBatch : null
        };
    }

    /// <summary>
    /// Legacy fallback when no Workflows exist (freshly created project not yet
    /// backfilled). Reads project-level mappings via the project repo and uses
    /// the project-level scope.
    /// </summary>
    private async Task<LegacyResult> ExecuteLegacyMappingAsync(RunContext ctx, CancellationToken ct)
    {
        var mappings = await _projectRepo.GetMappingsAsync(ctx.Project.Id);
        var scope = await _projectRepo.GetProjectScopeAsync(ctx.Project.Id)
                 ?? new SyncProjectScope { SyncProjectId = ctx.Project.Id, PageSize = 1000 };
        // Legacy path (no Workflows): there are no steps, so the class is the project's
        // and the cursor falls back to the project-level last-run cursor (step = null).
        // No later steps exist to consume EmittedBatch → needEmitted: false.
        var pump = await PumpAsync(ctx, mappings, scope, ctx.Project.ObjectClass, null, needEmitted: false, ct);
        return new LegacyResult(pump.Delta, pump.NewCursor, pump.WasIncremental);
    }

    private readonly record struct LegacyResult(RunDelta Delta, SyncCursor? NewCursor, bool WasIncremental);

    private async Task<StepResult> ExecutePersonMatchStepAsync(
        RunContext ctx, WorkflowStep step, List<ConnectorObject>? lastBatch, CancellationToken ct)
    {
        if (!ctx.SinkAdapter.Capabilities.SupportsPersonMatch)
        {
            await Log(ctx.RunId, "Warning", $"    Sink {ctx.SinkTenant.SystemType} does not implement PersonMatch — step skipped.");
            return new StepResult { Delta = new RunDelta(0, 0, 0, 1, 0, 0) };
        }
        if (lastBatch is null || lastBatch.Count == 0)
        {
            await Log(ctx.RunId, "Warning", "    PersonMatch step has no upstream batch to match against — needs a Mapping step before it. Skipped.");
            return new StepResult { Delta = new RunDelta(0, 0, 0, 1, 0, 0) };
        }

        var matches = new Dictionary<string, PersonMatchResult>(lastBatch.Count, StringComparer.Ordinal);
        int hits = 0, misses = 0, failures = 0;
        foreach (var obj in lastBatch)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var r = await ctx.Sink.MatchPersonAsync(obj, ct);
                matches[obj.SourceId] = r;
                if (r.ErrorMessage is not null) failures++;
                else if (r.MatchedIdentityId is not null) hits++;
                else misses++;
            }
            catch (Exception ex)
            {
                failures++;
                matches[obj.SourceId] = PersonMatchResult.Fail(ex.Message);
            }
        }

        await Log(ctx.RunId, "Info", $"    PersonMatch: hits={hits} misses={misses} failures={failures}.");
        return new StepResult
        {
            Delta = new RunDelta(0, 0, 0, hits + misses, failures, 0),
            EmittedBatch = lastBatch,
            PersonMatches = matches
        };
    }

    private async Task<StepResult> ExecutePersonCreateStepAsync(
        RunContext ctx, WorkflowStep step,
        List<ConnectorObject>? lastBatch,
        Dictionary<string, PersonMatchResult>? lastMatches,
        CancellationToken ct)
    {
        if (!ctx.SinkAdapter.Capabilities.SupportsPersonCreate)
        {
            await Log(ctx.RunId, "Warning", $"    Sink {ctx.SinkTenant.SystemType} does not implement PersonCreate — step skipped.");
            return new StepResult { Delta = new RunDelta(0, 0, 0, 1, 0, 0) };
        }
        if (lastBatch is null || lastBatch.Count == 0)
        {
            await Log(ctx.RunId, "Warning", "    PersonCreate step has no upstream batch to act on. Skipped.");
            return new StepResult { Delta = new RunDelta(0, 0, 0, 1, 0, 0) };
        }

        int created = 0, failed = 0, skipped = 0;
        foreach (var obj in lastBatch)
        {
            ct.ThrowIfCancellationRequested();

            // If we have prior PersonMatch results, only create on miss.
            if (lastMatches is not null && lastMatches.TryGetValue(obj.SourceId, out var match))
            {
                if (match.MatchedIdentityId is not null) { skipped++; continue; }
                if (match.ErrorMessage is not null)      { skipped++; continue; }
            }

            try
            {
                var r = await ctx.Sink.CreatePersonAsync(obj, ct);
                if (r.CreatedIdentityId is not null) created++;
                else { failed++; await Log(ctx.RunId, "Error", $"    PersonCreate failed for SourceId={obj.SourceId}: {r.ErrorMessage}"); }
            }
            catch (Exception ex)
            {
                failed++;
                await Log(ctx.RunId, "Error", $"    PersonCreate threw for SourceId={obj.SourceId}: {ex.Message}");
            }
        }

        await Log(ctx.RunId, "Info", $"    PersonCreate: created={created} skipped={skipped} failed={failed}.");
        return new StepResult
        {
            Delta = new RunDelta(0, created, 0, skipped, failed, 0),
            EmittedBatch = lastBatch
        };
    }

    private async Task<StepResult> ExecuteAssignManagerStepAsync(
        RunContext ctx, WorkflowStep step,
        List<ConnectorObject>? lastBatch, CancellationToken ct)
    {
        if (!ctx.SinkAdapter.Capabilities.SupportsAssignManager)
        {
            await Log(ctx.RunId, "Warning", $"    Sink {ctx.SinkTenant.SystemType} does not implement AssignManager — step skipped.");
            return new StepResult { Delta = new RunDelta(0, 0, 0, 1, 0, 0) };
        }
        if (lastBatch is null || lastBatch.Count == 0)
        {
            await Log(ctx.RunId, "Warning", "    AssignManager step has no upstream batch. Skipped.");
            return new StepResult { Delta = new RunDelta(0, 0, 0, 1, 0, 0) };
        }

        int updated = 0, skipped = 0, failed = 0;
        foreach (var obj in lastBatch)
        {
            ct.ThrowIfCancellationRequested();

            // Resolve manager from common attribute keys. Source-stamped first,
            // mapped second. Sinks decide how to interpret the value (DN, UPN, GUID).
            string? managerRef = null;
            foreach (var key in new[] { "manager", "Manager", "managerId", "managerDN", "managerUpn" })
            {
                if (obj.Attributes.TryGetValue(key, out var v) && v is not null)
                {
                    managerRef = v.ToString();
                    if (!string.IsNullOrWhiteSpace(managerRef)) break;
                }
            }
            if (string.IsNullOrWhiteSpace(managerRef)) { skipped++; continue; }

            try
            {
                var r = await ctx.Sink.AssignManagerAsync(obj.SourceId, managerRef!, ct);
                switch (r.Outcome)
                {
                    case SinkWriteOutcome.Updated: updated++; break;
                    case SinkWriteOutcome.Created: updated++; break;
                    case SinkWriteOutcome.Skipped: skipped++; break;
                    case SinkWriteOutcome.Failed:  failed++;  await Log(ctx.RunId, "Error", $"    AssignManager failed for {obj.SourceId}: {r.ErrorMessage}"); break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                await Log(ctx.RunId, "Error", $"    AssignManager threw for {obj.SourceId}: {ex.Message}");
            }
        }

        await Log(ctx.RunId, "Info", $"    AssignManager: updated={updated} skipped={skipped} failed={failed}.");
        return new StepResult
        {
            Delta = new RunDelta(0, 0, updated, skipped, failed, 0),
            EmittedBatch = lastBatch
        };
    }

    private async Task<StepResult> ExecuteAssignGroupOwnerStepAsync(
        RunContext ctx, WorkflowStep step,
        List<ConnectorObject>? lastBatch, CancellationToken ct)
    {
        if (!ctx.SinkAdapter.Capabilities.SupportsAssignGroupOwner)
        {
            await Log(ctx.RunId, "Warning", $"    Sink {ctx.SinkTenant.SystemType} does not implement AssignGroupOwner — step skipped.");
            return new StepResult { Delta = new RunDelta(0, 0, 0, 1, 0, 0) };
        }
        if (lastBatch is null || lastBatch.Count == 0)
        {
            await Log(ctx.RunId, "Warning", "    AssignGroupOwner step has no upstream batch. Skipped.");
            return new StepResult { Delta = new RunDelta(0, 0, 0, 1, 0, 0) };
        }

        int updated = 0, skipped = 0, failed = 0;
        foreach (var obj in lastBatch)
        {
            ct.ThrowIfCancellationRequested();

            string? ownerRef = null;
            foreach (var key in new[] { "managedBy", "owner", "Owner", "ownerId", "ownerDN", "ownerUpn" })
            {
                if (obj.Attributes.TryGetValue(key, out var v) && v is not null)
                {
                    ownerRef = v.ToString();
                    if (!string.IsNullOrWhiteSpace(ownerRef)) break;
                }
            }
            if (string.IsNullOrWhiteSpace(ownerRef)) { skipped++; continue; }

            try
            {
                var r = await ctx.Sink.AssignGroupOwnerAsync(obj.SourceId, ownerRef!, ct);
                switch (r.Outcome)
                {
                    case SinkWriteOutcome.Updated:
                    case SinkWriteOutcome.Created: updated++; break;
                    case SinkWriteOutcome.Skipped: skipped++; break;
                    case SinkWriteOutcome.Failed:  failed++;  await Log(ctx.RunId, "Error", $"    AssignGroupOwner failed for {obj.SourceId}: {r.ErrorMessage}"); break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                await Log(ctx.RunId, "Error", $"    AssignGroupOwner threw for {obj.SourceId}: {ex.Message}");
            }
        }

        await Log(ctx.RunId, "Info", $"    AssignGroupOwner: updated={updated} skipped={skipped} failed={failed}.");
        return new StepResult
        {
            Delta = new RunDelta(0, 0, updated, skipped, failed, 0),
            EmittedBatch = lastBatch
        };
    }

    /// <summary>
    /// A sink <c>Failed</c> result that actually means "the reference could not be
    /// resolved on IC" (the manager/owner external id matched no IC row, or the object
    /// itself isn't synced yet) rather than a genuine transport/server error. These are
    /// benign for relationship resolution — counted as skipped, not failed.
    /// </summary>
    private static bool IsUnresolvedReference(string? error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        return error.Contains("404", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Not Found", StringComparison.OrdinalIgnoreCase)
            || error.Contains("No IC Identity matches", StringComparison.OrdinalIgnoreCase)
            || error.Contains("No IC group", StringComparison.OrdinalIgnoreCase)
            || error.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// IC-parity relationship-resolution step ("Resolve Manager Relationships" /
    /// "Resolve ManagedBy" / "Resolve Group Owner"). This is the FUNCTIONAL Lookup
    /// arm — it no longer falls through to the Skipped default.
    ///
    /// DESIGN — the canonical Conduit→IC governance-step pattern (settled here):
    ///   Conduit is a stateless pump; it has no Objects/Identities store and cannot
    ///   resolve a reference (manager DN → manager GUID) across batches the way IC can.
    ///   So a relationship-resolution step does NOT resolve locally. It (a) reads the
    ///   raw reference attribute that the upstream Mapping step already carried on each
    ///   object (manager / managedBy / owner) and (b) hands the (object, reference) pair
    ///   to IdentityCenter via the SINK's already-proven capability calls
    ///   (<c>AssignManagerAsync</c> / <c>AssignGroupOwnerAsync</c>) — IC owns the object
    ///   store + the resolution + this is an IC-licensed feature. The same transport the
    ///   <see cref="WorkflowStepTypes.AssignManager"/>/<see cref="WorkflowStepTypes.AssignGroupOwner"/>
    ///   steps use; Lookup is the IC-parity-named relationship resolver over it.
    ///
    /// GATING: functional only when the sink advertises the relevant capability — today
    ///   only the IdentityCenter sink does. A non-IC sink clean-skips (no crash, truthful
    ///   Skipped count). Same-source=target is harmless: the references are just pushed.
    ///
    /// HONEST DEGRADATION (verified against IC's contract): IC's
    ///   <c>PATCH /api/identities/{id}/manager</c> resolves the manager external id
    ///   against <c>Identities.PrimaryEmail</c> (UPN/email), NOT a DN. AD's <c>manager</c>
    ///   attribute is a DN, so an AD-sourced manager reference will come back unresolved
    ///   on IC and is counted as SKIPPED here (never a fake success). Entra/cloud sources
    ///   whose manager maps to a UPN/email resolve fully. Group owner is stored as-is by
    ///   IC (no resolution gate), so it resolves for any source.
    /// </summary>
    private async Task<StepResult> ExecuteLookupStepAsync(
        RunContext ctx, WorkflowStep step,
        List<ConnectorObject>? lastBatch, CancellationToken ct)
    {
        // The class this Lookup resolves: prefer the step's own class, fall back to the
        // project's. group/team → owner resolution; everything else → manager resolution.
        var objectClass = !string.IsNullOrWhiteSpace(step.ObjectClass)
            ? step.ObjectClass!
            : ctx.Project.ObjectClass;
        var isGroupClass =
            string.Equals(objectClass, "group", StringComparison.OrdinalIgnoreCase)
            || string.Equals(objectClass, "team", StringComparison.OrdinalIgnoreCase);

        // Capability gate — only a sink that absorbs the relationship can run this.
        // Non-IC sinks (and an IC sink that doesn't advertise the cap) clean-skip.
        var capable = isGroupClass
            ? ctx.SinkAdapter.Capabilities.SupportsAssignGroupOwner
            : ctx.SinkAdapter.Capabilities.SupportsAssignManager;
        if (!capable)
        {
            await Log(ctx.RunId, "Warning",
                $"    Lookup step '{step.Name}': sink {ctx.SinkTenant.SystemType} does not implement " +
                $"{(isGroupClass ? "AssignGroupOwner" : "AssignManager")} relationship resolution — step skipped (no-op).");
            return new StepResult { Delta = new RunDelta(0, 0, 0, 1, 0, 0) };
        }

        if (lastBatch is null || lastBatch.Count == 0)
        {
            await Log(ctx.RunId, "Warning",
                $"    Lookup step '{step.Name}' has no upstream batch to resolve against — it needs a Mapping step before it. Skipped.");
            return new StepResult { Delta = new RunDelta(0, 0, 0, 1, 0, 0) };
        }

        var refKeys = isGroupClass
            ? new[] { "managedBy", "owner", "Owner", "ownerId", "ownerDN", "ownerUpn" }
            : new[] { "manager", "Manager", "managerId", "managerDN", "managerUpn" };

        int resolved = 0, skipped = 0, failed = 0;
        foreach (var obj in lastBatch)
        {
            ct.ThrowIfCancellationRequested();

            string? reference = null;
            foreach (var key in refKeys)
            {
                if (obj.Attributes.TryGetValue(key, out var v) && v is not null)
                {
                    reference = v.ToString();
                    if (!string.IsNullOrWhiteSpace(reference)) break;
                }
            }
            // No reference on this object (most objects have no manager/owner) — not an
            // error; just nothing to resolve.
            if (string.IsNullOrWhiteSpace(reference)) { skipped++; continue; }

            try
            {
                var r = isGroupClass
                    ? await ctx.Sink.AssignGroupOwnerAsync(obj.SourceId, reference!, ct)
                    : await ctx.Sink.AssignManagerAsync(obj.SourceId, reference!, ct);
                switch (r.Outcome)
                {
                    case SinkWriteOutcome.Updated:
                    case SinkWriteOutcome.Created:
                        resolved++;
                        break;
                    case SinkWriteOutcome.Skipped:
                        // IC could not resolve the reference (e.g. AD manager DN vs IC's
                        // PrimaryEmail match) — truthfully a skip, not a success.
                        skipped++;
                        break;
                    case SinkWriteOutcome.Failed:
                        // Distinguish an UNRESOLVED reference from a genuine failure.
                        // IC returns 404 when the manager external id (an AD manager DN)
                        // doesn't match Identities.PrimaryEmail, and the sink returns a
                        // "No IC Identity matches" fail when the object/group itself isn't
                        // synced yet. Both are benign "nothing to resolve" outcomes — count
                        // them as SKIPPED, not failed, so an AD source (DN-keyed managers)
                        // doesn't paint the step/run red. Reserve `failed` for real errors
                        // (5xx, transport, unexpected).
                        if (IsUnresolvedReference(r.ErrorMessage))
                        {
                            skipped++;
                        }
                        else
                        {
                            failed++;
                            await Log(ctx.RunId, "Error",
                                $"    Lookup resolve failed for {obj.SourceId}: {r.ErrorMessage}");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                await Log(ctx.RunId, "Error", $"    Lookup resolve threw for {obj.SourceId}: {ex.Message}");
            }
        }

        await Log(ctx.RunId, "Info",
            $"    Lookup ({(isGroupClass ? "group owner" : "manager")} resolution): resolved={resolved} skipped={skipped} failed={failed}.");

        // Updated = links actually resolved on IC. Skipped covers both "no reference"
        // and "IC could not resolve" — the log line above disambiguates. EmittedBatch
        // is passed through so a later step in the same workflow can still consume it.
        return new StepResult
        {
            Delta = new RunDelta(0, 0, resolved, skipped, failed, 0),
            EmittedBatch = lastBatch
        };
    }

    private async Task<StepResult> ExecuteCustomStepAsync(RunContext ctx, WorkflowStep step, CancellationToken ct)
    {
        // Reserved for future plugin extensibility. Log + no-op.
        await Log(ctx.RunId, "Info", $"    Custom step '{step.Name}' is a no-op placeholder.");
        return new StepResult { Delta = new RunDelta(0, 0, 0, 1, 0, 0) };
    }

    // ─── Mapping-step pump (the Phase-2 source→mapping→sink loop) ────────────

    private readonly record struct PumpResult(
        RunDelta Delta,
        SyncCursor? NewCursor,
        bool WasIncremental,
        bool ReadWasComplete,
        List<ConnectorObject> EmittedBatch);

    private async Task<PumpResult> PumpAsync(
        RunContext ctx,
        IReadOnlyList<AttributeMapping> mappings,
        SyncProjectScope scope,
        string objectClass,
        WorkflowStep? step,
        bool needEmitted,
        CancellationToken ct)
    {
        var run = ctx.RunId;
        var project = ctx.Project;
        var source = ctx.Source;
        var sink = ctx.Sink;
        var sourceAdapter = ctx.SourceAdapter;
        var sinkAdapter = ctx.SinkAdapter;
        var sinkTenant = ctx.SinkTenant;

        // Recover the prior incremental cursor if the source supports it.
        //
        // V25 per-STEP cursor: each per-class Mapping step owns its OWN high-water
        // mark. When a step is supplied (the Workflow path) we read THIS step's
        // IncrementalCursor — never the project-level/last-run value — so the user
        // step's cursor can never be clobbered by the group step's, and vice-versa.
        //
        // The legacy single-pass path (no Workflows yet) has no step; it falls back to
        // the last successful RUN's cursor (SyncRuns.[Cursor]) exactly as before, so
        // that back-compat behavior is unchanged.
        SyncCursor? priorCursor = null;
        if (sourceAdapter.Capabilities.SupportsIncremental)
        {
            if (step is not null)
            {
                if (!string.IsNullOrEmpty(step.IncrementalCursor))
                {
                    priorCursor = new SyncCursor
                    {
                        Token = step.IncrementalCursor!,
                        IssuedAt = step.CursorUpdatedAt ?? DateTime.UtcNow
                    };
                    await Log(run, "Info", $"    Incremental: resuming step '{step.Name}' from its own cursor (updated {(step.CursorUpdatedAt?.ToString("o") ?? "unknown")}).");
                }
                else
                {
                    await Log(run, "Info", $"    Incremental: step '{step.Name}' has no prior cursor — running full enumeration for this class.");
                }
            }
            else
            {
                var last = await _runRepo.GetLastSuccessfulAsync(project.Id);
                if (last is not null && !string.IsNullOrEmpty(last.Cursor))
                {
                    priorCursor = new SyncCursor { Token = last.Cursor!, IssuedAt = last.StartedAt };
                    await Log(run, "Info", $"    Incremental: resuming from last-run cursor issued {last.StartedAt:o} (legacy project-level path).");
                }
                else
                {
                    await Log(run, "Info", "    Incremental: no prior cursor — running full enumeration.");
                }
            }
        }

        var sinkCaps = sinkAdapter.Capabilities;
        var batchSize = sinkCaps.SupportsBulk && sinkCaps.MaxBatchSize > 1 ? sinkCaps.MaxBatchSize : 1;

        // Sink-side skip-unchanged. Opt-in per project. Load-once: pull the whole
        // (project, sink) content-hash map at run start and reuse it across the hot
        // loop — no per-record SELECT. Records whose mapped payload hashes to the
        // stored value are skipped instead of re-pushed.
        //
        // Correctness: an empty map (first run, or a record never seen before)
        // means every record is treated as changed and written normally. Failed
        // writes never get a stored hash, so a retry re-writes them. Records with
        // no stable SourceId are never skipped. When in doubt, write.
        bool skipUnchanged = project.SkipUnchanged;
        Dictionary<string, SinkHashEntry> priorHashes = skipUnchanged
            ? await _hashRepo.LoadMapAsync(project.Id, sinkTenant.Id, objectClass)
            : new Dictionary<string, SinkHashEntry>(StringComparer.Ordinal);
        if (skipUnchanged)
            await Log(run, "Info", $"    Skip-unchanged ON; loaded {priorHashes.Count} prior sink hash(es) for class '{objectClass}'.");
        var writtenHashes = new List<KeyValuePair<string, string>>(256);

        // Source-declared volatile attributes (per-attempt timestamps) are excluded
        // from the content hash so they alone never force a re-ingest. Empty for
        // every connector that doesn't declare any — hash values are bit-identical
        // to before in that case.
        var hashVolatile = sourceAdapter.Capabilities.HashVolatileAttributes.Count > 0
            ? new HashSet<string>(sourceAdapter.Capabilities.HashVolatileAttributes, StringComparer.OrdinalIgnoreCase)
            : null;

        // Bounded refresh — ONLY for sources that declare HashVolatileAttributes.
        // Excluding volatile freshness attributes (sqlLastScannedAt etc.) means a
        // stable healthy record can stay hash-identical forever, so the sink-side
        // freshness column ages and the sink falsely reports it Stale (IC flips
        // IsOnline after a 7-day window). Fix: a hash-matched record is skipped
        // ONLY if it was last actually sent within the TTL; past that it re-ingests
        // so the volatile attributes refresh. 3 days = half IC's staleness window,
        // so a healthy server always refreshes well before flipping Stale. Sources
        // with no volatile attributes keep the exact prior semantics: hash match =
        // skip, regardless of age.
        var refreshCutoff = hashVolatile is not null
            ? DateTime.UtcNow - SinkHashRefreshTtl
            : (DateTime?)null;

        // ── Phase 2.2 delete-detection setup ────────────────────────────────────
        // Tombstoning is opt-in by SINK CAPABILITY (only IC implements the reversible
        // soft-delete contract). It runs INDEPENDENTLY of skip-unchanged, so we load
        // the prior-synced id set even when skip-unchanged is off — otherwise a
        // project that doesn't opt into skip-unchanged could never detect leavers.
        //
        // V26 per-CLASS scope: the prior set is the SinkRecordHashes keys for THIS
        // (project, sink, objectClass). Before V26 the registry was shared by every
        // class in the project, so a two-class project's user step diffed the WHOLE
        // registry against user-only seen ids and tombstoned every group record
        // (and vice versa). Scoping the load, diff, prune, and registry write to
        // this pump's class is the fix — plus the existing per-connection scoping.
        var tombstoneSink = sink as ITombstoneEmittingSink;
        // SOURCE-side veto: a discovery-style source (SQL Discovery) declares that
        // a missing record is NOT evidence of deletion — a failed scan and a
        // decommission look identical from the read. Nulling the sink handle here
        // disables BOTH tombstone paths (diff-based and source-emitted) and the
        // prior-id registry maintenance for this pump, by construction.
        if (tombstoneSink is not null && sourceAdapter.Capabilities.SuppressDeleteDetection)
        {
            await Log(run, "Info",
                $"    Delete-detection DISABLED: source connector {sourceAdapter.SystemType} declares SuppressDeleteDetection (a missing record is not evidence of deletion). Upsert-only.");
            tombstoneSink = null;
        }
        // Sign-in EVENT records are append-only and aged out at the source (Entra P1
        // ~30-day retention), so an event "disappearing" from a windowed read is
        // expiry, NOT a deletion. Never tombstone them. Conduit native class names
        // are lowercase; compare case-insensitively.
        if (tombstoneSink is not null && string.Equals(objectClass, "signinlog", StringComparison.OrdinalIgnoreCase))
        {
            await Log(run, "Info",
                "    Delete-detection DISABLED for class 'signinlog': sign-in events are append-only and age out at the source — a missing event is expiry, not deletion. Upsert-only.");
            tombstoneSink = null;
        }
        var deleteDetectionOn = tombstoneSink is not null;
        HashSet<string> priorIdsForDelete;
        if (deleteDetectionOn && !skipUnchanged)
        {
            var map = await _hashRepo.LoadMapAsync(project.Id, sinkTenant.Id, objectClass);
            priorIdsForDelete = new HashSet<string>(map.Keys, StringComparer.Ordinal);
        }
        else
        {
            // Reuse the already-loaded skip-unchanged map keys when both are on.
            priorIdsForDelete = new HashSet<string>(priorHashes.Keys, StringComparer.Ordinal);
        }
        // Every stable SourceId seen this run — INCLUDING skipped-unchanged records,
        // which are still present in the source and must NOT be tombstoned.
        var seenSourceIds = deleteDetectionOn
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        if (deleteDetectionOn)
            await Log(run, "Info", $"    Delete-detection ARMED (sink supports tombstones); loaded {priorIdsForDelete.Count} prior id(s) for class '{objectClass}'. Emission gated on a COMPLETE source read.");

        // Fix 2: source-emitted tombstones (Attributes["_deleted"] == true; AD
        // recycle-bin pass on incremental runs, Entra delta @removed). These are
        // NOT live objects: they must never be mapped/upserted, never hashed,
        // never counted as seen, never enter the emitted batch. Their SourceIds
        // are collected here and forwarded through the sink's tombstone contract
        // after the read (same safety gates as the diff path).
        var sourceTombstoneIds = new List<string>();

        await Log(run, "Info",
            $"    Source={ctx.SourceTenant.Name} ({ctx.SourceTenant.SystemType}) → Sink={sinkTenant.Name} ({sinkTenant.SystemType}); ObjectClass={objectClass}; Mappings={mappings.Count}; BatchSize={batchSize}; Incremental={sourceAdapter.Capabilities.SupportsIncremental}; SkipUnchanged={skipUnchanged}.");

        // Attribute projection hint. We know EXACTLY which source attributes this
        // step maps, so stamp them onto the scope (in-memory, not persisted). A
        // source connector that honors the hint (AD does) then requests only those
        // attributes + its own structural floor instead of every attribute — the
        // dominant cost in a large directory read. Sources that ignore the hint are
        // unaffected. We only set it when there's at least one mapped attribute; an
        // empty list would (correctly) read nothing useful, so leave null = read-all
        // in that degenerate case. The connector always re-adds its structural set,
        // so a mapped attribute that the connector also needs is never double-trouble.
        var mappedSourceAttrs = mappings
            .Select(m => m.SourceAttribute)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        scope.RequestedAttributes = mappedSourceAttrs.Count > 0 ? mappedSourceAttrs : null;

        // V23: read THIS step's object class (resolved by the caller as
        // step.ObjectClass ?? project.ObjectClass), not the project-level one. Each
        // Mapping step is its own complete per-class read — its own cursor, its own
        // scope, its own skip-unchanged / tombstone diff — so threading the class here
        // is the whole behavioural change: one project, N per-class pumps.
        var enumeration = await source.EnumerateAsync(objectClass, scope, priorCursor, ct);
        var wasIncremental = enumeration.IsIncremental;

        var buffer = new List<ConnectorObject>(Math.Min(batchSize, 256));
        // Parallel to buffer: the content hash of each buffered record (null when
        // not computable). FlushAsync caches the hash only for records the sink
        // actually accepted, so failures don't poison the cache.
        var bufferHashes = new List<string?>(Math.Min(batchSize, 256));
        var emitted = new List<ConnectorObject>(256);
        // Group-membership second pass: when this step is a group class, capture each
        // group's source id + its member ids here and push them to the sink AFTER the
        // object upserts drain (groups + members must already exist sink-side). Only
        // populated for the "group" class + a sink that absorbs membership; otherwise
        // it stays empty and the second pass is a no-op.
        var groupMembershipBuffer = new List<GroupMembership>();
        // Capture membership only on a group-like step into a sink that absorbs it.
        // Conduit native class names are lowercase; compare case-insensitively.
        // "team" carries member edges in Attributes["members"] exactly like "group".
        var captureGroupMembership =
            (string.Equals(objectClass, "group", StringComparison.OrdinalIgnoreCase)
             || string.Equals(objectClass, "team", StringComparison.OrdinalIgnoreCase))
            && sink is IGroupMembershipEmittingSink;
        int read = 0, created = 0, updated = 0, skipped = 0, failed = 0, ttlRefreshed = 0;

        var progress = Stopwatch.StartNew();

        await foreach (var sourceObj in enumeration.Objects.WithCancellation(ct))
        {
            read++;

            // Fix 2: divert source-emitted tombstone records BEFORE mapping. A
            // tombstone is not a live object — upserting it would resurrect an
            // AD recycle-bin entry as a live sink record. Collect its id for the
            // post-read tombstone emission and skip everything else (mapping,
            // seenSourceIds, skip-unchanged hashing, the emitted batch).
            if (sourceObj.Attributes.TryGetValue("_deleted", out var deletedFlag)
                && deletedFlag is bool isDeleted && isDeleted)
            {
                if (!string.IsNullOrEmpty(sourceObj.SourceId))
                    sourceTombstoneIds.Add(sourceObj.SourceId);
                continue;
            }

            if (!sourceObj.Attributes.ContainsKey("_source"))
                sourceObj.Attributes["_source"] = sourceAdapter.SystemType;

            var sinkObj = ApplyMappings(sourceObj, mappings);

            if (!sinkObj.Attributes.ContainsKey("_source"))
                sinkObj.Attributes["_source"] = sourceAdapter.SystemType;

            // Capture group membership BEFORE skip-unchanged: members can change
            // without the group's mapped attributes changing, so unchanged groups must
            // still emit their current member set. Read from the RAW sourceObj bag, not
            // sinkObj: ApplyMappings only keeps mapped attributes, and the member
            // attribute is intentionally NOT in most templates — the cloud sources
            // (Entra/GWS/AWS) emit "members" (member GUIDs/ids) unconditionally; AD
            // emits "member" (DNs) only when the group step maps it. Key the edge on
            // sinkObj.SourceId (== sourceObj.SourceId, the SourceUniqueId the upsert used).
            if (captureGroupMembership && !string.IsNullOrEmpty(sinkObj.SourceId))
            {
                if (!sourceObj.Attributes.TryGetValue("members", out var memberVal))
                    sourceObj.Attributes.TryGetValue("member", out memberVal);
                var memberIds = CoerceToStringList(memberVal);
                if (memberIds.Count > 0)
                    groupMembershipBuffer.Add(new GroupMembership(sinkObj.SourceId, memberIds));
            }

            // Stamp the source CONNECTION name (the domain, e.g. "domain.local2")
            // onto every record so the IC sink can use it as the upsert Source.
            // IC auto-seeds a DirectoryConnection named exactly this value and
            // groups the objects under a domain node — matching IC's native
            // domain nodes instead of a literal "Conduit" node. Mirrors how
            // "_source" carries the SystemType (→ IC's OriginalSource); this
            // internal "_" key is lifted out by the sink, never written as a
            // real ObjectAttribute. ApplyMappings returns a fresh object that
            // doesn't carry "_"-prefixed keys, so stamp it on sinkObj here.
            if (!string.IsNullOrWhiteSpace(ctx.SourceTenant.Name) && !sinkObj.Attributes.ContainsKey("_sourceConnection"))
                sinkObj.Attributes["_sourceConnection"] = ctx.SourceTenant.Name;

            if (!string.IsNullOrWhiteSpace(scope.BaseDN) && !sinkObj.Attributes.ContainsKey("targetOU"))
                sinkObj.Attributes["targetOU"] = scope.BaseDN;

            // Fix 6: only accumulate the full emitted list when a later step in
            // this workflow actually consumes it — otherwise this is pure memory
            // growth proportional to the directory size.
            if (needEmitted)
                emitted.Add(sinkObj);

            // Delete-detection: record EVERY stable id seen this run, BEFORE the
            // skip-unchanged gate below. A skipped-unchanged record is still present
            // in the source — it must count as "seen" so it is never tombstoned.
            if (seenSourceIds is not null && !string.IsNullOrEmpty(sinkObj.SourceId))
                seenSourceIds.Add(sinkObj.SourceId);

            // Skip-unchanged gate. Only applies when opt-in AND the record carries
            // a stable external id. Compute the hash once; reuse it on write.
            string? hash = null;
            if (skipUnchanged && !string.IsNullOrEmpty(sinkObj.SourceId))
            {
                hash = ComputeContentHash(sinkObj, hashVolatile);
                if (priorHashes.TryGetValue(sinkObj.SourceId, out var prev) && prev.ContentHash == hash)
                {
                    if (refreshCutoff is null || prev.UpdatedAt >= refreshCutoff.Value)
                    {
                        skipped++;
                        continue;
                    }
                    // Hash-stable but past the refresh TTL: force a re-ingest so the
                    // sink's volatile freshness attributes update. The successful
                    // write refreshes UpdatedAt via the end-of-run hash upsert.
                    ttlRefreshed++;
                }
            }

            buffer.Add(sinkObj);
            bufferHashes.Add(hash);

            if (buffer.Count >= batchSize)
            {
                var delta = await FlushAsync(sink, buffer, bufferHashes, writtenHashes, run, project.Id, sinkTenant.Id, sinkAdapter.SystemType, ct);
                created += delta.Created; updated += delta.Updated; skipped += delta.Skipped; failed += delta.Failed;
                buffer.Clear();
                bufferHashes.Clear();
            }

            // Throttle UI counter writes to ~every 2.5s rather than every N records.
            if (progress.ElapsedMilliseconds >= 2500)
            {
                await _runRepo.UpdateCountersAsync(run, read, created, updated, skipped, failed);
                progress.Restart();
            }
        }

        if (buffer.Count > 0)
        {
            var delta = await FlushAsync(sink, buffer, bufferHashes, writtenHashes, run, project.Id, sinkTenant.Id, sinkAdapter.SystemType, ct);
            created += delta.Created; updated += delta.Updated; skipped += delta.Skipped; failed += delta.Failed;
            buffer.Clear();
            bufferHashes.Clear();
        }

        if (ttlRefreshed > 0)
            await Log(run, "Info", $"    Bounded refresh: {ttlRefreshed} hash-unchanged record(s) re-ingested anyway (last sent > {SinkHashRefreshTtl.TotalDays:0} days ago) to keep volatile freshness attributes current.");

        // Persist hashes for records the sink accepted this run. Done once at the
        // end (bounded by the change set) so the next run can skip them. V26:
        // scoped to this pump's class.
        if (skipUnchanged && writtenHashes.Count > 0)
        {
            try { await _hashRepo.UpsertManyAsync(project.Id, sinkTenant.Id, objectClass, writtenHashes); }
            catch (Exception ex) { await Log(run, "Warning", $"    Could not persist sink hashes: {ex.Message}"); }
        }

        // Complete-read sentinel. Computed unconditionally because three consumers
        // need it: the diff-based tombstone gate, the source-emitted tombstone gate,
        // and the caller's cursor-seeding gate (Fix 2). Sources that don't implement
        // it report false (the safe default).
        bool readWasComplete;
        try { readWasComplete = enumeration.WasCompleteRead(); }
        catch { readWasComplete = false; }

        // ── Phase 2.2 delete-detection + tombstone emission ─────────────────────
        // SAFETY GATE: the linchpin. We emit tombstones ONLY when EVERY one of these
        // holds. Any failure leaves the sink untouched (upsert-only this run):
        //   (1) the sink supports the reversible soft-delete contract;
        //   (2) the source proved a COMPLETE read (WasCompleteRead() == true) — a
        //       partial/failed/cancelled/truncated read is indistinguishable from a
        //       clean drain by the stream alone, so this sentinel is mandatory;
        //   (3) the run is not being torn down (no cancellation requested);
        //   (4) we have a non-empty prior id set to diff against;
        //   (5) the current read returned a plausible (non-zero) population — a
        //       "complete" read that returned ZERO live objects while we hold a
        //       non-empty prior set is treated as SUSPECT and is NOT emitted (it is
        //       almost always a silently-empty bind, not a real full depopulation);
        //   (6) Fix 2: the read was a FULL enumeration. An INCREMENTAL pass only
        //       sees records changed since the cursor, so its seen-set is partial
        //       by design — diffing the prior registry against it would tombstone
        //       every unchanged record. On incremental passes, deletes come ONLY
        //       from source-emitted tombstone records (handled below).
        if (deleteDetectionOn && seenSourceIds is not null && tombstoneSink is not null && !wasIncremental)
        {
            var complete = readWasComplete;

            if (!complete)
            {
                await Log(run, "Warning",
                    "    Tombstoning SKIPPED: source read was NOT complete (partial/failed/truncated). Upsert-only this run — no deletes computed.");
            }
            else if (ct.IsCancellationRequested)
            {
                await Log(run, "Warning", "    Tombstoning SKIPPED: run cancellation requested.");
            }
            else if (priorIdsForDelete.Count == 0)
            {
                await Log(run, "Info", "    Tombstoning: no prior id set (first run for this sink) — nothing to delete-detect.");
            }
            else if (read == 0 || seenSourceIds.Count == 0)
            {
                // Suspicious: a "complete" read that saw nothing, yet we have prior
                // records. Refuse to wipe the population on a likely-empty bind.
                await Log(run, "Error",
                    $"    Tombstoning REFUSED: read reported complete but saw 0 objects while {priorIdsForDelete.Count} prior id(s) exist. Treating as a suspect read — NO tombstones emitted. Investigate the source bind/filter.");
            }
            else
            {
                // The delta: prior-synced ids NOT seen in this complete read = leavers.
                var disappeared = new List<string>();
                foreach (var priorId in priorIdsForDelete)
                    if (!seenSourceIds.Contains(priorId))
                        disappeared.Add(priorId);

                if (disappeared.Count == 0)
                {
                    // Per-class proof (V26): a multi-class project's steps each diff
                    // ONLY their own class registry, so a stable two-class run logs
                    // this line once per class with zero tombstones.
                    await Log(run, "Info", $"    Tombstoning (class '{objectClass}'): complete read, no disappeared records. Nothing to delete.");
                }
                else
                {
                    // IC keys the soft-delete on the SAME Source string the IC sink
                    // stamps on its upserts. The sink now derives Source per-record
                    // from "_sourceConnection" (the source connection / domain name,
                    // e.g. "domain.local2"), which the orchestrator stamps above from
                    // ctx.SourceTenant.Name — with a "Conduit" fallback in the sink if
                    // the attribute is ever missing. Pass that SAME source connection
                    // name here so IC resolves the SAME auto-seeded SourceConnectionId
                    // (plus IC's SourceConnectionId SQL guard) and the delete is scoped
                    // to this connection. These two MUST agree or tombstones mis-target.
                    var icUpsertSource = !string.IsNullOrWhiteSpace(ctx.SourceTenant.Name)
                        ? ctx.SourceTenant.Name
                        : "Conduit";

                    await Log(run, "Warning",
                        $"    Tombstoning (class '{objectClass}'): complete read detected {disappeared.Count} disappeared record(s) of {priorIdsForDelete.Count} prior; emitting to sink (IC enforces a 50% cap as the backstop).");

                    try
                    {
                        var emit = await tombstoneSink.EmitTombstonesAsync(icUpsertSource, disappeared, ct);
                        if (!emit.Succeeded)
                        {
                            await Log(run, "Error", $"    Tombstone emission FAILED: {emit.ErrorMessage}. No prune; will retry next complete run.");
                        }
                        else if (emit.Aborted)
                        {
                            await Log(run, "Error",
                                $"    Tombstone emission ABORTED by sink safety cap: {emit.AbortReason} (requested={emit.Requested}, matched={emit.Matched}). Records NOT deleted; NOT pruned.");
                        }
                        else
                        {
                            await Log(run, "Info",
                                $"    Tombstones applied: requested={emit.Requested}, matched={emit.Matched}, softDeleted={emit.SoftDeleted}.");
                        }

                        // Prune ONLY the ids the sink durably actioned, so they aren't
                        // re-emitted every run. If one reappears in the source later,
                        // a normal upsert revives it on the IC side (DeletedAt cleared)
                        // and re-registers its hash. Aborted/failed ids are NOT pruned.
                        if (emit.PrunableIds.Count > 0)
                        {
                            try
                            {
                                await _hashRepo.DeleteManyAsync(project.Id, sinkTenant.Id, objectClass, emit.PrunableIds);
                                await Log(run, "Info", $"    Pruned {emit.PrunableIds.Count} tombstoned id(s) from the sink hash registry (class '{objectClass}').");
                            }
                            catch (Exception ex)
                            {
                                await Log(run, "Warning", $"    Could not prune tombstoned ids from hash registry: {ex.Message}");
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        await Log(run, "Error", $"    Tombstone emission threw: {ex.Message}. Upsert results stand; no deletes pruned.");
                    }
                }
            }
        }

        // ── Fix 2: source-emitted tombstone forwarding ──────────────────────────
        // Records the SOURCE itself flagged deleted (AD recycle-bin pass on an
        // incremental read, Entra delta @removed). These were diverted out of the
        // upsert loop above; forward them through the sink's reversible soft-delete
        // contract under the SAME safety gates as the diff path: tombstone-capable
        // sink, complete read, no teardown. Sinks without the contract get a
        // Warning and the records are skipped entirely (never upserted as live).
        if (sourceTombstoneIds.Count > 0)
        {
            if (tombstoneSink is null)
            {
                await Log(run, "Warning",
                    $"    Source emitted {sourceTombstoneIds.Count} tombstone record(s) (class '{objectClass}') but the sink does not support the soft-delete contract — skipped (not upserted, not deleted).");
            }
            else if (!readWasComplete)
            {
                await Log(run, "Warning",
                    $"    Source-emitted tombstones SKIPPED: source read was NOT complete (partial/failed/truncated). {sourceTombstoneIds.Count} candidate(s) will be re-evaluated next run.");
            }
            else if (ct.IsCancellationRequested)
            {
                await Log(run, "Warning", "    Source-emitted tombstones SKIPPED: run cancellation requested.");
            }
            else
            {
                var icUpsertSource = !string.IsNullOrWhiteSpace(ctx.SourceTenant.Name)
                    ? ctx.SourceTenant.Name
                    : "Conduit";
                await Log(run, "Warning",
                    $"    Source emitted {sourceTombstoneIds.Count} tombstone record(s) (class '{objectClass}'); emitting to sink (sink-side safety cap still applies).");
                try
                {
                    var emit = await tombstoneSink.EmitTombstonesAsync(icUpsertSource, sourceTombstoneIds, ct);
                    if (!emit.Succeeded)
                    {
                        await Log(run, "Error", $"    Source-tombstone emission FAILED: {emit.ErrorMessage}. Will retry next run.");
                    }
                    else if (emit.Aborted)
                    {
                        await Log(run, "Error",
                            $"    Source-tombstone emission ABORTED by sink safety cap: {emit.AbortReason} (requested={emit.Requested}, matched={emit.Matched}).");
                    }
                    else
                    {
                        await Log(run, "Info",
                            $"    Source tombstones applied: requested={emit.Requested}, matched={emit.Matched}, softDeleted={emit.SoftDeleted}.");
                    }

                    if (emit.PrunableIds.Count > 0)
                    {
                        try
                        {
                            await _hashRepo.DeleteManyAsync(project.Id, sinkTenant.Id, objectClass, emit.PrunableIds);
                            await Log(run, "Info", $"    Pruned {emit.PrunableIds.Count} source-tombstoned id(s) from the sink hash registry (class '{objectClass}').");
                        }
                        catch (Exception ex)
                        {
                            await Log(run, "Warning", $"    Could not prune source-tombstoned ids from hash registry: {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    await Log(run, "Error", $"    Source-tombstone emission threw: {ex.Message}. Upsert results stand.");
                }
            }
        }

        // ── Group-membership second pass ────────────────────────────────────────
        // After the object upserts have drained (groups + their members now exist on
        // the sink side), push the captured group→member edges. NOT gated on
        // readWasComplete (unlike tombstones): IC's /api/objects/group-memberships/bulk
        // UPSERTS edges and never prunes absent ones, so a partial read only adds FEWER
        // edges — it can never look like a delete or a membership shrink. Membership is
        // additive; tombstones are destructive, so tombstones keep the complete-read
        // gate and this does not. Best-effort — failures are logged and the run proceeds.
        if (groupMembershipBuffer.Count > 0
            && sink is IGroupMembershipEmittingSink memSink
            && !ct.IsCancellationRequested)
        {
            // Same expression the upsert + tombstone paths use so IC resolves the
            // SAME connection the group/member objects landed under.
            var icUpsertSource = !string.IsNullOrWhiteSpace(ctx.SourceTenant.Name)
                ? ctx.SourceTenant.Name
                : "Conduit";

            // AD member values are DNs, not objectGUIDs; IC keys members by
            // objectGUID (Objects.SourceUniqueId). DNs will come back unresolved on
            // IC (silently skipped, no error) until reconciliation lands. Warn so the
            // gap is visible. Cloud sources (Entra/GWS/AWS) emit GUID-keyed member ids
            // and resolve fully.
            // TODO(membership): AD member DN->objectGUID reconciliation — project each
            // member's objectGUID in the AD group read (or a DN->GUID lookup pass) so
            // AD group membership resolves on IC the same way cloud membership does.
            if (string.Equals(sourceAdapter.SystemType, "ActiveDirectory", StringComparison.OrdinalIgnoreCase))
            {
                await Log(run, "Warning",
                    $"    Group memberships (class '{objectClass}'): source is ActiveDirectory — member values are DNs, not objectGUIDs. Pushing as-is; IC will leave them UNRESOLVED until DN->objectGUID reconciliation is implemented (cloud sources resolve fully).");
            }

            try
            {
                var edges = await memSink.EmitGroupMembershipsAsync(icUpsertSource, groupMembershipBuffer, ct);
                await Log(run, "Info",
                    $"    Group memberships: pushed {edges} edge(s) across {groupMembershipBuffer.Count} group(s).");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                await Log(run, "Error", $"    Group-membership emission threw: {ex.Message}. Upsert results stand.");
            }
        }

        // Maintain the per-connection, per-class prior-synced id REGISTRY.
        // Delete-detection diffs against SinkRecordHashes keys, so those keys must
        // reflect "what we last successfully synced to this sink" — INDEPENDENT of
        // skip-unchanged. When skip-unchanged is OFF we still register the ids the
        // sink accepted this run (with a sentinel hash) so the NEXT complete run can
        // detect their absence. When skip-unchanged is ON the writtenHashes above
        // already did this.
        //
        // ONLY register on a COMPLETE read. A partial read holds a partial seen-set;
        // registering it would silently drop real ids from the prior set, degrading
        // delete-detection over repeated partial runs. Skipping registration on a
        // partial read keeps the last complete registry intact (safe-leaning: a real
        // leaver may go undetected for a cycle, but a present record is never wiped).
        // Incremental passes are additive here by construction (UpsertMany never
        // removes keys), so registering their partial seen-set is safe and keeps
        // newly created records delete-detectable.
        if (deleteDetectionOn && readWasComplete && !skipUnchanged && seenSourceIds is not null && seenSourceIds.Count > 0)
        {
            // Register every id seen this run (present in source). Use a sentinel
            // hash — the value is irrelevant for delete-detection (only the key is),
            // and skip-unchanged is off so no one reads it back as a content hash.
            const string registryHash = "TOMBSTONE-REGISTRY-NO-CONTENT-HASH--------00";
            var registry = new List<KeyValuePair<string, string>>(seenSourceIds.Count);
            foreach (var id in seenSourceIds)
                registry.Add(new KeyValuePair<string, string>(id, registryHash));
            try { await _hashRepo.UpsertManyAsync(project.Id, sinkTenant.Id, objectClass, registry); }
            catch (Exception ex) { await Log(run, "Warning", $"    Could not update prior-synced id registry: {ex.Message}"); }
        }

        SyncCursor? newCursor = null;
        try { newCursor = enumeration.ResolveNewCursor(); }
        catch (Exception ex)
        {
            await Log(run, "Warning", $"    Could not resolve cursor: {ex.Message}");
        }

        return new PumpResult(
            new RunDelta(read, created, updated, skipped, failed, 0),
            newCursor,
            wasIncremental,
            readWasComplete,
            emitted);
    }

    // Bounded-refresh TTL for the skip-unchanged path on volatile-hash sources:
    // a hash-matched record last sent more than this long ago re-ingests anyway,
    // refreshing sink-side volatile freshness attributes (sqlLastScannedAt etc.)
    // before IC's 7-day staleness window can falsely flip the record Stale.
    private static readonly TimeSpan SinkHashRefreshTtl = TimeSpan.FromDays(3);

    // Field/unit separators for the hash canonical form (ASCII FS/US control
    // chars) — chosen so they can't collide with attribute keys or values.
    private const char HashFieldSep = '\x1f';
    private const char HashUnitSep = '\x1e';

    /// <summary>
    /// Stable SHA-256 of a mapped sink record. Attributes are sorted by key
    /// (ordinal) and serialized so the hash is order-independent. The transient
    /// "_source"/"targetOU" routing hints are included because changing the target
    /// OU IS a real change the sink must see. Base64 of the 32-byte digest = 44
    /// chars, matching SinkRecordHashes.ContentHash CHAR(44).
    /// <paramref name="volatileAttributes"/> (source-declared, usually null) names
    /// attributes excluded from the canonical form — per-attempt timestamps that
    /// would otherwise defeat skip-unchanged.
    /// </summary>
    private static string ComputeContentHash(ConnectorObject obj, HashSet<string>? volatileAttributes = null)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append(obj.ObjectClass).Append(HashUnitSep);
        foreach (var key in obj.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (volatileAttributes is not null && volatileAttributes.Contains(key))
                continue;
            sb.Append(key).Append(HashFieldSep);
            var v = obj.Attributes[key];
            sb.Append(v is null
                ? "\x00"
                : System.Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(HashUnitSep);
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        var digest = System.Security.Cryptography.SHA256.HashData(bytes);
        return System.Convert.ToBase64String(digest);
    }

    /// <summary>
    /// Phase 2: flush an accumulated batch through the sink. Uses UpsertBatchAsync
    /// when the sink advertises SupportsBulk (single batch network call), else
    /// the default loops over UpsertAsync per record (identical to Phase 1).
    /// </summary>
    private readonly record struct FlushDelta(int Created, int Updated, int Skipped, int Failed);

    private async Task<FlushDelta> FlushAsync(
        IConnectorSink sink,
        List<ConnectorObject> buffer,
        List<string?> bufferHashes,
        List<KeyValuePair<string, string>> writtenHashes,
        Guid runId,
        Guid projectId,
        Guid sinkTenantId,
        string sinkSystemType,
        CancellationToken ct)
    {
        IReadOnlyList<SinkWriteResult> results;
        try
        {
            results = await sink.UpsertBatchAsync(buffer, ct);
        }
        catch (Exception ex)
        {
            await Log(runId, "Error", $"    Sink bulk upsert threw for {buffer.Count} records: {ex.Message}");
            return new FlushDelta(0, 0, 0, buffer.Count);
        }

        int c = 0, u = 0, s = 0, f = 0;
        int asyncSubmitted = 0;
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (r.AsyncJob is not null)
            {
                var srcObj = i < buffer.Count ? buffer[i] : null;
                try
                {
                    await _asyncJobRepo.InsertAsync(new Core.SyncModels.SyncRunAsyncJob
                    {
                        SyncRunId = runId,
                        SyncProjectId = projectId,
                        TenantId = sinkTenantId,
                        SystemType = sinkSystemType,
                        JobType = r.AsyncJob.JobType,
                        JobId = r.AsyncJob.JobId,
                        ObjectExternalId = string.IsNullOrEmpty(r.AsyncJob.ObjectExternalId) ? srcObj?.SourceId : r.AsyncJob.ObjectExternalId,
                        PayloadJson = r.AsyncJob.PayloadJson,
                        State = "Pending",
                        SubmittedAt = DateTime.UtcNow
                    });
                    asyncSubmitted++;
                }
                catch (Exception ex)
                {
                    await Log(runId, "Warning", $"    Could not persist async job submission ({r.AsyncJob.JobType} / {r.AsyncJob.JobId}): {ex.Message}");
                }
            }

            // Cache the content hash only for records the sink durably accepted
            // (Created/Updated) AND that carried a computable hash (skip-unchanged
            // on + stable SourceId). Async-job submissions surface as Skipped with
            // an AsyncJob descriptor — the write isn't durable until the poller
            // confirms it, so they (and plain Skipped/Failed) are never cached.
            if ((r.Outcome == SinkWriteOutcome.Created || r.Outcome == SinkWriteOutcome.Updated)
                && r.AsyncJob is null
                && i < bufferHashes.Count && bufferHashes[i] is { } h
                && i < buffer.Count && !string.IsNullOrEmpty(buffer[i].SourceId))
            {
                writtenHashes.Add(new KeyValuePair<string, string>(buffer[i].SourceId, h));
            }

            switch (r.Outcome)
            {
                case SinkWriteOutcome.Created: c++; break;
                case SinkWriteOutcome.Updated: u++; break;
                case SinkWriteOutcome.Skipped: s++; break;
                case SinkWriteOutcome.Failed:
                    f++;
                    var srcId = i < buffer.Count ? buffer[i].SourceId : "<?>";
                    await Log(runId, "Error", $"    Sink upsert failed for SourceId={srcId}: {r.ErrorMessage}");
                    break;
            }
        }
        if (asyncSubmitted > 0)
            await Log(runId, "Info", $"    Submitted {asyncSubmitted} async job(s); poller will advance them out-of-band.");
        return new FlushDelta(c, u, s, f);
    }

    /// <summary>
    /// Coerce a member-attribute value into a clean string list. Sources hand back
    /// different shapes: a single string, a List&lt;string&gt;, a List&lt;object?&gt;,
    /// or an object[] (AD multi-valued attributes arrive as object[]). Empties are
    /// dropped. Anything unparseable yields an empty list.
    /// </summary>
    private static List<string> CoerceToStringList(object? value)
    {
        var result = new List<string>();
        if (value is null) return result;

        if (value is string single)
        {
            if (!string.IsNullOrWhiteSpace(single)) result.Add(single);
            return result;
        }

        if (value is System.Collections.IEnumerable e && value is not byte[])
        {
            foreach (var item in e)
            {
                if (item is null) continue;
                var s = item as string ?? item.ToString();
                if (!string.IsNullOrWhiteSpace(s)) result.Add(s!);
            }
            return result;
        }

        var scalar = value.ToString();
        if (!string.IsNullOrWhiteSpace(scalar)) result.Add(scalar!);
        return result;
    }

    private static ConnectorObject ApplyMappings(ConnectorObject src, IReadOnlyList<AttributeMapping> mappings)
    {
        if (mappings.Count == 0) return src;

        var dst = new ConnectorObject
        {
            SourceId = src.SourceId,
            ObjectClass = src.ObjectClass
        };

        foreach (var m in mappings)
        {
            object? value = null;
            var hasValue = src.Attributes.TryGetValue(m.SourceAttribute, out value);

            if (!string.IsNullOrWhiteSpace(m.TransformExpr))
            {
                value = AttributeTransformer.Apply(m.TransformExpr!, value);
                hasValue = value is not null || AttributeTransformer.ProducesValueWhenSourceMissing(m.TransformExpr!);
            }

            if (hasValue)
            {
                dst.Attributes[m.SinkAttribute] = value;
            }
            else if (m.IsRequired)
            {
                dst.Attributes[m.SinkAttribute] = null;
            }
        }

        foreach (var fallback in new[] { "userName", "UserName", "sAMAccountName" })
        {
            if (!dst.Attributes.ContainsKey(fallback) && src.Attributes.TryGetValue(fallback, out var v))
                dst.Attributes[fallback] = v;
        }

        return dst;
    }

    private Task Log(Guid runId, string level, string message) =>
        _runRepo.AppendLogAsync(runId, level, message);
}
