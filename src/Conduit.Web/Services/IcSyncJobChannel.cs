using System.Text;
using System.Text.Json;
using Conduit.Core.SyncModels;

namespace Conduit.Web.Services;

/// <summary>
/// The job-queue entry IdentityCenter hands back on a claim. This is a MIRROR of IC's
/// <c>JobQueueEntry</c> (DataAccessLibrary/Models/RemoteAgentModels.cs) reduced to the
/// fields this executor acts on; the two repositories share no assembly, so the wire
/// field names are the contract and are pinned by tests.
/// </summary>
public sealed record IcJobQueueEntry(
    Guid Id,
    string JobType,
    string? JobName,
    Guid? RelatedEntityId,
    string? RelatedEntityType,
    string? PayloadJson,
    Guid? TargetServerId,
    bool CancellationRequested);

/// <summary>Why a claim attempt ended the way it did — every value is reportable text, never a silent idle.</summary>
public enum IcClaimResult
{
    /// <summary>A job was handed to this installation.</summary>
    Claimed,

    /// <summary>The channel answered, and there is nothing pinned here right now.</summary>
    NoWork,

    /// <summary>401/403: IdentityCenter refused this key on the job queue. Work will NEVER arrive until that is fixed.</summary>
    Rejected,

    /// <summary>404/405: this IdentityCenter does not expose the job queue.</summary>
    NotDeployed,

    /// <summary>5xx, a transport failure or an unreadable body — transient; retry next tick.</summary>
    Unavailable
}

public sealed record IcClaimOutcome(IcClaimResult Result, IcJobQueueEntry? Job, string Message);

/// <summary>One container item IdentityCenter queued for a plan, and the server it was dealt to.</summary>
public sealed record IcPlanChild(Guid JobId, int Index, string Label, string ServerName);

/// <summary>IdentityCenter's answer to a partition plan: accepted with the queued containers, or not, with the reason.</summary>
public sealed record IcPlanAnswer(bool Accepted, string Outcome, string? Reason, IReadOnlyList<IcPlanChild> Children);

/// <summary>
/// The IdentityCenter job-queue wire protocol, as one testable object: claim, progress,
/// completion and the cancellation re-read. Holds no policy — it never decides whether to
/// run anything — so a fake <see cref="HttpMessageHandler"/> proves the whole contract offline.
///
/// Endpoints (IdentityCenter.API/Controllers/JobsController.cs):
///   POST /api/jobs/claim            [AgentPolicy]  { agentId, supportedJobTypes[], maxJobs } → { success, job, message }
///   POST /api/jobs/{id}/progress    [AgentPolicy]  JobProgressUpdate
///   POST /api/jobs/{id}/complete    [AgentPolicy]  CompleteJobRequest
///   GET  /api/jobs/{id}             [Authorize]    JobQueueEntry — re-read for CancellationRequested
///   GET  /api/agents/{agentId}      [Authorize]    RemoteAgent — SupportedJobTypes advisory check
///
/// The claim body always carries THIS installation's own agent id: IC rejects a mismatch
/// between the body and the agent id in the key's claims, and nothing here accepts an
/// agent id from anywhere but the heartbeat-assigned identity.
/// </summary>
public sealed class IcSyncJobChannel
{
    /// <summary>The one job type this executor claims. IdentityCenter's SyncSubmissionService stamps the same string.</summary>
    public const string SyncProjectJobType = "SyncProject";

    /// <summary>JOB-PLACEMENT-03: IdentityCenter's mail drain job, claimed only when it is placed on this server.</summary>
    public const string MailJobType = "EmailQueueProcessing";

    /// <summary>IC's JobTypeSupport.Any — an agent row listing "*" supports every job type.</summary>
    public const string AnyJobType = "*";

    private readonly HttpClient _client;

    public IcSyncJobChannel(HttpClient client) => _client = client;

    // ── Request bodies (the wire contract) ──────────────────────────────────

    public static string BuildClaimRequestJson(Guid agentId, IReadOnlyList<string>? jobTypes = null) =>
        JsonSerializer.Serialize(new
        {
            agentId,
            supportedJobTypes = jobTypes is { Count: > 0 } ? jobTypes.ToArray() : new[] { SyncProjectJobType },
            maxJobs = 1
        });

    public static string BuildProgressJson(Guid jobId, Guid agentId, int progressPercent, string? progressMessage,
        int itemsProcessed, int itemsSucceeded, int itemsFailed) =>
        JsonSerializer.Serialize(new
        {
            jobId,
            agentId,
            progressPercent = Math.Clamp(progressPercent, 0, 100),
            progressMessage,
            itemsProcessed,
            itemsSucceeded,
            itemsFailed
        });

    public static string BuildCompleteJson(Guid jobId, Guid agentId, bool success,
        int itemsProcessed, int itemsSucceeded, int itemsFailed, string? errorMessage, string? resultJson) =>
        JsonSerializer.Serialize(new
        {
            jobId,
            agentId,
            success,
            itemsProcessed,
            itemsSucceeded,
            itemsFailed,
            errorMessage,
            resultJson
        });

    // ── Response parsing ────────────────────────────────────────────────────

    /// <summary>
    /// Reads a ClaimJobResponse. A body with success=false (or no job) is "no work",
    /// not a failure; an unparseable body is reported as unavailable rather than silently empty.
    /// </summary>
    public static IcClaimOutcome ParseClaimResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new IcClaimOutcome(IcClaimResult.Unavailable, null, "The claim response was not an object.");

            var message = doc.RootElement.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString()
                : null;

            if (!doc.RootElement.TryGetProperty("job", out var jobEl) || jobEl.ValueKind != JsonValueKind.Object)
                return new IcClaimOutcome(IcClaimResult.NoWork, null, message ?? "No jobs available");

            var job = ReadJob(jobEl);
            return job is null
                ? new IcClaimOutcome(IcClaimResult.Unavailable, null, "The claimed job carried no usable id or job type.")
                : new IcClaimOutcome(IcClaimResult.Claimed, job, message ?? "Claimed");
        }
        catch (JsonException)
        {
            return new IcClaimOutcome(IcClaimResult.Unavailable, null, "The claim response was not valid JSON.");
        }
    }

    public static IcJobQueueEntry? ParseJob(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? ReadJob(doc.RootElement) : null;
        }
        catch (JsonException) { return null; }
    }

    private static IcJobQueueEntry? ReadJob(JsonElement el)
    {
        var id = ReadGuid(el, "id");
        var jobType = ReadString(el, "jobType");
        if (id is null || string.IsNullOrEmpty(jobType)) return null;
        return new IcJobQueueEntry(
            id.Value,
            jobType!,
            ReadString(el, "jobName"),
            ReadGuid(el, "relatedEntityId"),
            ReadString(el, "relatedEntityType"),
            ReadString(el, "payloadJson"),
            ReadGuid(el, "targetServerId"),
            el.TryGetProperty("cancellationRequested", out var cancelEl) && cancelEl.ValueKind == JsonValueKind.True);
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Guid? ReadGuid(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g)
            ? g
            : null;

    /// <summary>
    /// Mirror of IdentityCenter's <c>JobTypeSupport.Supports</c>: a comma-separated list where
    /// "*" means every type. Kept here because the two repositories share no assembly; a divergence
    /// would silently mis-report "this agent will never get work".
    /// </summary>
    public static bool SupportsSyncProject(string? supportedJobTypes)
    {
        if (string.IsNullOrWhiteSpace(supportedJobTypes)) return false;
        foreach (var raw in supportedJobTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == AnyJobType || string.Equals(raw, SyncProjectJobType, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ── Calls ───────────────────────────────────────────────────────────────

    public async Task<IcClaimOutcome> ClaimAsync(string baseUrl, Guid agentId, CancellationToken ct, IReadOnlyList<string>? jobTypes = null)
    {
        try
        {
            using var content = Json(BuildClaimRequestJson(agentId, jobTypes));
            using var resp = await _client.PostAsync($"{baseUrl}/api/jobs/claim", content, ct);
            var status = (int)resp.StatusCode;

            if (resp.IsSuccessStatusCode)
                return ParseClaimResponse(await resp.Content.ReadAsStringAsync(ct));

            return status switch
            {
                401 or 403 => new IcClaimOutcome(IcClaimResult.Rejected, null,
                    $"HTTP {status}: IdentityCenter refused this installation's key on the job queue."),
                404 or 405 => new IcClaimOutcome(IcClaimResult.NotDeployed, null,
                    $"HTTP {status}: this IdentityCenter does not expose /api/jobs/claim."),
                _ => new IcClaimOutcome(IcClaimResult.Unavailable, null, $"HTTP {status}")
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new IcClaimOutcome(IcClaimResult.Unavailable, null, $"unreachable ({ex.Message})");
        }
    }

    public Task<bool> ReportProgressAsync(string baseUrl, Guid jobId, Guid agentId, int progressPercent,
        string? progressMessage, int itemsProcessed, int itemsSucceeded, int itemsFailed, CancellationToken ct) =>
        PostAsync($"{baseUrl}/api/jobs/{jobId}/progress",
            BuildProgressJson(jobId, agentId, progressPercent, progressMessage, itemsProcessed, itemsSucceeded, itemsFailed), ct);

    public Task<bool> CompleteAsync(string baseUrl, Guid jobId, Guid agentId, bool success,
        int itemsProcessed, int itemsSucceeded, int itemsFailed, string? errorMessage, string? resultJson, CancellationToken ct) =>
        PostAsync($"{baseUrl}/api/jobs/{jobId}/complete",
            BuildCompleteJson(jobId, agentId, success, itemsProcessed, itemsSucceeded, itemsFailed, errorMessage, resultJson), ct);

    /// <summary>
    /// Re-reads the claimed job. Null means "could not tell" — an unreachable or unreadable
    /// answer must never be mistaken for "cancellation was requested" or for "it was not".
    /// </summary>
    public async Task<IcJobQueueEntry?> ReadJobAsync(string baseUrl, Guid jobId, CancellationToken ct)
    {
        try
        {
            using var resp = await _client.GetAsync($"{baseUrl}/api/jobs/{jobId}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return ParseJob(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The agent row's declared SupportedJobTypes, or null when it cannot be read. Advisory:
    /// it explains a permanently empty queue, it never decides whether to claim.
    /// </summary>
    public async Task<string?> ReadSupportedJobTypesAsync(string baseUrl, Guid agentId, CancellationToken ct)
    {
        try
        {
            using var resp = await _client.GetAsync($"{baseUrl}/api/agents/{agentId}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? ReadString(doc.RootElement, "supportedJobTypes") : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    // ── Partitions (SPLIT-ENUM-01: POST /api/jobs/{id}/partitions, AgentJobsPolicy) ──

    /// <summary>
    /// Hands IdentityCenter the containers of a plan item this installation holds. IdentityCenter queues one item per
    /// container over the project's partition servers and answers with them (200, Queued or AlreadyQueued); 409 is a
    /// refusal with its reason; anything else is "not accepted" with what is known. Nothing is assumed queued from an
    /// unreadable answer.
    /// </summary>
    /// <summary>
    /// ZERO-TOUCH-01: GET api/jobs/{id}/project, the definition of the IdentityCenter project behind a job this
    /// installation holds. The answer carries the definition, or the status and reason IdentityCenter refused with
    /// (404 no such job, 403 held by another server, 409 not a sync project, 503 not deployed); unreachable is a reason too.
    /// </summary>
    public async Task<IcProjectDefinitionAnswer> ReadProjectDefinitionAsync(string baseUrl, Guid jobId, CancellationToken ct)
    {
        try
        {
            using var resp = await _client.GetAsync($"{baseUrl}/api/jobs/{jobId}/project", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var status = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
            {
                var definition = IcProjectDefinitionReader.Parse(body);
                return definition is null
                    ? new IcProjectDefinitionAnswer(null, status, "IdentityCenter answered with something that is not a project definition.")
                    : new IcProjectDefinitionAnswer(definition, status, null);
            }
            return new IcProjectDefinitionAnswer(null, status, IcHeartbeatBody.DescribeRefusal(status, body));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new IcProjectDefinitionAnswer(null, 0, $"unreachable ({ex.Message})");
        }
    }

    public async Task<IcPlanAnswer> PlanPartitionsAsync(string baseUrl, Guid jobId, Guid agentId, IReadOnlyList<SyncRunPartition> partitions, CancellationToken ct)
    {
        try
        {
            using var content = Json(BuildPlanJson(agentId, partitions));
            using var resp = await _client.PostAsync($"{baseUrl}/api/jobs/{jobId}/partitions", content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var parsed = ParsePlanAnswer(body);
            if (resp.IsSuccessStatusCode && parsed is { Children.Count: > 0 })
                return parsed with { Accepted = true };
            var status = (int)resp.StatusCode;
            return new IcPlanAnswer(false, parsed?.Outcome ?? $"HTTP {status}", parsed?.Reason ?? $"HTTP {status}: IdentityCenter did not accept the partition plan.", Array.Empty<IcPlanChild>());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new IcPlanAnswer(false, "Unreachable", $"unreachable ({ex.Message})", Array.Empty<IcPlanChild>());
        }
    }

    public static string BuildPlanJson(Guid agentId, IReadOnlyList<SyncRunPartition> partitions) =>
        JsonSerializer.Serialize(new
        {
            agentId,
            partitions = partitions.Select(p => new { label = p.Label, includedBaseDNs = p.IncludedBaseDNs, oneLevel = p.OneLevel, carriesTombstones = p.CarriesTombstones }).ToArray()
        });

    public static IcPlanAnswer? ParsePlanAnswer(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var children = new List<IcPlanChild>();
            if (doc.RootElement.TryGetProperty("children", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in list.EnumerateArray())
                {
                    var id = ReadGuid(child, "jobId");
                    if (id is null) continue;
                    var index = child.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number ? ix.GetInt32() : 0;
                    children.Add(new IcPlanChild(id.Value, index, ReadString(child, "label") ?? string.Empty, ReadString(child, "serverName") ?? string.Empty));
                }
            }
            return new IcPlanAnswer(false, ReadString(doc.RootElement, "outcome") ?? string.Empty, ReadString(doc.RootElement, "reason"), children);
        }
        catch (JsonException) { return null; }
    }

    // ── Mail (JOB-PLACEMENT-03: IdentityCenter.API/Controllers/JobMailController.cs) ──

    private static readonly JsonSerializerOptions MailJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// POST /api/jobs/{id}/mail/claim — a batch of messages and the relay to send them through. Null means the answer
    /// could not be read; nothing is assumed claimed or not claimed from an unreadable answer.
    /// </summary>
    public async Task<IcMailBatch?> ClaimMailAsync(string baseUrl, Guid jobId, int max, bool canSignIn, CancellationToken ct)
    {
        try
        {
            using var content = Json(JsonSerializer.Serialize(new { max, canSignIn }));
            using var resp = await _client.PostAsync($"{baseUrl}/api/jobs/{jobId}/mail/claim", content, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var batch = JsonSerializer.Deserialize<IcMailBatch>(await resp.Content.ReadAsStringAsync(ct), MailJson);
            return batch is null ? null : batch with { Items = batch.Items ?? Array.Empty<IcMailItem>() };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>POST /api/jobs/{id}/mail/settle — each message's outcome. False = IdentityCenter did not accept it.</summary>
    public Task<bool> SettleMailAsync(string baseUrl, Guid jobId, IReadOnlyList<IcMailResult> results, CancellationToken ct) =>
        PostAsync($"{baseUrl}/api/jobs/{jobId}/mail/settle",
            JsonSerializer.Serialize(new { results = results.Select(r => new { id = r.Id, success = r.Success, error = r.Error }) }), ct);

    private async Task<bool> PostAsync(string url, string body, CancellationToken ct)
    {
        try
        {
            using var content = Json(body);
            using var resp = await _client.PostAsync(url, content, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
