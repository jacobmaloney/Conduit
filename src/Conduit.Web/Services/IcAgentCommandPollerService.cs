using System.Text;
using System.Text.Json;
using Conduit.Connectors.IdentityCenter;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Orchestration;
using Conduit.Sync.Security;

namespace Conduit.Web.Services;

/// <summary>
/// Outbound-only "Scan now" command poller + agent heartbeat. When an
/// IdentityCenter sink connection (BaseUrl + X-API-Key credential) is configured,
/// this polls IC's agent-command API every ~15s.
///
/// Key selection: the credential blob's OPTIONAL AgentApiKey (per-agent key) is
/// preferred for claim + heartbeat; ApiKey is the fallback (legacy mode) and is
/// ALWAYS what the sync source/sink and the legacy pending/ack path use — IC's
/// TenantDataPolicy denies per-agent keys on the data endpoints, so one key
/// cannot serve both channels.
///
/// Preferred (V140, per-agent key carrying an agent_id claim):
///   POST /api/agent/commands/claim              → atomically claims this agent's commands
///   POST /api/agent/commands/{id}/complete      → { success, message }
///   POST /api/agent/heartbeat (every ~60s)      → { version, capabilities[] };
///                                                 response echoes the server-assigned
///                                                 identity { agentId, name, location }
///
/// Fallback (older IC server, or a shared key without agent_id — claim answers
/// 404/405 for the former, 401/403 for the latter):
///   GET  /api/agent/commands/pending            → untargeted commands only
///   POST /api/agent/commands/{id}/ack           → legacy claim
///   POST /api/agent/commands/{id}/complete
///
/// For commandType "RunSqlDiscovery" it triggers Run-Now of the first (or the
/// payload-named) enabled Sync Project whose SOURCE is the SQL Discovery
/// connector, using the same IsRunning CAS + preClaimed orchestrator path the
/// manual Run-Now API uses.
///
/// 404 on the legacy pending endpoint means "feature not deployed" — logged ONCE
/// per endpoint at Debug, then polled quietly. Nothing in here may ever crash the host.
/// </summary>
public sealed class IcAgentCommandPollerService : BackgroundService
{
    private const string CommandRunSqlDiscovery = "RunSqlDiscovery";
    private const string CommandApplyObjectWrite = "ApplyObjectWrite";
    private const string CommandApplySqlWrite = "ApplySqlWrite";
    private const string SqlDiscoverySystemType = "SqlDiscovery";
    private const string IcCredentialName = "identitycenter";

    private static readonly string[] DefaultCapabilities = { "AdIdentitySync", "SqlDiscovery" };
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ClaimReprobeInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly IcAgentStatusService _status;
    private readonly ILogger<IcAgentCommandPollerService> _logger;

    /// <summary>BaseUrls whose pending endpoint returned 404 (feature not deployed) — log once each.</summary>
    private readonly HashSet<string> _notDeployedLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>BaseUrls where /claim is unusable (older server or shared key) + when we last probed it.</summary>
    private readonly Dictionary<string, DateTime> _legacyModeSince = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last heartbeat attempt per BaseUrl (UTC).</summary>
    private readonly Dictionary<string, DateTime> _lastHeartbeatAttempt = new(StringComparer.OrdinalIgnoreCase);

    public IcAgentCommandPollerService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        IcAgentStatusService status,
        ILogger<IcAgentCommandPollerService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _config = config;
        _status = status;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("AgentCommands:Enabled", true))
        {
            _logger.LogInformation("IC agent command poller disabled via AgentCommands:Enabled=false.");
            return;
        }

        var intervalSeconds = Math.Clamp(_config.GetValue("AgentCommands:PollIntervalSeconds", 15), 5, 3600);
        _logger.LogInformation("IC agent command poller started (interval {Interval}s, heartbeat {Heartbeat}s).",
            intervalSeconds, (int)HeartbeatInterval.TotalSeconds);

        // Initial delay lets the app finish booting (DB init, setup checks).
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never crash the host. Whatever went wrong this tick, try again next tick.
                _logger.LogDebug(ex, "IC agent command poll tick failed; retrying next interval.");
            }

            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        // Fresh scope per tick — repositories and the credential protector are scoped.
        using var scope = _scopeFactory.CreateScope();
        var tenants = scope.ServiceProvider.GetRequiredService<TenantRepository>();
        var protector = scope.ServiceProvider.GetRequiredService<CredentialProtector>();

        var icEndpoints = new List<(string BaseUrl, string ApiKey, string? AgentApiKey)>();
        foreach (var tenant in await tenants.GetAllAsync())
        {
            if (!string.Equals(tenant.SystemType, "IdentityCenter", StringComparison.OrdinalIgnoreCase))
                continue;
            string? raw = null;
            try { raw = await protector.RetrieveAsync(tenant.Id, IcCredentialName); }
            catch { /* unreadable credential — skip this connection */ }
            if (string.IsNullOrEmpty(raw)) continue;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var baseUrl = doc.RootElement.TryGetProperty("BaseUrl", out var uEl) ? uEl.GetString() : null;
                var apiKey = doc.RootElement.TryGetProperty("ApiKey", out var kEl) ? kEl.GetString() : null;
                var agentApiKey = doc.RootElement.TryGetProperty("AgentApiKey", out var aEl) ? aEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(agentApiKey)) agentApiKey = null;
                if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey)) continue;

                // Dedup by BaseUrl, but deterministically prefer the connection that
                // carries an AgentApiKey over one that does not (enumeration order of
                // the tenants must not decide which key drives the channel).
                var normalized = baseUrl!.TrimEnd('/');
                var existing = icEndpoints.FindIndex(e => string.Equals(e.BaseUrl, normalized, StringComparison.OrdinalIgnoreCase));
                if (existing < 0)
                    icEndpoints.Add((normalized, apiKey!, agentApiKey));
                else if (icEndpoints[existing].AgentApiKey is null && agentApiKey is not null)
                    icEndpoints[existing] = (normalized, apiKey!, agentApiKey);
            }
            catch (JsonException) { /* malformed blob — skip */ }
        }

        foreach (var (baseUrl, apiKey, agentApiKey) in icEndpoints)
        {
            ct.ThrowIfCancellationRequested();
            // AgentApiKey (per-agent key) drives claim + heartbeat when present;
            // the legacy pending/ack path always uses the shared ApiKey.
            var channelKey = agentApiKey ?? apiKey;
            _status.Update(baseUrl, s =>
            {
                s.KeyConfigured = true;
                s.KeySource = agentApiKey is not null ? "AgentApiKey" : "ApiKey";
            });
            await HeartbeatIfDueAsync(baseUrl, channelKey, ct);
            await PollEndpointAsync(baseUrl, channelKey, apiKey, ct);
        }
    }

    // ── Heartbeat ───────────────────────────────────────────────────────────

    private async Task HeartbeatIfDueAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_lastHeartbeatAttempt.TryGetValue(baseUrl, out var last) && now - last < HeartbeatInterval)
            return;
        _lastHeartbeatAttempt[baseUrl] = now;

        var client = CreateClient(apiKey);
        var version = GetType().Assembly.GetName().Version?.ToString() ?? "unknown";
        var capabilities = _config.GetSection("IcAgent:Capabilities").Get<string[]>() is { Length: > 0 } configured
            ? configured
            : DefaultCapabilities;

        try
        {
            var body = JsonSerializer.Serialize(new { version, capabilities });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync($"{baseUrl}/api/agent/heartbeat", content, ct);

            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                Guid? agentId = null;
                string? name = null, location = null;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (doc.RootElement.TryGetProperty("agentId", out var idEl)
                            && Guid.TryParse(idEl.GetString(), out var parsed))
                            agentId = parsed;
                        if (doc.RootElement.TryGetProperty("name", out var nameEl))
                            name = nameEl.GetString();
                        if (doc.RootElement.TryGetProperty("location", out var locEl)
                            && locEl.ValueKind == JsonValueKind.String)
                            location = locEl.GetString();
                    }
                }
                catch (JsonException) { /* identity display is best-effort */ }

                _status.Update(baseUrl, s =>
                {
                    s.LastHeartbeatUtc = now;
                    s.LastHeartbeatOk = true;
                    s.LastHeartbeatError = null;
                    s.AgentId = agentId;
                    s.AgentName = name;
                    s.AgentLocation = location;
                });
                _logger.LogDebug("IC agent heartbeat ok at {BaseUrl} — enrolled as '{Name}' ({AgentId}).", baseUrl, name, agentId);

                // Drift check (advisory, non-blocking): the key driving this channel should
                // be bound to THIS installation's provenance id, so write-back commands route
                // to the same agent that stamps Objects.SourceJobServerId. A mismatch means
                // the agent was enrolled in IC without (or with a wrong) instance id.
                if (agentId.HasValue && agentId.Value != ConduitInstanceIdentity.InstanceId)
                {
                    _logger.LogWarning(
                        "IC agent key at {BaseUrl} is bound to agent id {EnrolledId}, which differs from this instance's provenance id {InstanceId} — write-back may not route to this agent. Re-enroll in IdentityCenter using this instance's Instance ID.",
                        baseUrl, agentId.Value, ConduitInstanceIdentity.InstanceId);
                }
            }
            else
            {
                // 404/405: older IC server. 401/403: shared key without agent_id. 429: too chatty.
                _status.Update(baseUrl, s =>
                {
                    s.LastHeartbeatUtc = now;
                    s.LastHeartbeatOk = false;
                    s.LastHeartbeatError = $"HTTP {(int)resp.StatusCode}";
                });
                _logger.LogDebug("IC agent heartbeat at {BaseUrl} returned HTTP {Status}.", baseUrl, (int)resp.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _status.Update(baseUrl, s =>
            {
                s.LastHeartbeatUtc = now;
                s.LastHeartbeatOk = false;
                s.LastHeartbeatError = ex.Message;
            });
            _logger.LogDebug("IC agent heartbeat at {BaseUrl} unreachable ({Message}).", baseUrl, ex.Message);
        }
    }

    // ── Command polling ─────────────────────────────────────────────────────

    private async Task PollEndpointAsync(string baseUrl, string channelApiKey, string legacyApiKey, CancellationToken ct)
    {
        if (!IsInLegacyMode(baseUrl))
        {
            // Claim path (and the completes of claimed commands) runs on the
            // channel key — the per-agent AgentApiKey when configured.
            var client = CreateClient(channelApiKey);
            var claimOutcome = await TryClaimAsync(client, baseUrl, ct);
            if (claimOutcome.ChannelAvailable)
            {
                _status.Update(baseUrl, s => s.Mode = "Claim");
                foreach (var command in claimOutcome.Commands)
                {
                    ct.ThrowIfCancellationRequested();
                    await HandleCommandAsync(client, baseUrl, command, alreadyClaimed: true, ct);
                }
                return;
            }
            // Claim endpoint unusable (older IC server or shared key) — drop to the
            // legacy path and re-probe claim periodically in case IC gets upgraded.
            _legacyModeSince[baseUrl] = DateTime.UtcNow;
            _status.Update(baseUrl, s => s.Mode = "Legacy");
            _logger.LogInformation("IC agent commands at {BaseUrl}: claim endpoint unavailable ({Reason}) — using the legacy untargeted poll path.",
                baseUrl, claimOutcome.Reason);
        }

        // Legacy pending/ack/complete always runs on the shared ApiKey — IC's
        // legacy endpoints sit behind TenantDataPolicy, which denies per-agent keys.
        await PollLegacyAsync(CreateClient(legacyApiKey), baseUrl, ct);
    }

    private bool IsInLegacyMode(string baseUrl)
    {
        if (!_legacyModeSince.TryGetValue(baseUrl, out var since)) return false;
        if (DateTime.UtcNow - since < ClaimReprobeInterval) return true;
        _legacyModeSince.Remove(baseUrl); // time to re-probe claim
        return false;
    }

    private sealed record ClaimOutcome(bool ChannelAvailable, string Reason, List<PendingCommand> Commands);

    private async Task<ClaimOutcome> TryClaimAsync(HttpClient client, string baseUrl, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent("{\"max\":10}", Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync($"{baseUrl}/api/agent/commands/claim", content, ct);

            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                try
                {
                    return new ClaimOutcome(true, "ok", ParsePendingCommands(json));
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug(ex, "IC agent claim at {BaseUrl} returned an unparseable payload.", baseUrl);
                    return new ClaimOutcome(true, "ok", new List<PendingCommand>());
                }
            }

            var status = (int)resp.StatusCode;
            if (status is 404 or 405 or 401 or 403)
                return new ClaimOutcome(false, $"HTTP {status}", new List<PendingCommand>());

            // Transient server-side trouble: stay on the claim channel, just skip this tick.
            _logger.LogDebug("IC agent claim at {BaseUrl} returned HTTP {Status}; retrying next tick.", baseUrl, status);
            return new ClaimOutcome(true, $"HTTP {status}", new List<PendingCommand>());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug("IC agent claim at {BaseUrl} unreachable ({Message}).", baseUrl, ex.Message);
            return new ClaimOutcome(true, "unreachable", new List<PendingCommand>());
        }
    }

    private async Task PollLegacyAsync(HttpClient client, string baseUrl, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await client.GetAsync($"{baseUrl}/api/agent/commands/pending", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug("IC agent command poll: {BaseUrl} unreachable ({Message}).", baseUrl, ex.Message);
            return;
        }

        using (resp)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                if (_notDeployedLogged.Add(baseUrl))
                    _logger.LogDebug("IC agent command API not deployed at {BaseUrl} (404 on /api/agent/commands/pending) — will keep polling quietly.", baseUrl);
                return;
            }
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("IC agent command poll: {BaseUrl} returned HTTP {Status}.", baseUrl, (int)resp.StatusCode);
                return;
            }

            // The endpoint exists (deployed since the last 404, if any).
            _notDeployedLogged.Remove(baseUrl);

            var json = await resp.Content.ReadAsStringAsync(ct);
            List<PendingCommand> commands;
            try
            {
                commands = ParsePendingCommands(json);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "IC agent command poll: {BaseUrl} returned an unparseable pending payload.", baseUrl);
                return;
            }

            foreach (var command in commands)
            {
                ct.ThrowIfCancellationRequested();
                await HandleCommandAsync(client, baseUrl, command, alreadyClaimed: false, ct);
            }
        }
    }

    private HttpClient CreateClient(string apiKey)
    {
        var client = _httpFactory.CreateClient("IcAgentCommandPoller");
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.Remove("X-API-Key");
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        return client;
    }

    private sealed record PendingCommand(Guid Id, string CommandType, string? PayloadJson);

    private static List<PendingCommand> ParsePendingCommands(string json)
    {
        var commands = new List<PendingCommand>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return commands;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            Guid id = Guid.Empty;
            string? type = null, payload = null;
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.NameEquals("id") && prop.Value.ValueKind == JsonValueKind.String)
                    Guid.TryParse(prop.Value.GetString(), out id);
                else if (prop.NameEquals("commandType") && prop.Value.ValueKind == JsonValueKind.String)
                    type = prop.Value.GetString();
                else if (prop.NameEquals("payloadJson"))
                    payload = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
            }
            if (id != Guid.Empty && !string.IsNullOrEmpty(type))
                commands.Add(new PendingCommand(id, type!, payload));
        }
        return commands;
    }

    private async Task HandleCommandAsync(HttpClient client, string baseUrl, PendingCommand command, bool alreadyClaimed, CancellationToken ct)
    {
        // Legacy path: claim first — even for unsupported types, so IC stops re-offering
        // it. The claim channel returns commands already Acked, so no separate ack there.
        if (!alreadyClaimed
            && !await PostAsync(client, $"{baseUrl}/api/agent/commands/{command.Id}/ack", null, ct))
        {
            _logger.LogDebug("IC agent command {Id}: ack failed; leaving for next poll.", command.Id);
            return;
        }

        bool success;
        string message;
        if (string.Equals(command.CommandType, CommandRunSqlDiscovery, StringComparison.OrdinalIgnoreCase))
        {
            (success, message) = await RunSqlDiscoveryAsync(command.PayloadJson, ct);
        }
        else if (string.Equals(command.CommandType, CommandApplyObjectWrite, StringComparison.OrdinalIgnoreCase))
        {
            (success, message) = await ApplyObjectWriteAsync(command.Id, command.PayloadJson, ct);
        }
        else if (string.Equals(command.CommandType, CommandApplySqlWrite, StringComparison.OrdinalIgnoreCase))
        {
            (success, message) = await ApplySqlWriteAsync(command.Id, command.PayloadJson, ct);
        }
        else
        {
            success = false;
            message = $"Unsupported commandType '{command.CommandType}'.";
            _logger.LogWarning("IC agent command {Id}: unsupported commandType '{Type}'.", command.Id, command.CommandType);
        }

        var completeBody = JsonSerializer.Serialize(new { success, message });
        if (!await PostAsync(client, $"{baseUrl}/api/agent/commands/{command.Id}/complete", completeBody, ct))
            _logger.LogDebug("IC agent command {Id}: complete callback failed (run outcome: {Success} — {Message}).", command.Id, success, message);
        else
            _logger.LogInformation("IC agent command {Id} ({Type}) completed: {Success} — {Message}", command.Id, command.CommandType, success, message);
    }

    /// <summary>
    /// Run-Now of the SQL Discovery project, awaited so the complete callback
    /// carries the real run outcome. Uses the SAME IsRunning CAS + preClaimed
    /// orchestrator contract the manual Run-Now API endpoint uses.
    /// </summary>
    private async Task<(bool Success, string Message)> RunSqlDiscoveryAsync(string? payloadJson, CancellationToken ct)
    {
        string? requestedName = null;
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("projectName", out var nameEl)
                    && nameEl.ValueKind == JsonValueKind.String)
                {
                    requestedName = nameEl.GetString();
                }
            }
            catch (JsonException) { /* payload is advisory; fall through to first project */ }
        }

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

        var project = requestedName is null
            ? candidates.FirstOrDefault()
            : candidates.FirstOrDefault(p => string.Equals(p.Name, requestedName, StringComparison.OrdinalIgnoreCase));

        if (project is null)
        {
            return (false, requestedName is null
                ? "No enabled Sync Project with a SQL Discovery source exists."
                : $"No enabled SQL Discovery project named '{requestedName}' exists.");
        }

        var claimed = await projectRepo.SetRunningAsync(project.Id, Guid.Empty);
        if (!claimed)
            return (false, $"Project '{project.Name}' already has a run in progress.");

        try
        {
            // CancellationToken.None: a host shutdown mid-run is handled by the
            // orchestrator's own cancellation registry; the command outcome is
            // best-effort at that point.
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
            return (false, $"Project '{project.Name}' run threw: {ex.Message}");
        }
    }

    /// <summary>
    /// ApplyObjectWrite: route a single GUID-addressed AD write through this agent.
    /// Thin shim — all allow-listing/validation/LDAP lives in AdAgentWriteExecutor,
    /// resolved from a per-command DI scope (TenantRepository + CredentialProtector
    /// + connector adapters are scoped). The raw payload is NEVER logged here.
    /// </summary>
    private async Task<(bool Success, string Message)> ApplyObjectWriteAsync(Guid commandId, string? payloadJson, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<Conduit.Web.Services.AdAgentWriteExecutor>();
        return await executor.ExecuteAsync(commandId, payloadJson, ct);
    }

    /// <summary>
    /// ApplySqlWrite: route a single SQL Server security DDL change through this
    /// agent. Thin shim — all allow-listing / identifier validation / parameterized
    /// QUOTENAME DDL lives in SqlAgentWriteExecutor, resolved from a per-command DI
    /// scope (the CredentialProtector it needs is scoped). Raw payload NEVER logged.
    /// </summary>
    private async Task<(bool Success, string Message)> ApplySqlWriteAsync(Guid commandId, string? payloadJson, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<Conduit.Web.Services.SqlAgentWriteExecutor>();
        return await executor.ExecuteAsync(commandId, payloadJson, ct);
    }

    private async Task<bool> PostAsync(HttpClient client, string url, string? jsonBody, CancellationToken ct)
    {
        try
        {
            using var content = jsonBody is null
                ? new StringContent(string.Empty, Encoding.UTF8, "application/json")
                : new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync(url, content, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
