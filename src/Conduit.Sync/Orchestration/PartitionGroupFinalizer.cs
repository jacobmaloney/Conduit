using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Core.SyncModels;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Microsoft.Extensions.Configuration;

namespace Conduit.Sync.Orchestration;

/// <summary>What delete detection across the containers of one split read does (config <c>Sync:PartitionDeleteDetection</c>).</summary>
public enum PartitionDeleteDetectionMode
{
    /// <summary>Nothing is recorded or computed; a split read is upsert-only, as before.</summary>
    Off,
    /// <summary>The default: everything is computed and logged, nothing is emitted or pruned.</summary>
    ReportOnly,
    /// <summary>Disappeared ids are emitted through the sink's tombstone contract and pruned from the registry.</summary>
    Enforce,
}

/// <summary>The configuration of delete detection across a split read.</summary>
public sealed record PartitionDeleteDetectionSettings(PartitionDeleteDetectionMode Mode, int MaxDeletePercent)
{
    public const string ModeKey = "Sync:PartitionDeleteDetection";
    public const string MaxPercentKey = "Sync:PartitionDeleteMaxPercent";
    public const int DefaultMaxDeletePercent = 10;

    /// <summary>
    /// Read once per run. A missing or unrecognised mode is ReportOnly (the safe default: compute, never emit), and so is
    /// a number or a comma list (Enum.TryParse reads "Off,Enforce" as flags): only a single name counts. A missing or out-of-range percentage is 10.
    /// </summary>
    public static PartitionDeleteDetectionSettings Read(IConfiguration? config)
    {
        var text = config?[ModeKey]?.Trim();
        var mode = !string.IsNullOrEmpty(text) && !char.IsDigit(text[0]) && text[0] != '-' && text.IndexOf(',') < 0
                   && Enum.TryParse<PartitionDeleteDetectionMode>(text, ignoreCase: true, out var parsed)
                   && Enum.IsDefined(parsed)
            ? parsed
            : PartitionDeleteDetectionMode.ReportOnly;
        var percent = int.TryParse(config?[MaxPercentKey], out var p) && p is >= 1 and <= 100 ? p : DefaultMaxDeletePercent;
        return new PartitionDeleteDetectionSettings(mode, percent);
    }
}

/// <summary>
/// One finalization attempt: the group, the settings, and the run's handles to the sink and the step registry. The
/// delegates are the orchestrator's existing calls (SinkRecordHashRepository load and prune under <c>step:{id}</c>, the
/// run log), passed in so the gates are tested without a database or a connector.
/// </summary>
public sealed record PartitionGroupFinalizeRequest(
    Guid ProjectId,
    Guid GroupId,
    PartitionDeleteDetectionSettings Settings,
    ITombstoneEmittingSink? Sink,
    string Source,
    Func<Guid, Task<IReadOnlyCollection<string>>> LoadRegistryAsync,
    Func<Guid, IReadOnlyCollection<string>, Task> PruneRegistryAsync,
    Func<string, string, Task> LogAsync,
    IReadOnlyDictionary<Guid, string>? StepNames = null);

/// <summary>How a group, or one step of it, came out.</summary>
public static class PartitionGroupOutcomes
{
    public const string Off = "Off";
    public const string Waiting = "Waiting";
    public const string AlreadyFinalized = "AlreadyFinalized";
    public const string Cancelled = "Cancelled";
    public const string PartitionIncomplete = "PartitionIncomplete";
    public const string Finalized = "Finalized";
    // Per step.
    public const string Baseline = "Baseline";
    public const string Rebaselined = "Rebaselined";
    public const string NoPrior = "NoPrior";
    public const string NoChange = "NoChange";
    public const string RefusedEmptyRead = "RefusedEmptyRead";
    public const string RefusedOverCap = "RefusedOverCap";
    public const string Reported = "Reported";
    public const string Emitted = "Emitted";
    public const string EmitFailed = "EmitFailed";
}

public sealed record PartitionStepOutcome(Guid StepId, string Outcome, int Prior, int Seen, IReadOnlyList<string> Disappeared);

public sealed record PartitionGroupResult(string Outcome, IReadOnlyList<PartitionStepOutcome> Steps)
{
    public static PartitionGroupResult Of(string outcome) => new(outcome, Array.Empty<PartitionStepOutcome>());
}

/// <summary>
/// UC017-F47: delete detection across the containers of one split read. No container of a split read may tombstone on
/// its own (its read is a slice of the scope); the group of one plan, IdentityCenter's ParentJobId, together is a full
/// read. Every partition run ends here. Until the group has a result for each of its N containers this waits; then one
/// caller claims the group's finalization row (exactly once) and, per delete-detected step, diffs the step's baseline
/// against the union of what the group read.
///
/// The prior set is the BASELINE (the ids the last complete group of the same plan read) intersected with the step's
/// <c>step:{id}</c> registry. Both bounds matter: the registry alone can hold ids no container covers (legacy rows with
/// no class match any key; a whole read of the scope before it was split also reached containers the planner leaves
/// out), and an id the registry does not hold was never synced to the sink. So an id is only ever tombstoned if some
/// container of the same plan read it last time, the sink holds it, and no container read it this time.
///
/// Gates, every one of which leaves the sink untouched: a failed, partial, incremental or missing partition; a changed
/// plan (container list fingerprint) re-baselines; an empty prior set; a group that read nothing while the prior set is
/// not empty; more disappeared ids than max(5, N% of prior); cancellation; mode ReportOnly (computes and logs only) or
/// Off. The baseline advances only on a clean outcome, so a refusal repeats until someone looks at it.
/// </summary>
public sealed class PartitionGroupFinalizer
{
    public const int MinimumDeleteCap = 5;
    public const int SampleSize = 20;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    private readonly IPartitionGroupRepository _store;

    public PartitionGroupFinalizer(IPartitionGroupRepository store) => _store = store;

    /// <summary>The descriptor a partition records of its container; the plan fingerprint is built from these.</summary>
    public static string ContainerKey(SyncRunPartition partition) =>
        $"{partition.Index}/{partition.Count}|{(partition.OneLevel ? "one" : "subtree")}|"
        + string.Join(";", partition.IncludedBaseDNs.Select(Canonical));

    /// <summary>SHA-256 (hex) of the plan's containers in index order: a different container list is a different plan.</summary>
    public static string PlanFingerprint(IEnumerable<PartitionRunResult> results)
    {
        var keys = results
            .GroupBy(r => r.PartitionIndex)
            .OrderBy(g => g.Key)
            .Select(g => g.First().ContainerKey);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", keys))));
    }

    /// <summary>The cap on one step's tombstones in one group: max(5, percent of the prior set, rounded up).</summary>
    public static int DeleteCap(int priorCount, int percent) =>
        Math.Max(MinimumDeleteCap, (int)Math.Ceiling(priorCount * (percent / 100.0)));

    public async Task<PartitionGroupResult> FinalizeAsync(PartitionGroupFinalizeRequest request, CancellationToken ct)
    {
        var log = request.LogAsync;
        if (request.Settings.Mode == PartitionDeleteDetectionMode.Off)
            return PartitionGroupResult.Of(PartitionGroupOutcomes.Off);
        if (ct.IsCancellationRequested)
        {
            await log("Warning", "    Delete detection across the plan SKIPPED: run cancellation requested.");
            return PartitionGroupResult.Of(PartitionGroupOutcomes.Cancelled);
        }

        var results = await _store.GetResultsAsync(request.GroupId);
        if (results.Count == 0) return PartitionGroupResult.Of(PartitionGroupOutcomes.Waiting);
        var count = results[0].PartitionCount;
        var indexes = results.Select(r => r.PartitionIndex).Distinct().Count();
        var bad = results.FirstOrDefault(r => !r.Succeeded || !r.AllStepsCompleteRead || r.PartitionCount != count
                                               || r.PartitionIndex < 1 || r.PartitionIndex > count);
        if (bad is null && indexes < count)
        {
            await log("Info", $"    Delete detection across the plan waits for {count - indexes} more of {count} container(s). A container that runs on a Conduit server with its own database never reports here, so its plan is not delete-detected.");
            return PartitionGroupResult.Of(PartitionGroupOutcomes.Waiting);
        }

        var fingerprint = PlanFingerprint(results);
        if (!await _store.TryClaimAsync(request.GroupId, request.ProjectId, fingerprint))
        {
            await log("Info", "    Delete detection across the plan: another container already finalized this plan. Nothing to do.");
            return PartitionGroupResult.Of(PartitionGroupOutcomes.AlreadyFinalized);
        }

        if (bad is not null)
        {
            var reason = !bad.Succeeded
                ? $"container {bad.PartitionIndex} of {count} did not succeed"
                : !bad.AllStepsCompleteRead
                    ? $"container {bad.PartitionIndex} of {count} did not read completely (truncated, incremental or cancelled)"
                    : "the containers disagree on the size of the plan";
            await log("Warning", $"    Delete detection across the plan SKIPPED: {reason}. Upsert-only; the next complete plan is compared with the last complete one.");
            await _store.ReleaseSeenAsync(request.GroupId, Array.Empty<Guid>());
            await _store.CompleteAsync(request.GroupId, PartitionGroupOutcomes.PartitionIncomplete, Detail(reason, Array.Empty<PartitionStepOutcome>()));
            return PartitionGroupResult.Of(PartitionGroupOutcomes.PartitionIncomplete);
        }

        // A step takes part only when every container recorded its read; a step some container did not arm is not
        // a complete read of the scope.
        var armed = results
            .Select(r => (IEnumerable<Guid>)r.ArmedSteps())
            .Aggregate((a, b) => a.Intersect(b))
            .Distinct()
            .ToList();

        var outcomes = new List<PartitionStepOutcome>();
        var keep = new List<Guid>();
        foreach (var stepId in armed)
        {
            if (ct.IsCancellationRequested)
            {
                await log("Warning", "    Delete detection across the plan stopped: run cancellation requested. Remaining steps were not compared.");
                await _store.ReleaseSeenAsync(request.GroupId, keep);
                await _store.CompleteAsync(request.GroupId, PartitionGroupOutcomes.Cancelled, Detail("cancelled", outcomes));
                return new PartitionGroupResult(PartitionGroupOutcomes.Cancelled, outcomes);
            }
            var outcome = await FinalizeStepAsync(request, stepId, fingerprint, count, ct);
            outcomes.Add(outcome);
            if (Advances(outcome.Outcome))
            {
                await _store.AdvanceBaselineAsync(request.ProjectId, stepId, request.GroupId, fingerprint);
                keep.Add(stepId);
            }
        }

        await _store.ReleaseSeenAsync(request.GroupId, keep);
        await _store.CompleteAsync(request.GroupId, PartitionGroupOutcomes.Finalized, Detail(request.Settings.Mode.ToString(), outcomes));
        try
        {
            var purged = await _store.PurgeStaleAsync(DateTime.UtcNow - StaleAfter);
            if (purged > 0)
                await log("Info", $"    Delete detection across the plan: removed {purged} record(s) of plans older than {StaleAfter.TotalDays:0} days that never finished or were replaced.");
        }
        catch (Exception ex)
        {
            await log("Warning", $"    Could not remove stale plan records: {ex.Message}");
        }
        return new PartitionGroupResult(PartitionGroupOutcomes.Finalized, outcomes);
    }

    private static bool Advances(string outcome) => outcome is PartitionGroupOutcomes.Baseline
        or PartitionGroupOutcomes.Rebaselined or PartitionGroupOutcomes.NoPrior
        or PartitionGroupOutcomes.NoChange or PartitionGroupOutcomes.Emitted;

    private async Task<PartitionStepOutcome> FinalizeStepAsync(
        PartitionGroupFinalizeRequest request, Guid stepId, string fingerprint, int count, CancellationToken ct)
    {
        var log = request.LogAsync;
        var seen = await _store.LoadSeenAsync(request.GroupId, stepId);
        var label = request.StepNames is not null && request.StepNames.TryGetValue(stepId, out var name)
            ? $"step '{name}'"
            : $"step {stepId:N}";
        PartitionStepOutcome Result(string outcome, int prior, IReadOnlyList<string>? gone = null) =>
            new(stepId, outcome, prior, seen.Count, gone ?? Array.Empty<string>());

        var baseline = await _store.GetBaselineAsync(request.ProjectId, stepId);
        if (baseline is null)
        {
            await log("Info", $"    Delete detection across the plan ({label}): first complete read of this plan ({count} containers, {seen.Count} id(s)). Recorded as the baseline; nothing to compare with yet.");
            return Result(PartitionGroupOutcomes.Baseline, 0);
        }
        if (!string.Equals(baseline.PlanFingerprint, fingerprint, StringComparison.Ordinal))
        {
            await log("Warning", $"    Delete detection across the plan ({label}): the container list changed since the last complete read. Re-baselined on this read ({count} containers, {seen.Count} id(s)); nothing tombstoned.");
            return Result(PartitionGroupOutcomes.Rebaselined, 0);
        }

        var previous = await _store.LoadSeenAsync(baseline.GroupId, stepId);
        var registry = await request.LoadRegistryAsync(stepId);
        var prior = new HashSet<string>(previous.Where(registry.Contains), StringComparer.Ordinal);
        if (prior.Count == 0)
        {
            await log("Info", $"    Delete detection across the plan ({label}): no prior id set the sink holds. Nothing to compare; this read is the new baseline.");
            return Result(PartitionGroupOutcomes.NoPrior, 0);
        }
        if (seen.Count == 0)
        {
            await log("Error", $"    Delete detection across the plan REFUSED ({label}): every container reported a complete read but together they saw 0 objects while {prior.Count} prior id(s) exist. Treated as a suspect read; nothing tombstoned. Investigate the source bind or filter.");
            return Result(PartitionGroupOutcomes.RefusedEmptyRead, prior.Count);
        }

        var disappeared = prior.Where(id => !seen.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
        if (disappeared.Count == 0)
        {
            await log("Info", $"    Delete detection across the plan ({label}): all {count} containers read completely; no disappeared records of {prior.Count} prior.");
            return Result(PartitionGroupOutcomes.NoChange, prior.Count);
        }

        var cap = DeleteCap(prior.Count, request.Settings.MaxDeletePercent);
        var sample = string.Join(", ", disappeared.Take(SampleSize));
        if (disappeared.Count > cap)
        {
            await log("Error", $"    Delete detection across the plan REFUSED ({label}): {disappeared.Count} of {prior.Count} prior id(s) disappeared, over the limit of {cap} (max(5, {request.Settings.MaxDeletePercent}% of prior)). Nothing tombstoned; the comparison repeats on the next plan. Sample: {sample}");
            return Result(PartitionGroupOutcomes.RefusedOverCap, prior.Count, disappeared);
        }

        if (request.Settings.Mode != PartitionDeleteDetectionMode.Enforce)
        {
            await log("Warning", $"    Delete detection across the plan ({label}, report only): would tombstone {disappeared.Count} of {prior.Count} prior id(s). Nothing emitted or pruned. Sample: {sample}");
            return Result(PartitionGroupOutcomes.Reported, prior.Count, disappeared);
        }
        if (request.Sink is null)
        {
            await log("Warning", $"    Delete detection across the plan ({label}): the sink does not support the soft-delete contract; {disappeared.Count} disappeared id(s) not emitted.");
            return Result(PartitionGroupOutcomes.EmitFailed, prior.Count, disappeared);
        }
        if (ct.IsCancellationRequested)
        {
            await log("Warning", "    Delete detection across the plan SKIPPED: run cancellation requested.");
            return Result(PartitionGroupOutcomes.EmitFailed, prior.Count, disappeared);
        }

        // The same emission and prune the whole-read path makes (SyncProjectOrchestrator, Phase 2.2), scoped to the
        // step's own registry key.
        await log("Warning", $"    Delete detection across the plan ({label}): all {count} containers read completely; {disappeared.Count} disappeared record(s) of {prior.Count} prior; emitting to sink (IC enforces a 50% cap as the backstop). Sample: {sample}");
        try
        {
            var emit = await request.Sink.EmitTombstonesAsync(request.Source, disappeared, ct);
            var clean = emit.Succeeded && !emit.Aborted;
            if (!emit.Succeeded)
                await log("Error", $"    Tombstone emission FAILED: {emit.ErrorMessage}. No prune; will retry on the next complete plan.");
            else if (emit.Aborted)
                await log("Error", $"    Tombstone emission ABORTED by sink safety cap: {emit.AbortReason} (requested={emit.Requested}, matched={emit.Matched}). Records NOT deleted; NOT pruned.");
            else
                await log("Info", $"    Tombstones applied: requested={emit.Requested}, matched={emit.Matched}, softDeleted={emit.SoftDeleted}.");

            if (emit.PrunableIds.Count > 0)
            {
                try
                {
                    await request.PruneRegistryAsync(stepId, emit.PrunableIds);
                    await log("Info", $"    Pruned {emit.PrunableIds.Count} tombstoned id(s) from the sink hash registry ({label}).");
                }
                catch (Exception ex)
                {
                    await log("Warning", $"    Could not prune tombstoned ids from hash registry: {ex.Message}");
                }
            }
            return Result(clean ? PartitionGroupOutcomes.Emitted : PartitionGroupOutcomes.EmitFailed, prior.Count, disappeared);
        }
        catch (OperationCanceledException)
        {
            await log("Warning", "    Tombstone emission cancelled.");
            return Result(PartitionGroupOutcomes.EmitFailed, prior.Count, disappeared);
        }
        catch (Exception ex)
        {
            await log("Error", $"    Tombstone emission threw: {ex.Message}. Upsert results stand; no deletes pruned.");
            return Result(PartitionGroupOutcomes.EmitFailed, prior.Count, disappeared);
        }
    }

    private static string Detail(string note, IReadOnlyList<PartitionStepOutcome> steps) =>
        JsonSerializer.Serialize(new
        {
            note,
            steps = steps.Select(s => new
            {
                stepId = s.StepId.ToString("N"),
                outcome = s.Outcome,
                prior = s.Prior,
                seen = s.Seen,
                disappeared = s.Disappeared.Count,
                sample = s.Disappeared.Take(SampleSize),
            }),
        });

    private static string Canonical(string dn) =>
        string.Join(",", (dn ?? string.Empty).Split(',').Select(part => part.Trim())).ToLowerInvariant();
}
