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
    private readonly ILogger<SyncProjectOrchestrator> _logger;

    public SyncProjectOrchestrator(
        SyncProjectRepository projectRepo,
        SyncRunRepository runRepo,
        TenantRepository tenantRepo,
        ConnectorRegistry connectors,
        SyncRunAsyncJobRepository asyncJobRepo,
        WorkflowRepository workflowRepo,
        ILogger<SyncProjectOrchestrator> logger)
    {
        _projectRepo = projectRepo;
        _runRepo = runRepo;
        _tenantRepo = tenantRepo;
        _connectors = connectors;
        _asyncJobRepo = asyncJobRepo;
        _workflowRepo = workflowRepo;
        _logger = logger;
    }

    /// <summary>
    /// Run a project once. Creates a SyncRun row, walks the workflow tree,
    /// updates counters per step, marks the run Succeeded or Failed at the end.
    /// Returns the SyncRun.Id so callers (UI, Quartz) can deep-link to history.
    /// </summary>
    public async Task<Guid> ExecuteAsync(Guid projectId, string triggeredBy, CancellationToken cancellationToken)
    {
        var project = await _projectRepo.GetByIdAsync(projectId)
            ?? throw new InvalidOperationException($"SyncProject {projectId} not found.");

        var run = await _runRepo.CreateAsync(new SyncRun
        {
            SyncProjectId = project.Id,
            Status = "Running",
            TriggeredBy = triggeredBy,
            StartedAt = DateTime.UtcNow
        });

        // No-op when the controller already won the IsRunning compare-and-swap
        // for a manual Run-Now (Worf HIGH-1). The scheduler path still flips
        // the row here on the first call.
        _ = await _projectRepo.SetRunningAsync(project.Id, run.Id);
        await Log(run.Id, "Info", $"Run started by {triggeredBy} for project '{project.Name}'.");

        var sw = Stopwatch.StartNew();
        var totals = new RunCounters();
        string status = "Succeeded";
        string? errorMessage = null;
        SyncCursor? newCursor = null;
        bool wasIncremental = false;

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
            errorMessage = ex.Message;
            _logger.LogError(ex, "Sync project {ProjectId} failed", project.Id);
            await Log(run.Id, "Error", $"Run failed: {ex.Message}");
        }
        finally
        {
            sw.Stop();
            await _runRepo.UpdateCountersAsync(run.Id, totals.Read, totals.Created, totals.Updated, totals.Skipped, totals.Failed);
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

        var pump = await PumpAsync(ctx, mappings, scope, ct);
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
        var pump = await PumpAsync(ctx, mappings, scope, ct);
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

        await Log(run, "Info",
            $"    Source={ctx.SourceTenant.Name} ({ctx.SourceTenant.SystemType}) → Sink={sinkTenant.Name} ({sinkTenant.SystemType}); ObjectClass={project.ObjectClass}; Mappings={mappings.Count}; BatchSize={batchSize}; Incremental={sourceAdapter.Capabilities.SupportsIncremental}.");

        var enumeration = await source.EnumerateAsync(project.ObjectClass, scope, priorCursor, ct);
        var wasIncremental = enumeration.IsIncremental;

        var buffer = new List<ConnectorObject>(Math.Min(batchSize, 256));
        var emitted = new List<ConnectorObject>(256);
        int read = 0, created = 0, updated = 0, skipped = 0, failed = 0;

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

            buffer.Add(sinkObj);
            emitted.Add(sinkObj);

            if (buffer.Count >= batchSize)
            {
                var delta = await FlushAsync(sink, buffer, run, project.Id, sinkTenant.Id, sinkAdapter.SystemType, ct);
                created += delta.Created; updated += delta.Updated; skipped += delta.Skipped; failed += delta.Failed;
                buffer.Clear();
            }

            if (read % 50 == 0)
            {
                // Note: top-level run counters are updated in the workflow loop's
                // post-step flush — local read counter is for periodic UI ticks.
                await _runRepo.UpdateCountersAsync(run, read, created, updated, skipped, failed);
            }
        }

        if (buffer.Count > 0)
        {
            var delta = await FlushAsync(sink, buffer, run, project.Id, sinkTenant.Id, sinkAdapter.SystemType, ct);
            created += delta.Created; updated += delta.Updated; skipped += delta.Skipped; failed += delta.Failed;
            buffer.Clear();
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

    /// <summary>
    /// Phase 2: flush an accumulated batch through the sink. Uses UpsertBatchAsync
    /// when the sink advertises SupportsBulk (single batch network call), else
    /// the default loops over UpsertAsync per record (identical to Phase 1).
    /// </summary>
    private readonly record struct FlushDelta(int Created, int Updated, int Skipped, int Failed);

    private async Task<FlushDelta> FlushAsync(
        IConnectorSink sink,
        List<ConnectorObject> buffer,
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
