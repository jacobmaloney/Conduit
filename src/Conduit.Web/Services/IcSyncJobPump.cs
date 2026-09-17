using System.Text;
using System.Text.Json;
using Conduit.Core.SyncModels;

namespace Conduit.Web.Services;

/// <summary>
/// Every refusal this executor can report, as one named reason each. A job that is not run is
/// completed UNSUCCESSFULLY carrying the matching sentence — never dropped, never quietly
/// re-queued, and never completed as a success with zero counts.
/// </summary>
public static class IcSyncJobReasons
{
    public static string Unbound(Guid identityCenterProjectId) =>
        $"No Conduit sync project is bound to IdentityCenter project {identityCenterProjectId}; bind it on this installation before pinning work here.";

    public static string MissingRelatedEntity(Guid jobId) =>
        $"IdentityCenter job {jobId} names no related entity, so no Conduit sync project can be resolved; nothing was executed.";

    public static string WrongRelatedEntityType(Guid jobId, string? relatedEntityType) =>
        $"IdentityCenter job {jobId} is related to '{relatedEntityType}', not SyncProject; nothing was executed.";

    public static string WrongJobType(Guid jobId, string jobType) =>
        $"IdentityCenter job {jobId} is of type '{jobType}', which this installation did not ask for and will not run.";

    public static string BindingMismatch(Guid jobId, Guid identityCenterProjectId, SyncProject project) =>
        $"IdentityCenter job {jobId} names project {identityCenterProjectId} but the resolved Conduit project '{project.Name}' ({project.Id}) is bound to {project.IdentityCenterProjectId?.ToString() ?? "nothing"}; nothing was executed.";

    public static string Disabled(Guid identityCenterProjectId, SyncProject project) =>
        $"Conduit sync project '{project.Name}' ({project.Id}) is bound to IdentityCenter project {identityCenterProjectId} but is disabled on this installation; enable it there before pinning work here.";

    public static string AlreadyRunning(Guid jobId, SyncProject project) =>
        $"Conduit sync project '{project.Name}' ({project.Id}) already has a run in progress on this installation; IdentityCenter job {jobId} was not started.";

    public static string AdmissionLost(SyncProject project) =>
        $"Conduit sync project '{project.Name}' ({project.Id}) lost the single-run admission swap: another run started for it first, so this job executed nothing.";

    public static string PinnedElsewhere(Guid jobId, Guid targetServerId, Guid agentId) =>
        $"IdentityCenter job {jobId} is pinned to execution server {targetServerId}, not to this installation ({agentId}); nothing was executed.";

    public static string ExecutionFailed(Guid jobId, string detail) =>
        $"Conduit could not execute IdentityCenter job {jobId}: {detail}";

    public static string NoSyncProjectSupport(string? supportedJobTypes) =>
        $"IdentityCenter's agent row for this installation declares SupportedJobTypes '{supportedJobTypes}', which does not include SyncProject — no sync job will EVER be handed to it until that row is changed.";
}

/// <summary>What one pump cycle did, so the caller can choose its next cadence honestly.</summary>
public enum IcSyncJobTickResult
{
    NoWork,
    Executed,
    Refused,
    ChannelRejected,
    ChannelUnavailable
}

/// <summary>
/// One claim→resolve→execute→complete cycle against one IdentityCenter endpoint.
///
/// Holds the whole decision: a claimed job is resolved through the SYNC-SERVICE-05 binding
/// (<c>SyncProjects.IdentityCenterProjectId</c>) and either runs the ONE project bound to it or
/// is completed unsuccessfully with a named reason. <see cref="IIcSyncProjectJobRunner.ExecuteAsync"/>
/// is unreachable on every refusal path, so wrong-project execution is not a matter of care.
///
/// Tenancy: an IdentityCenter job payload carries the submitting tenant (TenantId/TenantSlug).
/// A Conduit installation is single-tenant — it has exactly one set of connected systems — so the
/// tenant travels for ATTRIBUTION only and is logged, never used to select data. One Conduit
/// installation serving two IdentityCenter tenants is not supported by this slice.
/// </summary>
public sealed class IcSyncJobPump
{
    /// <summary>IdentityCenter's RelatedEntityType for a sync job; SyncSubmissionService stamps the job type into it.</summary>
    public const string SyncProjectEntityType = "SyncProject";

    private static readonly TimeSpan CapabilityRecheckInterval = TimeSpan.FromMinutes(30);

    private readonly IIcSyncProjectJobRunner _runner;
    private readonly IcAgentStatusService _status;
    private readonly ILogger<IcSyncJobPump> _logger;

    /// <summary>
    /// Per-endpoint "already said this once" memory. The hosted service visits endpoints
    /// sequentially, so there is one caller today; the lock is here so a second one cannot
    /// silently corrupt these collections later.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>Endpoints whose job channel refused this key / is not deployed — logged once each.</summary>
    private readonly HashSet<string> _channelLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Endpoints whose agent row does not list SyncProject — logged once each.</summary>
    private readonly HashSet<string> _capabilityWarned = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, DateTime> _capabilityCheckedAt = new(StringComparer.OrdinalIgnoreCase);

    private bool FirstTimeSaying(HashSet<string> log, string baseUrl)
    {
        lock (_gate) return log.Add(baseUrl);
    }

    private void SaidNothingToSay(HashSet<string> log, string baseUrl)
    {
        lock (_gate) log.Remove(baseUrl);
    }

    private bool CapabilityCheckDue(string baseUrl)
    {
        lock (_gate)
        {
            if (_capabilityCheckedAt.TryGetValue(baseUrl, out var last) && DateTime.UtcNow - last < CapabilityRecheckInterval)
                return false;
            _capabilityCheckedAt[baseUrl] = DateTime.UtcNow;
            return true;
        }
    }

    public IcSyncJobPump(IIcSyncProjectJobRunner runner, IcAgentStatusService status, ILogger<IcSyncJobPump> logger)
    {
        _runner = runner;
        _status = status;
        _logger = logger;
    }

    public async Task<IcSyncJobTickResult> RunOnceAsync(IcSyncJobChannel channel, string baseUrl, Guid agentId, CancellationToken ct)
    {
        await CheckSyncProjectSupportAsync(channel, baseUrl, agentId, ct);

        var claim = await channel.ClaimAsync(baseUrl, agentId, ct);
        // IdentityCenter's message is remote text: it reaches a log template and the status
        // object, so it is stripped and capped before either sees it.
        var claimMessage = Clean(claim.Message);
        _status.Update(baseUrl, s =>
        {
            s.LastClaimUtc = DateTime.UtcNow;
            s.LastClaimOutcome = claim.Result == IcClaimResult.Claimed
                ? $"Claimed job {claim.Job!.Id}"
                : $"{claim.Result}: {claimMessage}";
        });

        switch (claim.Result)
        {
            case IcClaimResult.Rejected:
                if (FirstTimeSaying(_channelLogged, baseUrl))
                    _logger.LogWarning(
                        "IdentityCenter job queue at {BaseUrl} refused this installation's key ({Message}). IdentityCenter's AgentPolicy requires a scope=agent claim; an enrolled Conduit key carries agent:commands + agent:heartbeat. No pinned sync job can be claimed until that is resolved in IdentityCenter.",
                        baseUrl, claimMessage);
                return IcSyncJobTickResult.ChannelRejected;

            case IcClaimResult.NotDeployed:
                if (FirstTimeSaying(_channelLogged, baseUrl))
                    _logger.LogInformation("IdentityCenter at {BaseUrl} does not expose the job queue ({Message}); nothing pinned here can be executed.", baseUrl, claimMessage);
                return IcSyncJobTickResult.ChannelUnavailable;

            case IcClaimResult.Unavailable:
                _logger.LogDebug("IdentityCenter job claim at {BaseUrl}: {Message}; retrying next tick.", baseUrl, claimMessage);
                return IcSyncJobTickResult.ChannelUnavailable;

            case IcClaimResult.NoWork:
                SaidNothingToSay(_channelLogged, baseUrl);
                return IcSyncJobTickResult.NoWork;

            default:
                SaidNothingToSay(_channelLogged, baseUrl);
                return await HandleClaimedJobSafelyAsync(channel, baseUrl, agentId, claim.Job!, ct);
        }
    }

    /// <summary>
    /// A claimed job is IdentityCenter's, not ours: once the claim succeeds, EVERY path back to
    /// the caller must have completed it. Resolution hits SQL, so a pool timeout or a restarted
    /// database throws here as readily as the run itself does — and an escaped exception would
    /// leave the job Running at IdentityCenter forever with nothing but a swallowed tick to show.
    /// </summary>
    private async Task<IcSyncJobTickResult> HandleClaimedJobSafelyAsync(
        IcSyncJobChannel channel, string baseUrl, Guid agentId, IcJobQueueEntry job, CancellationToken ct)
    {
        try
        {
            return await HandleClaimedJobAsync(channel, baseUrl, agentId, job, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IdentityCenter job {JobId} threw before it could be completed; completing it as failed.", job.Id);
            await CompleteAsync(channel, baseUrl, agentId, job, success: false, 0, 0, 0,
                IcSyncJobReasons.ExecutionFailed(job.Id, Clean(ex.Message)), null, CancellationToken.None);
            return IcSyncJobTickResult.Refused;
        }
    }

    private async Task<IcSyncJobTickResult> HandleClaimedJobAsync(
        IcSyncJobChannel channel, string baseUrl, Guid agentId, IcJobQueueEntry job, CancellationToken ct)
    {
        // Remote text reaching a log template is stripped and capped; the tenant is attribution only.
        var tenant = Clean(ReadPayload(job.PayloadJson, "TenantSlug") ?? ReadPayload(job.PayloadJson, "TenantId"), 80);
        _logger.LogInformation("Claimed IdentityCenter job {JobId} ({JobType}) from {BaseUrl} for IdentityCenter project {ProjectId}{Tenant}.",
            job.Id, Clean(job.JobType, 50), baseUrl, job.RelatedEntityId,
            string.IsNullOrEmpty(tenant) ? string.Empty : $" (tenant {tenant})");

        // The pin is IdentityCenter's control and it enforces it server-side; this is the second
        // half of it. A job stamped for another execution server is refused, not run, even if
        // IdentityCenter handed it over.
        if (job.TargetServerId is Guid target && target != Guid.Empty && target != agentId)
            return await RefuseAsync(channel, baseUrl, agentId, job, IcSyncJobReasons.PinnedElsewhere(job.Id, target, agentId), ct);

        if (!string.Equals(job.JobType, IcSyncJobChannel.SyncProjectJobType, StringComparison.OrdinalIgnoreCase))
            return await RefuseAsync(channel, baseUrl, agentId, job, IcSyncJobReasons.WrongJobType(job.Id, Clean(job.JobType, 50)), ct);

        if (job.RelatedEntityId is not Guid icProjectId || icProjectId == Guid.Empty)
            return await RefuseAsync(channel, baseUrl, agentId, job, IcSyncJobReasons.MissingRelatedEntity(job.Id), ct);

        if (!string.IsNullOrWhiteSpace(job.RelatedEntityType)
            && !string.Equals(job.RelatedEntityType, SyncProjectEntityType, StringComparison.OrdinalIgnoreCase))
            return await RefuseAsync(channel, baseUrl, agentId, job, IcSyncJobReasons.WrongRelatedEntityType(job.Id, Clean(job.RelatedEntityType, 50)), ct);

        var project = await _runner.ResolveAsync(icProjectId);
        if (project is null)
            return await RefuseAsync(channel, baseUrl, agentId, job, IcSyncJobReasons.Unbound(icProjectId), ct);

        // The repository query selects on the binding, so this can only fire if that
        // contract ever changed. It stays because "the right project" must be checked,
        // not assumed, on the path that starts a real run.
        if (project.IdentityCenterProjectId != icProjectId)
            return await RefuseAsync(channel, baseUrl, agentId, job, IcSyncJobReasons.BindingMismatch(job.Id, icProjectId, project), ct);

        if (!project.IsEnabled)
            return await RefuseAsync(channel, baseUrl, agentId, job, IcSyncJobReasons.Disabled(icProjectId, project), ct);

        if (project.IsRunning)
            return await RefuseAsync(channel, baseUrl, agentId, job, IcSyncJobReasons.AlreadyRunning(job.Id, project), ct);

        return await ExecuteAsync(channel, baseUrl, agentId, job, project, ct);
    }

    private async Task<IcSyncJobTickResult> ExecuteAsync(
        IcSyncJobChannel channel, string baseUrl, Guid agentId, IcJobQueueEntry job, SyncProject project, CancellationToken ct)
    {
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cancelRequested = false;

        IcSyncRunOutcome outcome;
        try
        {
            outcome = await _runner.ExecuteAsync(project, TriggeredBy(job), async (progress, progressCt) =>
            {
                // IdentityCenter's ProgressPercent stays 0: a sync enumerates an unbounded source, so
                // a percentage would be invented. The counts and the message carry what is actually known.
                await channel.ReportProgressAsync(baseUrl, job.Id, agentId, 0, progress.Message,
                    progress.ItemsProcessed, progress.ItemsSucceeded, progress.ItemsFailed, progressCt);

                var fresh = await channel.ReadJobAsync(baseUrl, job.Id, progressCt);
                if (fresh?.CancellationRequested == true && !runCts.IsCancellationRequested)
                {
                    cancelRequested = true;
                    _logger.LogInformation("IdentityCenter requested cancellation of job {JobId}; cancelling Conduit run {RunId}.", job.Id, progress.RunId);
                    runCts.Cancel();
                }
            }, runCts.Token);
        }
        catch (Exception ex)
        {
            // A claimed job is never left silent, whatever failed — including a failure to
            // even admit the run. The job is completed unsuccessfully with what is known.
            _logger.LogError(ex, "IdentityCenter job {JobId} threw outside the run's own stamping.", job.Id);
            outcome = new IcSyncRunOutcome(false, null, "Failed", 0, 0, 0,
                $"Conduit could not execute IdentityCenter job {job.Id} for project '{project.Name}' ({project.Id}): {ex.Message}", null);
        }

        // A run IdentityCenter asked to cancel is never a success, whatever the roll-up said:
        // a PartialSuccess reached while the cancel was still propagating is still a cancelled run.
        var success = outcome.Success && !cancelRequested;
        var message = cancelRequested
            ? $"Cancelled at IdentityCenter's request. {outcome.Message}"
            : outcome.Message;

        // CancellationToken.None: a host shutdown or a cancelled run must still report;
        // a claimed job is never left silent.
        await CompleteAsync(channel, baseUrl, agentId, job, success,
            outcome.ItemsProcessed, outcome.ItemsSucceeded, outcome.ItemsFailed,
            success ? null : message, outcome.ResultJson, CancellationToken.None);

        return IcSyncJobTickResult.Executed;
    }

    /// <summary>
    /// The one place a job is completed. The completion is an EXTERNAL effect: a non-2xx or a
    /// dropped connection means IdentityCenter still has the job Running, so the local status and
    /// log say "reported" or "NOT reported" — never "completed" on the strength of having tried.
    /// </summary>
    private async Task CompleteAsync(
        IcSyncJobChannel channel, string baseUrl, Guid agentId, IcJobQueueEntry job, bool success,
        int itemsProcessed, int itemsSucceeded, int itemsFailed, string? errorMessage, string? resultJson, CancellationToken ct)
    {
        var reported = await channel.CompleteAsync(baseUrl, job.Id, agentId, success,
            itemsProcessed, itemsSucceeded, itemsFailed, errorMessage, resultJson, ct);

        var verdict = success ? "Succeeded" : "Failed";
        var detail = errorMessage ?? "completed";
        _status.Update(baseUrl, s =>
        {
            s.LastJobUtc = DateTime.UtcNow;
            s.LastJobOutcome = reported
                ? $"{job.Id}: {verdict} — {detail}"
                : $"{job.Id}: {verdict} locally, but IdentityCenter did NOT accept the completion — {detail}";
        });

        if (!reported)
        {
            _logger.LogError(
                "IdentityCenter job {JobId} finished {Verdict} locally but the completion call was not accepted; IdentityCenter still shows the job as running. {Detail}",
                job.Id, verdict, detail);
            return;
        }

        if (success)
            _logger.LogInformation("IdentityCenter job {JobId} completed: {Message}", job.Id, detail);
        else
            _logger.LogWarning("IdentityCenter job {JobId} completed unsuccessfully: {Message}", job.Id, detail);
    }

    private async Task<IcSyncJobTickResult> RefuseAsync(
        IcSyncJobChannel channel, string baseUrl, Guid agentId, IcJobQueueEntry job, string reason, CancellationToken ct)
    {
        _logger.LogWarning("IdentityCenter job {JobId} refused: {Reason}", job.Id, reason);
        // CancellationToken.None for the same reason the execute path uses it: a refusal dropped
        // on host shutdown leaves the job claimed and silent.
        await CompleteAsync(channel, baseUrl, agentId, job, success: false,
            itemsProcessed: 0, itemsSucceeded: 0, itemsFailed: 0, errorMessage: reason, resultJson: null,
            CancellationToken.None);
        return IcSyncJobTickResult.Refused;
    }

    /// <summary>
    /// Advisory: an agent row that does not list SyncProject yields an empty queue forever, which is
    /// indistinguishable from "nothing pinned here" unless it is said out loud. Never gates a claim.
    /// </summary>
    private async Task CheckSyncProjectSupportAsync(IcSyncJobChannel channel, string baseUrl, Guid agentId, CancellationToken ct)
    {
        if (!CapabilityCheckDue(baseUrl)) return;

        var supported = await channel.ReadSupportedJobTypesAsync(baseUrl, agentId, ct);
        if (supported is null) return;   // could not read — say nothing rather than guess

        var supports = IcSyncJobChannel.SupportsSyncProject(supported);
        _status.Update(baseUrl, s =>
        {
            s.SupportedJobTypes = supported;
            s.ExecutesSyncProjects = supports;
        });

        if (!supports && FirstTimeSaying(_capabilityWarned, baseUrl))
            _logger.LogWarning("IdentityCenter at {BaseUrl}: {Reason}", baseUrl, IcSyncJobReasons.NoSyncProjectSupport(Clean(supported, 200)));
        else if (supports)
            SaidNothingToSay(_capabilityWarned, baseUrl);
    }

    /// <summary>
    /// Attribution for the Conduit run. The payload's TriggeredBy is IdentityCenter's actor label —
    /// it is kept, but always under the IdentityCenter prefix, because a bare name in Conduit's run
    /// history would read as a local trigger that never happened. Control characters are stripped and
    /// the label is capped: it is remote text that lands in a log line and a run row.
    /// </summary>
    public static string TriggeredBy(IcJobQueueEntry job)
    {
        var label = Sanitize(ReadPayload(job.PayloadJson, "TriggeredBy"));
        return string.IsNullOrEmpty(label)
            ? $"IdentityCenter job {job.Id}"
            : $"IdentityCenter:{label}";
    }

    /// <summary>
    /// Reads a payload key case-INSENSITIVELY, matching IdentityCenter's own SyncProjectJobHandler:
    /// a casing difference must not silently fall through to an invented default.
    /// </summary>
    public static string? ReadPayload(string? payloadJson, string key)
    {
        if (string.IsNullOrEmpty(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    private static string Sanitize(string? input) => Clean(input, 60);

    /// <summary>
    /// Strips control characters and caps length. EVERY remote string that reaches a log
    /// template, the status object, or the text sent back to IdentityCenter goes through
    /// here: a forged CRLF would otherwise write attacker-controlled log lines, and an
    /// unbounded field would write a megabyte of them.
    /// </summary>
    public static string Clean(string? input, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (c <= '\u001F' || c == '\u007F') continue;   // C0 controls incl. CR, LF, tab
            sb.Append(c);
        }
        var trimmed = sb.ToString().Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
