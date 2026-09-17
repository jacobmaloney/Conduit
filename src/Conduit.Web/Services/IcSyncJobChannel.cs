using System.Text;
using System.Text.Json;

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

    /// <summary>IC's JobTypeSupport.Any — an agent row listing "*" supports every job type.</summary>
    public const string AnyJobType = "*";

    private readonly HttpClient _client;

    public IcSyncJobChannel(HttpClient client) => _client = client;

    // ── Request bodies (the wire contract) ──────────────────────────────────

    public static string BuildClaimRequestJson(Guid agentId) =>
        JsonSerializer.Serialize(new
        {
            agentId,
            supportedJobTypes = new[] { SyncProjectJobType },
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

    public async Task<IcClaimOutcome> ClaimAsync(string baseUrl, Guid agentId, CancellationToken ct)
    {
        try
        {
            using var content = Json(BuildClaimRequestJson(agentId));
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
