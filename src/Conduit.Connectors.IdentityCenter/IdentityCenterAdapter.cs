using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
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
    public string DisplayName => "IdentityCenter";
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
        SupportsAssignGroupOwner = true
    };

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes { get; } = new[]
    {
        new CredentialTypeInfo
        {
            Name = "identitycenter",
            DisplayName = "IdentityCenter API",
            Description = "IdentityCenter base URL + admin-scoped API key.",
            Fields = new[]
            {
                new CredentialFieldSpec { Key = "BaseUrl", Label = "Base URL", Placeholder = "https://identitycenter.local:7048", IsRequired = true },
                new CredentialFieldSpec { Key = "ApiKey",  Label = "API Key",  IsRequired = true, IsSecret = true },
                // The sync source/sink always authenticate with ApiKey. AgentApiKey is
                // consumed ONLY by IcAgentCommandPollerService (claim + heartbeat) — IC's
                // TenantDataPolicy denies per-agent keys on the data endpoints, so the
                // two channels need separate keys.
                new CredentialFieldSpec
                {
                    Key = "AgentApiKey", Label = "Agent API Key", IsSecret = true,
                    Help = "Per-agent key for the IC command channel; leave blank to use the API key (legacy mode)."
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
            throw new InvalidOperationException(
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
