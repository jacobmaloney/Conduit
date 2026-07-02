using System.Text;
using System.Text.Json;
using Conduit.Connectors.IdentityCenter;
using Conduit.Core.Models;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Security;

namespace Conduit.Web.Services;

/// <summary>
/// One-shot startup enrollment against an IdentityCenter tenant portal.
///
/// Given --enroll-url and --enroll-code (or Enroll:Url / Enroll:Code in
/// appsettings; command line wins), POSTs {enrollUrl}/api/agent/enroll once,
/// then persists the result as a normal IdentityCenter Connected System:
/// a Tenants row plus the "identitycenter" credential blob
/// { BaseUrl, ApiKey, AgentApiKey } — exactly the shape IcAgentCommandPollerService
/// and IdentityCenterCredentialReader already parse, so heartbeat/claim and sync
/// begin on the poller's next tick with no further wiring.
///
/// The enroll code is SINGLE-USE, so idempotency matters more than retries:
/// before calling, every existing IdentityCenter tenant's credential is checked
/// and if one already points at the enroll-url's origin the call is skipped
/// entirely — a restart with the same (consumed) code never re-sends it.
/// One retry on transient failure (network / 5xx) only; 403 means the code is
/// invalid/expired/used and is never retried. Nothing in here may crash the host.
/// </summary>
public sealed class EnrollmentService
{
    private const string IcCredentialName = "identitycenter";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<EnrollmentService> _logger;

    /// <summary>Read-only outcome of this boot's auto-enrollment, for the Configuration page.</summary>
    public string StateDescription { get; private set; } = "Not configured (start with --enroll-url and --enroll-code to enroll automatically).";

    public EnrollmentService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<EnrollmentService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Runs exactly once at startup, after the setup gate / DatabaseInitializer.
    /// Silent no-op when no enroll-code is configured. Never throws.
    /// </summary>
    public async Task RunAtStartupAsync(bool databaseReady, CancellationToken ct = default)
    {
        try
        {
            await RunCoreAsync(databaseReady, ct);
        }
        catch (Exception ex)
        {
            StateDescription = $"Enrollment failed: {ex.Message}";
            _logger.LogError(ex, "Startup enrollment failed; the host continues without it.");
        }
    }

    private async Task RunCoreAsync(bool databaseReady, CancellationToken ct)
    {
        // Command line (--enroll-url=/--enroll-code= arrive as the top-level keys
        // "enroll-url"/"enroll-code" via CreateBuilder(args)) wins over appsettings.
        var enrollUrl = _config["enroll-url"] ?? _config["Enroll:Url"];
        var enrollCode = _config["enroll-code"] ?? _config["Enroll:Code"];

        if (string.IsNullOrWhiteSpace(enrollCode) && string.IsNullOrWhiteSpace(enrollUrl))
            return; // not configured — silent no-op

        if (string.IsNullOrWhiteSpace(enrollCode) || string.IsNullOrWhiteSpace(enrollUrl))
        {
            StateDescription = "Enrollment skipped: both --enroll-url and --enroll-code are required.";
            _logger.LogWarning("Enrollment needs BOTH --enroll-url and --enroll-code; only one was supplied — skipping.");
            return;
        }

        if (!databaseReady)
        {
            StateDescription = "Enrollment skipped: database not initialized (complete /setup, then restart with the enroll arguments).";
            _logger.LogWarning("Enroll code is configured but the database is not initialized (setup incomplete or unreachable) — skipping enrollment this boot.");
            return;
        }

        var targetOrigin = EnrollmentClient.NormalizeOrigin(enrollUrl);
        if (targetOrigin is null)
        {
            StateDescription = $"Enrollment skipped: '{enrollUrl}' is not a valid absolute URL.";
            _logger.LogError("Enroll URL '{EnrollUrl}' is not a valid absolute http(s) URL — skipping enrollment.", enrollUrl);
            return;
        }
        if (targetOrigin.StartsWith("http://", StringComparison.Ordinal))
        {
            _logger.LogWarning("Enroll URL {Origin} is plain http — the enroll code and the returned API keys cross the wire in cleartext. Use https outside a lab.", targetOrigin);
        }

        using var scope = _scopeFactory.CreateScope();
        var tenants = scope.ServiceProvider.GetRequiredService<TenantRepository>();
        var protector = scope.ServiceProvider.GetRequiredService<CredentialProtector>();

        // Idempotency: a consumed single-use code must never be re-sent on restart.
        foreach (var tenant in await tenants.GetAllAsync(includeInactive: true))
        {
            if (!string.Equals(tenant.SystemType, "IdentityCenter", StringComparison.OrdinalIgnoreCase))
                continue;
            string? raw = null;
            try { raw = await protector.RetrieveAsync(tenant.Id, IcCredentialName); }
            catch { /* unreadable credential — cannot prove enrollment from it */ }
            if (string.IsNullOrEmpty(raw)) continue;

            if (EnrollmentClient.CredentialMatchesOrigin(raw, targetOrigin))
            {
                StateDescription = $"Enrolled against {targetOrigin} (connection '{tenant.Name}').";
                _logger.LogInformation("Already enrolled against {Origin} (connection '{Tenant}'), skipping.", targetOrigin, tenant.Name);
                return;
            }
        }

        var version = typeof(EnrollmentService).Assembly.GetName().Version?.ToString();
        var client = new EnrollmentClient(_httpFactory.CreateClient("Enrollment"));
        var outcome = await client.EnrollAsync(
            enrollUrl, enrollCode, ConduitInstanceIdentity.InstanceId, ConduitInstanceIdentity.Name, version, ct);

        if (outcome.Response is null)
        {
            StateDescription = $"Enrollment failed: {outcome.Error}";
            _logger.LogError("Enrollment against {Origin} failed: {Error}", targetOrigin, outcome.Error);
            return;
        }

        var response = outcome.Response;
        var name = EnrollmentClient.BuildTenantName(response.TenantSlug);
        if (await tenants.NameOrSlugInUseByOtherAsync(name, Guid.Empty))
            name = IdentityCenterSourceName.Sanitize($"{name}-{response.AgentId.ToString("N")[..8]}");

        // Past this point the single-use code is consumed server-side, so a
        // persistence failure cannot be retried with the same code. Fail with a
        // message that says exactly that (NOT "code invalid"), and never leave a
        // keyless Tenants row behind — the idempotency scan skips credential-less
        // rows, so an orphan would cause the next boot to re-send the dead code.
        Tenant created;
        try
        {
            created = await tenants.CreateAsync(new Tenant
            {
                Name = name,
                Slug = name,
                SystemType = "IdentityCenter",
                IsActive = true,
                Description = $"Auto-enrolled against {response.BaseUrl}"
            });
        }
        catch (Exception ex)
        {
            ReportPersistenceFailure(targetOrigin, ex);
            return;
        }

        try
        {
            await protector.StoreAsync(created.Id, IcCredentialName, EnrollmentClient.ComposeCredentialBlob(response, enrollUrl));
        }
        catch (Exception ex)
        {
            try { await tenants.DeleteAsync(created.Id); }
            catch { /* best effort; the row has no credential so nothing can use it */ }
            ReportPersistenceFailure(targetOrigin, ex);
            return;
        }

        try
        {
            await tenants.StampIcEntitlementAsync(created.Id, response.BaseUrl);
        }
        catch (Exception ex)
        {
            // The stamp is defensive future-proofing only (gate removed at 35f0a19);
            // the enrollment itself is complete and usable.
            _logger.LogWarning(ex, "Entitlement stamp failed for connection '{Name}'; enrollment is still valid.", name);
        }

        StateDescription = $"Enrolled against {response.BaseUrl} as agent {response.AgentId} (connection '{name}').";
        _logger.LogInformation(
            "Enrolled against {BaseUrl}: tenant slug '{Slug}', agent id {AgentId}, connection '{Name}'. The IC agent poller picks it up on its next tick.",
            response.BaseUrl, response.TenantSlug, response.AgentId, name);
    }

    private void ReportPersistenceFailure(string targetOrigin, Exception ex)
    {
        StateDescription = "Enrollment succeeded but saving the connection failed — the single-use code is consumed; generate a new one in the tenant portal and re-enroll.";
        _logger.LogError(ex,
            "Enrollment against {Origin} succeeded but persisting the connection failed. The single-use code is consumed server-side — generate a new one in the tenant portal and re-enroll.",
            targetOrigin);
    }
}

/// <summary>
/// The DB-free core of enrollment: request build, HTTP call + retry policy,
/// response parse, origin normalization, and credential-blob composition.
/// HttpClient is injected so tests drive it through a fake HttpMessageHandler.
/// </summary>
public sealed class EnrollmentClient
{
    private readonly HttpClient _http;
    private readonly TimeSpan _retryDelay;

    public EnrollmentClient(HttpClient http, TimeSpan? retryDelay = null)
    {
        _http = http;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(3);
    }

    public sealed record EnrollmentResponse(string BaseUrl, string TenantSlug, Guid AgentId, string AgentApiKey, string SyncApiKey);

    public sealed record EnrollmentOutcome(EnrollmentResponse? Response, string? Error, int Attempts);

    /// <summary>
    /// POST {enrollUrl}/api/agent/enroll. One retry on transient (network / 5xx)
    /// failure only. 403 = code invalid/expired/used — single-use, NEVER retried.
    /// 400/429 are not retried either (the code was not consumed; a later restart
    /// may try again). Never throws.
    /// </summary>
    public async Task<EnrollmentOutcome> EnrollAsync(
        string enrollUrl, string enrollCode, Guid instanceId, string? name, string? version, CancellationToken ct = default)
    {
        var url = $"{enrollUrl.TrimEnd('/')}/api/agent/enroll";
        var body = BuildRequestJson(enrollCode, instanceId, name, version);

        for (var attempt = 1; ; attempt++)
        {
            string? transientError;
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp = await _http.PostAsync(url, content, ct);

                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    var parsed = ParseResponse(json);
                    return parsed is null
                        ? new EnrollmentOutcome(null, "Enrollment returned 200 but the response payload was missing required fields.", attempt)
                        : new EnrollmentOutcome(parsed, null, attempt);
                }

                var status = (int)resp.StatusCode;
                if (status == 403)
                    return new EnrollmentOutcome(null, "enroll code invalid or expired — generate a new one in the tenant portal.", attempt);
                if (status == 400)
                    return new EnrollmentOutcome(null, "enrollment request rejected as malformed (HTTP 400).", attempt);
                if (status == 429)
                    return new EnrollmentOutcome(null, "enrollment rate limited (HTTP 429) — restart the service to retry.", attempt);
                if (status < 500)
                    return new EnrollmentOutcome(null, $"enrollment failed with HTTP {status}.", attempt);

                transientError = $"enrollment failed with HTTP {status}.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new EnrollmentOutcome(null, "enrollment cancelled (host shutting down).", attempt);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                transientError = $"could not reach {url} ({ex.Message}).";
            }

            if (attempt >= 2)
                return new EnrollmentOutcome(null, transientError, attempt);

            try { await Task.Delay(_retryDelay, ct); }
            catch (OperationCanceledException)
            {
                return new EnrollmentOutcome(null, "enrollment cancelled (host shutting down).", attempt);
            }
        }
    }

    /// <summary>Exact wire shape of the fixed contract: enrollCode/instanceId/name/version.</summary>
    public static string BuildRequestJson(string enrollCode, Guid instanceId, string? name, string? version) =>
        JsonSerializer.Serialize(new { enrollCode, instanceId, name, version });

    /// <summary>
    /// Parses the success payload { baseUrl, tenantSlug, agentId, agentApiKey, syncApiKey }.
    /// Returns null when any required field is missing/empty or the JSON is malformed.
    /// </summary>
    public static EnrollmentResponse? ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            var baseUrl = GetString(doc.RootElement, "baseUrl");
            var tenantSlug = GetString(doc.RootElement, "tenantSlug");
            var agentApiKey = GetString(doc.RootElement, "agentApiKey");
            var syncApiKey = GetString(doc.RootElement, "syncApiKey");
            var agentIdRaw = GetString(doc.RootElement, "agentId");

            // A baseUrl that is not a valid absolute http(s) URL would poison the
            // stored blob: NormalizeOrigin on it stays null forever, the idempotency
            // scan could never match it, and the poller could not use it either.
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(tenantSlug)
                || string.IsNullOrWhiteSpace(agentApiKey) || string.IsNullOrWhiteSpace(syncApiKey)
                || !Guid.TryParse(agentIdRaw, out var agentId)
                || NormalizeOrigin(baseUrl) is null)
                return null;

            return new EnrollmentResponse(baseUrl!, tenantSlug!, agentId, agentApiKey!, syncApiKey!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Composes the "identitycenter" credential blob. Field names are LOAD-BEARING:
    /// IcAgentCommandPollerService and IdentityCenterCredentialReader parse exactly
    /// BaseUrl (server), ApiKey (shared sync key), AgentApiKey (per-agent key).
    /// EnrollUrl is additive (ignored by both readers): it records the origin the
    /// code was redeemed against so restart idempotency still holds when IC's
    /// baseUrl legitimately lives on a different host than the enroll endpoint.
    /// </summary>
    public static string ComposeCredentialBlob(EnrollmentResponse response, string? enrollUrl = null) =>
        JsonSerializer.Serialize(new
        {
            BaseUrl = response.BaseUrl,
            ApiKey = response.SyncApiKey,
            AgentApiKey = response.AgentApiKey,
            EnrollUrl = enrollUrl
        });

    /// <summary>Connected System name for the enrolled IC, kept Source-regex-safe.</summary>
    public static string BuildTenantName(string tenantSlug) =>
        IdentityCenterSourceName.Sanitize($"IdentityCenter-{tenantSlug}");

    /// <summary>
    /// scheme://host:port, lowercased, default ports applied (https://Host:443/ and
    /// https://host normalize identically). Null when not an absolute http(s) URL.
    /// </summary>
    public static string? NormalizeOrigin(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return $"{uri.Scheme}://{uri.Host}:{uri.Port}".ToLowerInvariant();
    }

    /// <summary>
    /// True when the stored credential blob's BaseUrl OR EnrollUrl points at the
    /// given normalized origin. Matching either keeps restart idempotency intact
    /// when IC's baseUrl is a different host than the enroll endpoint.
    /// </summary>
    public static bool CredentialMatchesOrigin(string credentialJson, string normalizedOrigin)
    {
        try
        {
            using var doc = JsonDocument.Parse(credentialJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var baseUrlOrigin = NormalizeOrigin(GetString(doc.RootElement, "BaseUrl"));
            var enrollUrlOrigin = NormalizeOrigin(GetString(doc.RootElement, "EnrollUrl"));
            return string.Equals(baseUrlOrigin, normalizedOrigin, StringComparison.OrdinalIgnoreCase)
                || string.Equals(enrollUrlOrigin, normalizedOrigin, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
