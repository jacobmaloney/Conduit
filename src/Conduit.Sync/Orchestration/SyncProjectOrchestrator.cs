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
    private readonly SyncCancellationRegistry _cancellation;
    private readonly ILogger<SyncProjectOrchestrator> _logger;

    public SyncProjectOrchestrator(
        SyncProjectRepository projectRepo,
        SyncRunRepository runRepo,
        TenantRepository tenantRepo,
        ConnectorRegistry connectors,
        SyncRunAsyncJobRepository asyncJobRepo,
        WorkflowRepository workflowRepo,
        SinkRecordHashRepository hashRepo,
        SyncCancellationRegistry cancellation,
        ILogger<SyncProjectOrchestrator> logger)
    {
        _projectRepo = projectRepo;
        _runRepo = runRepo;
        _tenantRepo = tenantRepo;
        _connectors = connectors;
        _asyncJobRepo = asyncJobRepo;
        _workflowRepo = workflowRepo;
        _hashRepo = hashRepo;
        _cancellation = cancellation;
        _logger = logger;
    }

    /// <summary>
    /// Run a project once. Creates a SyncRun row, walks the workflow tree,
    /// updates counters per step, marks the run Succeeded or Failed at the end.
    /// Returns the SyncRun.Id so callers (UI, Quartz) can deep-link to history.
    /// </summary>
    public async Task<Guid> ExecuteAsync(Guid projectId, string triggeredBy, CancellationToken cancellationToken)
    {
        // Worf HIGH-1: the IsRunning flag must be released on EVERY exit path,
        // including the early throws that happen before a SyncRun row exists
        // (GetById null, CreateAsync, tenant resolution). The whole body runs
        // under a guard that clears the flag on any failure.
        //
        // Ownership semantics: the controller's manual Run-Now path pre-claims
        // IsRunning (CAS 0→1) before calling us, so SetRunningAsync below is a
        // no-op there but WE still own the release. The scheduler path does NOT
        // pre-claim, so SetRunningAsync below is the actual claim; if it returns
        // false the project was already running under another invocation and we
        // must NOT clear that other invocation's flag.
        SyncRun? run = null;
        bool ownsFlag = false;

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

            // Returns false when the controller already won the CAS for a manual
            // Run-Now (the row's already IsRunning=1) OR when a scheduled run
            // overlaps an in-flight run. Either way the flag is set; in the
            // pre-claimed manual case we own its release. We can't distinguish
            // "controller pre-claimed for me" from "someone else is running" via
            // the bool alone, so the controller path also releases as defense in
            // depth (see ApiV1SyncRunsController.StartRun).
            var won = await _projectRepo.SetRunningAsync(project.Id, run.Id);
            ownsFlag = true;
            _ = won;
            await Log(run.Id, "Info", $"Run started by {triggeredBy} for project '{project.Name}'.");

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
            else
            {
                // GetById/CreateAsync threw before we set the flag. Nothing was
                // stamped, but a stale flag from a prior crashed run could exist;
                // clear it defensively without touching run stats.
                await _projectRepo.ClearRunningAsync(projectId);
            }
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

            var source = sourceAdapter.CreateSource(sourceTenant.Id)
                ?? throw new InvalidOperationException($"Source tenant '{sourceTenant.Name}' ({sourceTenant.SystemType}) does not support source operations.");
            var sink = sinkAdapter.CreateSink(sinkTenant.Id)
                ?? throw new InvalidOperationException($"Sink tenant '{sinkTenant.Name}' ({sinkTenant.SystemType}) does not support sink operations.");

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

                    foreach (var step in steps)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await Log(run.Id, "Info", $"  Step '{step.Name}' [{step.StepType}] starting (ordinal {step.Ordinal}).");

                        StepResult stepResult;
                        try
                        {
                            stepResult = step.StepType switch
                            {
                                WorkflowStepTypes.Mapping            => await ExecuteMappingStepAsync(ctx, step, cancellationToken),
                                WorkflowStepTypes.PersonMatch        => await ExecutePersonMatchStepAsync(ctx, step, lastBatch, cancellationToken),
                                WorkflowStepTypes.PersonCreate       => await ExecutePersonCreateStepAsync(ctx, step, lastBatch, lastMatches, cancellationToken),
                                WorkflowStepTypes.AssignManager      => await ExecuteAssignManagerStepAsync(ctx, step, lastBatch, cancellationToken),
                                WorkflowStepTypes.AssignGroupOwner   => await ExecuteAssignGroupOwnerStepAsync(ctx, step, lastBatch, cancellationToken),
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

                        // Hoist incremental cursor only from a Mapping step.
                        if (stepResult.NewCursor is not null)
                        {
                            newCursor = stepResult.NewCursor;
                            wasIncremental = stepResult.WasIncremental;
                        }

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
            else if (anyMappingRan && totals.Read == 0)
            {
                // "Query never returned" sentinel normalization. A Mapping pass that
                // enumerated the source but read ZERO objects is almost always a
                // misconfiguration (bad BaseDN/filter, bind that silently returned
                // nothing) rather than a legitimately empty directory. Surface it as
                // a failure with a real reason instead of a silent green / 0-records
                // success — the long-standing false-success symptom.
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
            // Only persist the cursor if the run actually succeeded — otherwise next
            // run would silently skip data the failed run never wrote downstream.
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
    private async Task<StepResult> ExecuteMappingStepAsync(RunContext ctx, WorkflowStep step, CancellationToken ct)
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

        var pump = await PumpAsync(ctx, mappings, scope, objectClass, ct);
        return new StepResult
        {
            Delta = pump.Delta,
            NewCursor = pump.NewCursor,
            WasIncremental = pump.WasIncremental,
            EmittedBatch = pump.EmittedBatch
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
        // Legacy path (no Workflows): there are no steps, so the class is the project's.
        var pump = await PumpAsync(ctx, mappings, scope, ctx.Project.ObjectClass, ct);
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
        List<ConnectorObject> EmittedBatch);

    private async Task<PumpResult> PumpAsync(
        RunContext ctx,
        IReadOnlyList<AttributeMapping> mappings,
        SyncProjectScope scope,
        string objectClass,
        CancellationToken ct)
    {
        var run = ctx.RunId;
        var project = ctx.Project;
        var source = ctx.Source;
        var sink = ctx.Sink;
        var sourceAdapter = ctx.SourceAdapter;
        var sinkAdapter = ctx.SinkAdapter;
        var sinkTenant = ctx.SinkTenant;

        // Recover cursor from last successful run if source supports it.
        SyncCursor? priorCursor = null;
        if (sourceAdapter.Capabilities.SupportsIncremental)
        {
            var last = await _runRepo.GetLastSuccessfulAsync(project.Id);
            if (last is not null && !string.IsNullOrEmpty(last.Cursor))
            {
                priorCursor = new SyncCursor { Token = last.Cursor!, IssuedAt = last.StartedAt };
                await Log(run, "Info", $"    Incremental: resuming from cursor issued {last.StartedAt:o}.");
            }
            else
            {
                await Log(run, "Info", "    Incremental: no prior cursor — running full enumeration.");
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
        Dictionary<string, string> priorHashes = skipUnchanged
            ? await _hashRepo.LoadMapAsync(project.Id, sinkTenant.Id)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        if (skipUnchanged)
            await Log(run, "Info", $"    Skip-unchanged ON; loaded {priorHashes.Count} prior sink hash(es).");
        var writtenHashes = new List<KeyValuePair<string, string>>(256);

        // ── Phase 2.2 delete-detection setup ────────────────────────────────────
        // Tombstoning is opt-in by SINK CAPABILITY (only IC implements the reversible
        // soft-delete contract). It runs INDEPENDENTLY of skip-unchanged, so we load
        // the prior-synced id set even when skip-unchanged is off — otherwise a
        // project that doesn't opt into skip-unchanged could never detect leavers.
        //
        // The prior set is the SinkRecordHashes keys for THIS (project, sink) — that
        // table is uniquely keyed (SyncProjectId, SinkTenantId, ExternalId), so the
        // diff is inherently per-connection: no cross-project / cross-connection bleed.
        var tombstoneSink = sink as ITombstoneEmittingSink;
        var deleteDetectionOn = tombstoneSink is not null;
        HashSet<string> priorIdsForDelete;
        if (deleteDetectionOn && !skipUnchanged)
        {
            var map = await _hashRepo.LoadMapAsync(project.Id, sinkTenant.Id);
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
            await Log(run, "Info", $"    Delete-detection ARMED (sink supports tombstones); loaded {priorIdsForDelete.Count} prior id(s). Emission gated on a COMPLETE source read.");

        await Log(run, "Info",
            $"    Source={ctx.SourceTenant.Name} ({ctx.SourceTenant.SystemType}) → Sink={sinkTenant.Name} ({sinkTenant.SystemType}); ObjectClass={objectClass}; Mappings={mappings.Count}; BatchSize={batchSize}; Incremental={sourceAdapter.Capabilities.SupportsIncremental}; SkipUnchanged={skipUnchanged}.");

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
        int read = 0, created = 0, updated = 0, skipped = 0, failed = 0;

        var progress = Stopwatch.StartNew();

        await foreach (var sourceObj in enumeration.Objects.WithCancellation(ct))
        {
            read++;

            if (!sourceObj.Attributes.ContainsKey("_source"))
                sourceObj.Attributes["_source"] = sourceAdapter.SystemType;

            var sinkObj = ApplyMappings(sourceObj, mappings);

            if (!sinkObj.Attributes.ContainsKey("_source"))
                sinkObj.Attributes["_source"] = sourceAdapter.SystemType;

            if (!string.IsNullOrWhiteSpace(scope.BaseDN) && !sinkObj.Attributes.ContainsKey("targetOU"))
                sinkObj.Attributes["targetOU"] = scope.BaseDN;

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
                hash = ComputeContentHash(sinkObj);
                if (priorHashes.TryGetValue(sinkObj.SourceId, out var prev) && prev == hash)
                {
                    skipped++;
                    continue;
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

        // Persist hashes for records the sink accepted this run. Done once at the
        // end (bounded by the change set) so the next run can skip them.
        if (skipUnchanged && writtenHashes.Count > 0)
        {
            try { await _hashRepo.UpsertManyAsync(project.Id, sinkTenant.Id, writtenHashes); }
            catch (Exception ex) { await Log(run, "Warning", $"    Could not persist sink hashes: {ex.Message}"); }
        }

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
        //       almost always a silently-empty bind, not a real full depopulation).
        bool readWasComplete = false;
        if (deleteDetectionOn)
        {
            try { readWasComplete = enumeration.WasCompleteRead(); }
            catch { readWasComplete = false; }
        }

        if (deleteDetectionOn && seenSourceIds is not null && tombstoneSink is not null)
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
                    await Log(run, "Info", "    Tombstoning: complete read, no disappeared records. Nothing to delete.");
                }
                else
                {
                    // IC keys the soft-delete on the SAME Source string the IC sink
                    // stamps on its upserts (IdentityCenterSink.PostBatchAsync →
                    // Source = "Conduit"). Passing the same value makes IC resolve
                    // the SAME auto-seeded SourceConnectionId, which (plus IC's
                    // SourceConnectionId SQL guard) is what scopes the delete to this
                    // connection. If that upsert constant ever changes, change it here.
                    const string icUpsertSource = "Conduit";

                    await Log(run, "Warning",
                        $"    Tombstoning: complete read detected {disappeared.Count} disappeared record(s) of {priorIdsForDelete.Count} prior; emitting to sink (IC enforces a 50% cap as the backstop).");

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
                                await _hashRepo.DeleteManyAsync(project.Id, sinkTenant.Id, emit.PrunableIds);
                                await Log(run, "Info", $"    Pruned {emit.PrunableIds.Count} tombstoned id(s) from the sink hash registry.");
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

        // Maintain the per-connection prior-synced id REGISTRY. Delete-detection
        // diffs against SinkRecordHashes keys, so those keys must reflect "what we
        // last successfully synced to this sink" — INDEPENDENT of skip-unchanged.
        // When skip-unchanged is OFF we still register the ids the sink accepted
        // this run (with a sentinel hash) so the NEXT complete run can detect their
        // absence. When skip-unchanged is ON the writtenHashes above already did this.
        //
        // ONLY register on a COMPLETE read. A partial read holds a partial seen-set;
        // registering it would silently drop real ids from the prior set, degrading
        // delete-detection over repeated partial runs. Skipping registration on a
        // partial read keeps the last complete registry intact (safe-leaning: a real
        // leaver may go undetected for a cycle, but a present record is never wiped).
        if (deleteDetectionOn && readWasComplete && !skipUnchanged && seenSourceIds is not null && seenSourceIds.Count > 0)
        {
            // Register every id seen this run (present in source). Use a sentinel
            // hash — the value is irrelevant for delete-detection (only the key is),
            // and skip-unchanged is off so no one reads it back as a content hash.
            const string registryHash = "TOMBSTONE-REGISTRY-NO-CONTENT-HASH--------00";
            var registry = new List<KeyValuePair<string, string>>(seenSourceIds.Count);
            foreach (var id in seenSourceIds)
                registry.Add(new KeyValuePair<string, string>(id, registryHash));
            try { await _hashRepo.UpsertManyAsync(project.Id, sinkTenant.Id, registry); }
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
            emitted);
    }

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
    /// </summary>
    private static string ComputeContentHash(ConnectorObject obj)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append(obj.ObjectClass).Append(HashUnitSep);
        foreach (var key in obj.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
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
    /// Phase 1B mapping: rename + optional transform expression. SourceAttribute →
    /// TransformExpr → SinkAttribute. Transform DSL whitelist lives in
    /// <see cref="AttributeTransformer"/>. If no mappings are defined, the
    /// sourceObj is passed through unchanged.
    /// </summary>
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
