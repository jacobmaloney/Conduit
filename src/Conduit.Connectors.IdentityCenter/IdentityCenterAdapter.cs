using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.IdentityCenter;

/// <summary>
/// IdentityCenter adapter. Source + sink — IC exposes both a paged GET
/// (<c>/api/objects/query</c>) and a batched POST (<c>/api/objects/bulk</c>),
/// so a tenant of this type can sit on either side of a Sync Project.
/// </summary>
/// <remarks>
/// Authentication is IC's standard <c>X-API-Key</c> header. The IC API does
/// NOT echo Conduit's bulk writes back out to AD / Entra — that's deliberate
/// and lives in <c>ObjectsController.BulkUpsert</c>'s docs.
/// </remarks>
public sealed class IdentityCenterAdapter : IConnectorAdapter
{
    public string SystemType => "IdentityCenter";
    public string DisplayName => "Identity Center";
    public bool SupportsSource => true;
    public bool SupportsSink => true;

    public ConnectorCapabilities Capabilities { get; } = new()
    {
        SupportsBulk = true,
        MaxBatchSize = 500,
        SupportsIncremental = true,
        // Phase 7: IC is the canonical sink for person-aware workflow steps.
        SupportsPersonMatch = true,
        SupportsPersonCreate = true,
        SupportsAssignManager = true,
        SupportsAssignGroupOwner = true,
        // Phase 1 inbound proxy: IC implements CreateAsync (single-item write via
        // the bulk endpoints). This is what makes a SCIM/REST POST to an IC-typed
        // connection actually create an Object (or Identity) in IC.
        SupportsCreate = true,
        // Phase 2 inbound proxy: IC implements UpdateAsync via its /api/objects/bulk
        // (or /api/identities/bulk) MERGE endpoint. The bulk upsert is natively a
        // PARTIAL merge of the supplied attributes, so PATCH maps exactly; PUT is
        // honored as a partial merge of the attributes the SCIM payload carried
        // rather than clearing omitted columns (IC's bulk has no "replace-all-
        // attributes" mode). See IdentityCenterSink.UpdateAsync.
        SupportsUpdate = true,
        // Phase 2 inbound proxy: IC implements DeleteAsync as a REVERSIBLE tombstone
        // soft-delete (honors IC's 50% safety cap; Identities table returns Skipped
        // as it has no tombstone endpoint). See IdentityCenterSink.DeleteAsync.
        SupportsDelete = true,
        // Phase 8: IC has the dedicated bulk ingest endpoints for the deeper
        // governance data classes, and implements IGroupMembershipEmittingSink.
        // These are CAPABILITY facts, not a license flag — the IC-CONNECTION
        // license still gates whether an IC connection can be used at all, but
        // the steps themselves are free and run against any sink that advertises
        // them. Today IC is the only sink that does.
        SupportsLicenseIngest = true,
        SupportsSignInLogIngest = true,
        SupportsUsageReportIngest = true,
        SupportsAppRoleIngest = true,
        SupportsGroupMembership = true
    };

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes { get; } = new[]
    {
        new CredentialTypeInfo
        {
            Name = "identitycenter",
            DisplayName = "Identity Center",
            Description = "Identity Center (on-prem) base URL + admin-scoped API key.",
            Fields = new[]
            {
                new CredentialFieldSpec
                {
                    Key = "BaseUrl", Label = "Base URL", Placeholder = "http://identitycenter.local:8080", IsRequired = true,
                    Help = "Your on-prem Identity Center API base URL — e.g. http://identitycenter.local:8080. Match the scheme+port the API actually listens on (default plain HTTP on :8080; the :7048 web portal is NOT the API). Tip: enroll with a code (--enroll-url/--enroll-code) and this is filled in automatically."
                },
                new CredentialFieldSpec
                {
                    Key = "ApiKey", Label = "API Key (required)", IsRequired = true, IsSecret = true,
                    Help = "IC admin-scoped X-API-Key. Authenticates every sync source/sink read and bulk write. This is the only key a sync connection needs."
                },
                // The sync source/sink always authenticate with ApiKey. AgentApiKey is
                // consumed ONLY by IcAgentCommandPollerService (claim + heartbeat) — IC's
                // TenantDataPolicy denies per-agent keys on the data endpoints, so the
                // two channels need separate keys. Optional: leave blank unless this
                // Conduit instance is registered as an IC remote agent.
                new CredentialFieldSpec
                {
                    Key = "AgentApiKey", Label = "Agent API Key (optional)", IsSecret = true,
                    Help = "Only for the IC agent command channel — when this Conduit is registered as a remote agent that runs IC-dispatched scans/writes (claim + heartbeat). Leave blank for a plain sync connection; blank falls back to the API Key (legacy mode)."
                },
                // V22: the IC table (Objects | Identities) is NO LONGER a connection
                // credential field. It moved onto the Sync Project's source/sink
                // endpoints (SyncProject.SourceTable / SinkTable) so ONE IdentityCenter
                // connection can be source=Identities AND sink=Objects in a single
                // project (IC/Identities → IC/Objects). The connector reads it per side
                // from IdentityCenterTableContext, which the orchestrator stamps from
                // the project. Picked once in the New/Edit Sync Project wizard.
            }
        }
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly CredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public IdentityCenterAdapter(IHttpClientFactory httpFactory, CredentialProtector protector, ILoggerFactory loggerFactory)
    {
        _httpFactory = httpFactory;
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new IdentityCenterSource(tenantId, _httpFactory, _protector, _loggerFactory.CreateLogger<IdentityCenterSource>());

    public IConnectorSink? CreateSink(Guid tenantId) =>
        new IdentityCenterSink(tenantId, _httpFactory, _protector, _loggerFactory.CreateLogger<IdentityCenterSink>());
}

/// <summary>Which IC table an endpoint targets. Objects = directory accounts
/// (/api/objects/*); Identities = people golden records (/api/identities/*).</summary>
internal enum IcTable { Objects, Identities }

/// <summary>
/// The stored credential row exists but could not be parsed. Its message is authored in
/// this file and quotes nothing from the blob, which is what makes it the one credential
/// failure whose text may be shown to an operator verbatim — every other read failure can
/// carry a connection string, a SQL login, or a key-file path and must not reach a page.
/// </summary>
public sealed class IdentityCenterCredentialFormatException : InvalidOperationException
{
    public IdentityCenterCredentialFormatException(string message) : base(message) { }
}

internal sealed record IdentityCenterCredentials(string BaseUrl, string ApiKey, IcTable Table);

internal static class IdentityCenterCredentialReader
{
    public const string CredentialName = "identitycenter";

    /// <summary>
    /// HttpClient timeout for the IC sink. Raised from the prior 120s to 300s so a
    /// large bulk batch against a slow target SQL completes rather than being
    /// cancelled by HttpClient.Timeout mid-write. See <see cref="BuildClient"/>.
    /// </summary>
    private const int IcSinkTimeoutSeconds = 300;

    /// <summary>
    /// Reads the IC credential for the GIVEN side. The credential blob now carries
    /// ONLY BaseUrl + ApiKey; the table (Objects | Identities) is resolved per side
    /// from <see cref="IdentityCenterTableContext"/>, which the orchestrator stamps
    /// from the project's SourceTable / SinkTable (V22). This is what lets one IC
    /// connection source from Identities AND sink into Objects in one project.
    /// Falls back to the other side's blob only when the requested side has none
    /// stored (single-credential installs) — the TABLE always tracks the requested
    /// side regardless of which blob supplied the URL/key.
    /// </summary>
    public static async Task<IdentityCenterCredentials?> ReadAsync(
        CredentialProtector p, Guid tenantId, CredentialSide side = CredentialSide.Source)
    {
        var name = CredentialNameContext.Resolve(CredentialName, side);
        var raw = await p.RetrieveAsync(tenantId, name);
        if (string.IsNullOrEmpty(raw))
        {
            var other = side == CredentialSide.Source ? CredentialSide.Sink : CredentialSide.Source;
            var otherName = CredentialNameContext.Resolve(CredentialName, other);
            if (!string.Equals(otherName, name, StringComparison.OrdinalIgnoreCase))
                raw = await p.RetrieveAsync(tenantId, otherName);
        }
        // A null/empty raw means NO credential row exists for this tenant+name —
        // the genuine "not configured" case; the caller surfaces "No credential".
        if (string.IsNullOrEmpty(raw)) return null;

        // A row EXISTS but failed to parse is a DIFFERENT failure (corrupt /
        // hand-stamped blob). Returning null here previously masqueraded as
        // "no credential", which is misleading and was painful to diagnose.
        // Parse leniently and, when the strict JSON parse fails on an otherwise
        // recoverable blob (e.g. a manually stored {Key:value} without quotes),
        // fall back to a tolerant extractor rather than silently failing a run.
        string? url, key;
        if (!TryParseCredentialBlob(raw, out url, out key))
            throw new IdentityCenterCredentialFormatException(
                "The stored 'identitycenter' credential exists but is malformed " +
                "(could not read BaseUrl + ApiKey). Re-save the IdentityCenter connection's " +
                "credential in Connected Systems to repair it.");

        // V22: table comes from the per-side project endpoint (ambient context the
        // orchestrator stamps from SyncProject.SourceTable / SinkTable), NOT the
        // credential blob. Explicit "Identities" → Identities; unset / unknown /
        // "Objects" → Objects (back-compat default).
        var tableKey = IdentityCenterTableContext.Resolve(side);
        var table = string.Equals(tableKey, "Identities", StringComparison.OrdinalIgnoreCase)
            ? IcTable.Identities
            : IcTable.Objects;
        return new IdentityCenterCredentials(url!.TrimEnd('/'), key!, table);
    }

    /// <summary>
    /// Reads BaseUrl + ApiKey from a stored credential blob. Prefers a strict
    /// JSON parse; on failure (a malformed blob — e.g. one hand-stored without
    /// quoting) falls back to a tolerant key/value scan so an otherwise usable
    /// credential doesn't silently fail an entire sync run. Returns false only
    /// when neither path can recover both fields.
    /// </summary>
    internal static bool TryParseCredentialBlob(string raw, out string? baseUrl, out string? apiKey)
    {
        baseUrl = null;
        apiKey = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            baseUrl = doc.RootElement.TryGetProperty("BaseUrl", out var uEl) ? uEl.GetString() : null;
            apiKey = doc.RootElement.TryGetProperty("ApiKey", out var kEl) ? kEl.GetString() : null;
        }
        catch (JsonException)
        {
            baseUrl = ExtractLoose(raw, "BaseUrl");
            apiKey = ExtractLoose(raw, "ApiKey");
        }
        return !string.IsNullOrEmpty(baseUrl) && !string.IsNullOrEmpty(apiKey);
    }

    /// <summary>
    /// Last-resort extractor for a malformed brace blob like
    /// <c>{BaseUrl:http://h:5062,ApiKey:abc}</c> (no quotes). Reads the value
    /// after <c>field:</c> up to the next top-level comma or closing brace.
    /// </summary>
    private static string? ExtractLoose(string raw, string field)
    {
        var idx = raw.IndexOf(field, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var colon = raw.IndexOf(':', idx + field.Length);
        if (colon < 0) return null;
        var start = colon + 1;
        var end = start;
        while (end < raw.Length && raw[end] != ',' && raw[end] != '}') end++;
        if (end <= start) return null;
        return raw.Substring(start, end - start).Trim().Trim('"');
    }

    public static HttpClient BuildClient(IHttpClientFactory factory, IdentityCenterCredentials creds)
    {
        var client = factory.CreateClient("IdentityCenterConnector");
        // A full bulk batch (up to 1000 objects, each with ~20 attributes) against a
        // slow target SQL can take minutes. 120s was tripping HttpClient.Timeout on
        // .56 before the IC /api/objects/bulk N+1 was collapsed; keep 300s as headroom
        // so a slow-but-progressing batch is never cancelled mid-write.
        client.Timeout = TimeSpan.FromSeconds(IcSinkTimeoutSeconds);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // IC's ApiKeyAuthenticationHandler reads X-API-Key.
        client.DefaultRequestHeaders.Remove("X-API-Key");
        client.DefaultRequestHeaders.Add("X-API-Key", creds.ApiKey);
        return client;
    }
}

/// <summary>Why a tag-vocabulary fetch produced the list it did.</summary>
/// <remarks>
/// <see cref="Ok"/> with an EMPTY <see cref="IcTagVocabulary.Tags"/> is a valid, non-error
/// outcome: IC answered and genuinely has no tags defined. Every other member means the
/// vocabulary is UNKNOWN — callers must not infer anything about a tag's existence from it,
/// and that includes <see cref="Loading"/>, which is the state of not having asked yet.
/// </remarks>
public enum IcTagFetchStatus
{
    Ok,
    NoSinkSelected,
    NoCredentials,
    Unreachable,
    Unauthorized,
    Forbidden,
    EndpointMissing,
    Error,
    /// <summary>A fetch is in flight. Owned by the caller, never returned by the fetcher.</summary>
    Loading
}

/// <summary>
/// The tag vocabulary of an IdentityCenter connection plus WHY it looks the way it does.
/// <paramref name="Detail"/> carries the resolved BaseUrl for the reachability/auth outcomes
/// (the UI names the host in its message) and the failure text for <see cref="IcTagFetchStatus.Error"/>.
/// </summary>
public sealed record IcTagVocabulary(
    IReadOnlyList<string> Tags,
    IcTagFetchStatus Status,
    string? Detail);

/// <summary>
/// Fetches the tag vocabulary from an IdentityCenter connection (<c>GET /api/objects/tags</c>)
/// so Conduit's per-step tag picker can offer real, existing tag names. Tagging into IC is
/// assign-existing-only — IC resolves names against its own Tags rows and silently skips
/// unknown ones — so picking from the live list is the only honest affordance. Every failure
/// comes back as an <see cref="IcTagFetchStatus"/> the UI can explain; the sole exception that
/// escapes is cancellation of the CALLER's token, which means nobody is waiting for an answer.
/// </summary>
public sealed class IdentityCenterTagFetcher
{
    /// <summary>
    /// Timeout for the vocabulary fetch. Deliberately NOT the sink's 300s bulk-write budget:
    /// this is a small interactive read behind an open modal, and against an unresponsive IC
    /// the operator has to be told "can't reach it" in seconds, not minutes. Applied as a
    /// linked <see cref="CancellationTokenSource"/> so the shared client's timeout — which
    /// the bulk-write path depends on — is untouched.
    /// </summary>
    private static readonly TimeSpan TagFetchTimeout = TimeSpan.FromSeconds(12);

    private readonly IHttpClientFactory _httpFactory;
    private readonly CredentialProtector _protector;
    private readonly ILogger<IdentityCenterTagFetcher> _logger;

    public IdentityCenterTagFetcher(
        IHttpClientFactory httpFactory, CredentialProtector protector, ILogger<IdentityCenterTagFetcher> logger)
    {
        _httpFactory = httpFactory;
        _protector = protector;
        _logger = logger;
    }

    /// <summary>
    /// Returns the sorted, de-duped tag names available in the IdentityCenter connection
    /// bound to <paramref name="tenantId"/>. An empty list here is ambiguous (no tags, or no
    /// fetch); call <see cref="GetTagVocabularyAsync"/> when the caller needs to tell those apart.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetTagsAsync(Guid tenantId, CancellationToken ct = default)
        => (await GetTagVocabularyAsync(tenantId, ct)).Tags;

    /// <summary>
    /// Status-bearing fetch. Distinguishes "IC has no tags" (<see cref="IcTagFetchStatus.Ok"/>
    /// with an empty list) from every way the vocabulary can be UNKNOWN — unreachable, 401,
    /// 403, an IC too old to expose the endpoint, or no stored credential. A credential row
    /// that exists but is malformed comes back as <see cref="IcTagFetchStatus.Error"/> carrying
    /// the repair message, which is a different problem from "not configured".
    /// </summary>
    public async Task<IcTagVocabulary> GetTagVocabularyAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            return new IcTagVocabulary(Array.Empty<string>(), IcTagFetchStatus.NoSinkSelected, null);

        IdentityCenterCredentials? creds;
        try
        {
            creds = await IdentityCenterCredentialReader.ReadAsync(_protector, tenantId, CredentialSide.Sink);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IdentityCenter tag fetch: credential for tenant {TenantId} could not be read.", tenantId);
            return new IcTagVocabulary(Array.Empty<string>(), IcTagFetchStatus.Error, SafeDetail(ex));
        }

        if (creds is null)
            return new IcTagVocabulary(Array.Empty<string>(), IcTagFetchStatus.NoCredentials, null);

        var baseUrl = creds.BaseUrl;
        using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        fetchCts.CancelAfter(TagFetchTimeout);
        var fetchCt = fetchCts.Token;
        try
        {
            var client = IdentityCenterCredentialReader.BuildClient(_httpFactory, creds);
            using var resp = await client.GetAsync($"{baseUrl}/api/objects/tags", fetchCt);
            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                var status = code switch
                {
                    401 => IcTagFetchStatus.Unauthorized,
                    403 => IcTagFetchStatus.Forbidden,
                    404 => IcTagFetchStatus.EndpointMissing,
                    _ => IcTagFetchStatus.Error
                };
                _logger.LogWarning(
                    "IdentityCenter tag fetch for tenant {TenantId} returned {StatusCode} from {BaseUrl}.",
                    tenantId, code, baseUrl);
                return new IcTagVocabulary(
                    Array.Empty<string>(),
                    status,
                    status == IcTagFetchStatus.Error ? $"{code} {resp.ReasonPhrase}" : baseUrl);
            }

            var json = await resp.Content.ReadAsStringAsync(fetchCt);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tags", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new IcTagVocabulary(
                    Array.Empty<string>(), IcTagFetchStatus.Error,
                    "IdentityCenter returned an unexpected response shape for the tag list.");

            var names = arr.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Zero tags off a 200 is NOT a failure — IC simply has none defined yet.
            return new IcTagVocabulary(names, IcTagFetchStatus.Ok, baseUrl);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "IdentityCenter tag fetch: {BaseUrl} unreachable for tenant {TenantId}.", baseUrl, tenantId);
            return new IcTagVocabulary(Array.Empty<string>(), IcTagFetchStatus.Unreachable, baseUrl);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Our short vocabulary timeout, or HttpClient.Timeout — either way IC did not
            // answer in time and the vocabulary is unknown, which is what the UI must say.
            // A cancellation that came from the CALLER's token is a different event: it
            // means nobody is waiting for this answer, so it propagates rather than
            // painting a "can't reach IdentityCenter" claim the operator never earned.
            _logger.LogWarning(ex, "IdentityCenter tag fetch: {BaseUrl} timed out for tenant {TenantId}.", baseUrl, tenantId);
            return new IcTagVocabulary(Array.Empty<string>(), IcTagFetchStatus.Unreachable, baseUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not fetch IdentityCenter tags for tenant {TenantId}.", tenantId);
            return new IcTagVocabulary(Array.Empty<string>(), IcTagFetchStatus.Error, SafeDetail(ex));
        }
    }

    /// <summary>
    /// The operator-facing sentence for a failure. <see cref="IcTagVocabulary.Detail"/> is
    /// rendered into the page, and an exception message can carry a connection string, a SQL
    /// login name, or a key-file path — so the real exception goes to the log and the UI gets
    /// a fixed sentence chosen by TYPE. The malformed-credential message is the one that
    /// passes through: it is authored in this file and quotes nothing from the blob.
    /// </summary>
    private static string SafeDetail(Exception ex) => ex switch
    {
        IdentityCenterCredentialFormatException => ex.Message,
        System.Security.Cryptography.CryptographicException =>
            "The stored credential could not be decrypted. Re-save the IdentityCenter connection's credential in Connected Systems.",
        System.Data.Common.DbException =>
            "Conduit could not read the stored credential from its own database. Check Conduit's database connection, then retry.",
        UnauthorizedAccessException or System.IO.IOException =>
            "Conduit could not read the stored credential from disk. See the Conduit log for detail.",
        InvalidOperationException =>
            "Conduit could not read the stored credential for this connection. See the Conduit log for detail.",
        JsonException =>
            "IdentityCenter returned a tag list Conduit could not read.",
        _ => "The tag list could not be read. See the Conduit log for detail."
    };
}
