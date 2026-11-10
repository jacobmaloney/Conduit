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

    public static string SourceMissing(SyncProject project) =>
        $"Conduit sync project '{project.Name}' ({project.Id}) has no source connection any more; nothing was planned.";
    public static string NoContainers(SyncProject project, string systemType) =>
        $"Conduit sync project '{project.Name}' ({project.Id}) reads a {systemType} source, which has no containers to read one at a time; set the IdentityCenter project's Split to Whole scope.";
    public static string ContainerBrowseFailed(SyncProject project, string baseDn, string detail) =>
        $"Conduit sync project '{project.Name}' ({project.Id}) could not list the containers under {baseDn}: {detail}";
    public static string PlanNotAccepted(Guid jobId, string? detail) =>
        $"IdentityCenter did not accept the partition plan for job {jobId}: {detail ?? "no reason given"}; nothing was read.";
    public static string RanWhole(int containers) =>
        $"The scope has {containers} container{(containers == 1 ? string.Empty : "s")}, so the project ran whole.";
    public static string DefinitionUnavailable(Guid jobId, Guid identityCenterProjectId, string? detail) =>
        $"IdentityCenter project {identityCenterProjectId} has no mirror on this installation yet and its definition could not be read for job {jobId} ({detail ?? "no reason given"}); nothing was executed.";
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
    private readonly IIcProjectMirror? _mirror;
    private readonly IcMailJobRunner? _mail;
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

    /// <summary>Endpoints whose claim answered a server error — logged once until a claim is answered again.</summary>
    private readonly HashSet<string> _unavailableLogged = new(StringComparer.OrdinalIgnoreCase);

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

    public IcSyncJobPump(IIcSyncProjectJobRunner runner, IcAgentStatusService status, ILogger<IcSyncJobPump> logger,
        IcMailJobRunner? mail = null, IIcProjectMirror? mirror = null)
    {
        _runner = runner;
        _mirror = mirror;
        _mail = mail;
        _status = status;
        _logger = logger;
    }

    public async Task<IcSyncJobTickResult> RunOnceAsync(IcSyncJobChannel channel, string baseUrl, Guid agentId, CancellationToken ct)
    {
        await CheckSyncProjectSupportAsync(channel, baseUrl, agentId, ct);

        // JOB-PLACEMENT-03: with a mail sender this server also asks for the mail drain. IdentityCenter hands a
        // worker only work TARGETED at it, so this takes mail only when the mail job is placed here.
        var claim = await channel.ClaimAsync(baseUrl, agentId, ct, ClaimJobTypes);
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
                // CLAIM-SQL-01: IdentityCenter's claim answered 500 on every call for months and this said so only at
                // Debug. A server error is logged once per run of them; a transport blip stays at Debug.
                if (claimMessage.StartsWith("HTTP 5", StringComparison.Ordinal) && FirstTimeSaying(_unavailableLogged, baseUrl))
                    _logger.LogWarning("IdentityCenter job claim at {BaseUrl} answered {Message}; no job pinned here can be claimed until IdentityCenter answers. Retrying every tick.", baseUrl, claimMessage);
                else
                    _logger.LogDebug("IdentityCenter job claim at {BaseUrl}: {Message}; retrying next tick.", baseUrl, claimMessage);
                return IcSyncJobTickResult.ChannelUnavailable;

            case IcClaimResult.NoWork:
                SaidNothingToSay(_unavailableLogged, baseUrl);
                SaidNothingToSay(_channelLogged, baseUrl);
                return IcSyncJobTickResult.NoWork;

            default:
                SaidNothingToSay(_unavailableLogged, baseUrl);
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

        if (_mail is not null && string.Equals(job.JobType, IcSyncJobChannel.MailJobType, StringComparison.Ordinal))
            return await SendMailAsync(channel, baseUrl, agentId, job, ct);

        if (!string.Equals(job.JobType, IcSyncJobChannel.SyncProjectJobType, StringComparison.OrdinalIgnoreCase))
            return await RefuseAsync(channel, baseUrl, agentId, job, IcSyncJobReasons.WrongJobType(job.Id, Clean(job.JobType, 50)), ct);

        if (job.RelatedEntityId is not Guid icProjectId || icProjectId == Guid.Empty)
            return await RefuseAsync(channel, baseUrl, agentId, job, IcSyncJobReasons.MissingRelatedEntity(job.Id), ct);

        if (!string.IsNullOrWhiteSpace(job.RelatedEntityType)
            && !string.Equals(job.RelatedEntityType, SyncProjectEntityType, StringComparison.OrdinalIgnoreCase))
            return await RefuseAsync(channel, baseUrl, agentId, job, IcSyncJobReasons.WrongRelatedEntityType(job.Id, Clean(job.RelatedEntityType, 50)), ct);

        var project = await _runner.ResolveAsync(icProjectId);

        // ZERO-TOUCH-01 (UC038-D01): the IdentityCenter project is the one definition. With no local project bound,
        // its definition is read and mirrored here; with one the mirror created, the definition is read again and the
        // mirror rebuilt when it changed. A container item of a split run skips the read when its mirror exists: the
        // plan item already refreshed it. A definition that cannot be read leaves an existing mirror running as it is.
        if (_mirror is not null && (project is null || !IcPartitionPayload.HasPartition(job.PayloadJson)))
        {
            var answer = await channel.ReadProjectDefinitionAsync(baseUrl, job.Id, ct);
            if (answer.Definition is not null)
            {
                var mirrored = await _mirror.EnsureAsync(baseUrl, answer.Definition, project, ct);
                if (mirrored.Refusal is not null)
                    return await RefuseAsync(channel, baseUrl, agentId, job, mirrored.Refusal, ct);
                project = mirrored.Project;
                if (mirrored.Action is IcProjectMirrorOutcome.Created or IcProjectMirrorOutcome.Refreshed)
                    _logger.LogInformation("IdentityCenter job {JobId}: mirror of project {ProjectId} {Action} as '{Name}' ({LocalId})",
                        job.Id, icProjectId, mirrored.Action.ToLowerInvariant(), Clean(project?.Name, 100), project?.Id);
            }
            else if (project is null)
                return await RefuseAsync(channel, baseUrl, agentId, job, IcSyncJobReasons.DefinitionUnavailable(job.Id, icProjectId, Clean(answer.Reason)), ct);
            else
                _logger.LogWarning("IdentityCenter job {JobId}: the definition of project {ProjectId} could not be read ({Reason}); running the existing mirror '{Name}' as it is.",
                    job.Id, icProjectId, Clean(answer.Reason), Clean(project.Name, 100));
        }

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

        // SPLIT-ENUM-01: a plan item is planned here and its containers go back to IdentityCenter as one item each; a
        // container item runs the project reading that container alone; anything else runs the project whole. A
        // partition that does not parse is refused, never run as a whole read under a container's name.
        var partition = IcPartitionPayload.ReadPartition(job.PayloadJson);
        if (partition is null && IcPartitionPayload.HasPartition(job.PayloadJson))
            return await RefuseAsync(channel, baseUrl, agentId, job, IcSyncJobReasons.ExecutionFailed(job.Id, "the partition in the job payload could not be read"), ct);
        if (partition is null && IcPartitionPayload.IsPlan(job.PayloadJson))
            return await PlanAsync(channel, baseUrl, agentId, job, project, ct);
        return await ExecuteAsync(channel, baseUrl, agentId, job, project, partition, ct);
    }

    /// <summary>
    /// The plan item: list the containers, hand them to IdentityCenter, complete the item saying what was queued where.
    /// A scope with fewer than two containers runs whole here and says so; a source without containers, a failed
    /// browse or a plan IdentityCenter does not accept completes the item unsuccessfully with the reason.
    /// </summary>
    private async Task<IcSyncJobTickResult> PlanAsync(
        IcSyncJobChannel channel, string baseUrl, Guid agentId, IcJobQueueEntry job, SyncProject project, CancellationToken ct)
    {
        IcPartitionPlan plan;
        try
        {
            plan = await _runner.PlanPartitionsAsync(project, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await RefuseAsync(channel, baseUrl, agentId, job, IcSyncJobReasons.ContainerBrowseFailed(project, "its scope", Clean(ex.Message)), ct);
        }
        if (plan.Refusal is not null)
            return await RefuseAsync(channel, baseUrl, agentId, job, plan.Refusal, ct);
        if (plan.Partitions.Count < 2)
        {
            _logger.LogInformation("IdentityCenter job {JobId}: {Reason}", job.Id, IcSyncJobReasons.RanWhole(plan.Partitions.Count));
            await channel.ReportProgressAsync(baseUrl, job.Id, agentId, 0, IcSyncJobReasons.RanWhole(plan.Partitions.Count), 0, 0, 0, ct);
            return await ExecuteAsync(channel, baseUrl, agentId, job, project, null, ct);
        }

        var answer = await channel.PlanPartitionsAsync(baseUrl, job.Id, agentId, plan.Partitions, ct);
        if (!answer.Accepted)
            return await RefuseAsync(channel, baseUrl, agentId, job, IcSyncJobReasons.PlanNotAccepted(job.Id, Clean(answer.Reason)), ct);

        var perServer = answer.Children.GroupBy(c => c.ServerName).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key} x{g.Count()}");
        var summary = $"Split into {answer.Children.Count} containers: {string.Join(", ", perServer)}.";
        _logger.LogInformation("IdentityCenter job {JobId}: {Summary}", job.Id, summary);
        await channel.ReportProgressAsync(baseUrl, job.Id, agentId, 100, summary, 0, 0, 0, CancellationToken.None);
        await CompleteAsync(channel, baseUrl, agentId, job, success: true, 0, 0, 0, null,
            JsonSerializer.Serialize(new
            {
                resultMessage = summary,
                executor = "Conduit",
                conduitProjectId = project.Id,
                partitions = answer.Children.Count,
                children = answer.Children.Select(c => new { jobId = c.JobId, index = c.Index, label = c.Label, server = c.ServerName }).ToArray()
            }), CancellationToken.None);
        return IcSyncJobTickResult.Executed;
    }

    /// <summary>The job types this installation claims with.</summary>
    public IReadOnlyList<string> ClaimJobTypes => _mail is null
        ? new[] { IcSyncJobChannel.SyncProjectJobType }
        : new[] { IcSyncJobChannel.SyncProjectJobType, IcSyncJobChannel.MailJobType };

    private async Task<IcSyncJobTickResult> SendMailAsync(IcSyncJobChannel channel, string baseUrl, Guid agentId, IcJobQueueEntry job, CancellationToken ct)
    {
        var outcome = await _mail!.RunAsync(channel, baseUrl, agentId, job.Id, ct);
        _logger.LogInformation("IdentityCenter mail job {JobId}: {Summary}", job.Id, outcome.Message);
        await CompleteAsync(channel, baseUrl, agentId, job, outcome.Success, outcome.Processed, outcome.Sent, outcome.Failed,
            outcome.Success ? null : outcome.Message,
            JsonSerializer.Serialize(new { sent = outcome.Sent, failed = outcome.Failed, summary = outcome.Message }),
            CancellationToken.None);
        return IcSyncJobTickResult.Executed;
    }

    private async Task<IcSyncJobTickResult> ExecuteAsync(
        IcSyncJobChannel channel, string baseUrl, Guid agentId, IcJobQueueEntry job, SyncProject project, SyncRunPartition? partition, CancellationToken ct)
    {
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cancelRequested = false;

        IcSyncRunOutcome outcome;
        try
        {
            outcome = await _runner.ExecuteAsync(project, TriggeredBy(job, partition), async (progress, progressCt) =>
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
            }, runCts.Token, partition);
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

    /// <summary>A container run's attribution names its container too, so Sync History tells the partitions of one plan apart.</summary>
    public static string TriggeredBy(IcJobQueueEntry job, SyncRunPartition? partition) =>
        partition is null ? TriggeredBy(job) : $"{TriggeredBy(job)} partition {partition.Index}/{partition.Count}";

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
