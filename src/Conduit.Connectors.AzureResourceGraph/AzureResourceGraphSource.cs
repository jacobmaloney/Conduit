using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.AzureResourceGraph;

/// <summary>
/// Azure Resource Graph source. Raw HTTP against the ARG KQL endpoint (no
/// Azure.ResourceManager SDK). Token is acquired via ClientSecretCredential
/// scoped to ARM (https://management.azure.com/.default) and attached as Bearer
/// ONLY after the request host passes <see cref="ArgHttp.IsArmHost"/>.
///
/// KQL queries are STATIC constants — no user input is ever concatenated into a
/// query. Optional subscription/management-group scoping is passed via the ARG
/// POST body's "subscriptions"/"managementGroups" arrays after GUID validation.
///
/// Paging follows the response $skipToken until it is null/empty. HTTP 429 honors
/// Retry-After (capped retries). HTTP 403 (missing Reader role) is logged with a
/// clear hint and the class is skipped rather than aborting the whole run.
/// </summary>
public sealed class AzureResourceGraphSource : IConnectorSource
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string ArgEndpoint = "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01";
    private const int PageSize = 1000;
    private const int MaxThrottleRetries = 5;
    // Hard ceilings so an unbounded $skipToken loop can't run forever when
    // scope.MaxObjects is null. The adapter sets SuppressDeleteDetection=true, so
    // an aborted partial read never produces tombstones — capping is safe.
    private const int MaxPages = 5000;
    private const int MaxRows = MaxPages * PageSize;

    // STATIC KQL. resultFormat=objectArray is requested in the body so rows come
    // back as objects. Never interpolate into these.
    internal const string SubscriptionsKql =
        "ResourceContainers | where type =~ 'microsoft.resources/subscriptions' | project id, name, subscriptionId, tenantId, properties";
    internal const string ResourcesKql =
        "Resources | project id, name, type, location, subscriptionId, resourceGroup, sku, tags, properties";

    public const string SubscriptionClass = "azuresubscription";
    public const string ResourceClass = "azureresource";

    private readonly Guid _tenantId;
    private readonly CredentialProtector _protector;
    private readonly ILogger<AzureResourceGraphSource> _logger;
    private readonly HttpMessageHandler? _handlerOverride;

    public AzureResourceGraphSource(Guid tenantId, CredentialProtector protector, ILogger<AzureResourceGraphSource> logger)
        : this(tenantId, protector, logger, null)
    {
    }

    /// <summary>
    /// Test seam: lets a unit test inject a stub <see cref="HttpMessageHandler"/>
    /// so NO network call is made. The registered (public) constructor leaves this
    /// null and the source builds a default HttpClient per call.
    /// </summary>
    internal AzureResourceGraphSource(
        Guid tenantId,
        CredentialProtector protector,
        ILogger<AzureResourceGraphSource> logger,
        HttpMessageHandler? handlerOverride)
    {
        _tenantId = tenantId;
        _protector = protector;
        _logger = logger;
        _handlerOverride = handlerOverride;
    }

    public async IAsyncEnumerable<ConnectorObject> ReadAsync(
        string objectClass,
        SyncProjectScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (kql, isResource) =
            string.Equals(objectClass, SubscriptionClass, StringComparison.OrdinalIgnoreCase) ? (SubscriptionsKql, false) :
            string.Equals(objectClass, ResourceClass, StringComparison.OrdinalIgnoreCase) ? (ResourcesKql, true) :
            (null, false);

        if (kql is null) yield break; // unknown class — emit nothing (matches discovery-style sources)

        var creds = await AzureResourceGraphCredentialReader.ReadAsync(_protector, _tenantId)
            ?? throw new InvalidOperationException($"No '{AzureResourceGraphCredentialReader.CredentialName}' credential stored for tenant {_tenantId}.");

        var (subscriptions, managementGroups) = ParseScopeFilter(creds.ScopeFilter);

        using var http = CreateHttpClient();
        var token = await AcquireTokenAsync(creds, cancellationToken);

        await foreach (var obj in EnumeratePagesAsync(http, token, kql, isResource, scope, subscriptions, managementGroups, objectClass, cancellationToken))
            yield return obj;
    }

    /// <summary>
    /// Pages the ARG endpoint and yields mapped objects. Test seam: takes a ready
    /// HttpClient + token so a stub handler can drive it with no protector / network.
    /// </summary>
    internal async IAsyncEnumerable<ConnectorObject> EnumeratePagesAsync(
        HttpClient http,
        string token,
        string kql,
        bool isResource,
        SyncProjectScope scope,
        IReadOnlyList<string> subscriptions,
        IReadOnlyList<string> managementGroups,
        string objectClass,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var emitted = 0;
        var pages = 0;
        string? skipToken = null;
        do
        {
            if (pages >= MaxPages || emitted >= MaxRows)
            {
                _logger.LogWarning(
                    "AzureResourceGraph: hit hard paging cap (MaxPages={MaxPages}, MaxRows={MaxRows}) on class {ObjectClass} — stopping after {Emitted} rows. Narrow the scope to read the full set.",
                    MaxPages, MaxRows, objectClass, emitted);
                yield break;
            }
            pages++;

            JsonDocument? doc;
            try
            {
                doc = await PostQueryAsync(http, token, kql, subscriptions, managementGroups, skipToken, objectClass, cancellationToken);
            }
            catch (ForbiddenException)
            {
                _logger.LogWarning(
                    "AzureResourceGraph: skipping class {ObjectClass} — Azure RBAC Reader role missing on the target scope for this service principal (403).",
                    objectClass);
                yield break;
            }

            if (doc is null) yield break;

            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in data.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                        var obj = isResource ? ConvertResource(row) : ConvertSubscription(row);
                        if (obj is null) continue;
                        emitted++;
                        yield return obj;
                    }
                }

                skipToken = root.TryGetProperty("$skipToken", out var st) && st.ValueKind == JsonValueKind.String
                    ? st.GetString()
                    : null;
            }
        }
        while (!string.IsNullOrEmpty(skipToken));
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var creds = await AzureResourceGraphCredentialReader.ReadAsync(_protector, _tenantId);
            if (creds is null)
                return new ConnectorTestResult { IsSuccessful = false, Message = $"No '{AzureResourceGraphCredentialReader.CredentialName}' credential stored." };

            var (subscriptions, managementGroups) = ParseScopeFilter(creds.ScopeFilter);
            using var http = CreateHttpClient();
            var token = await AcquireTokenAsync(creds, cancellationToken);

            JsonDocument? doc;
            try
            {
                doc = await PostQueryAsync(http, token, SubscriptionsKql, subscriptions, managementGroups, skipToken: null, objectClass: SubscriptionClass, cancellationToken, top: 1);
            }
            catch (ForbiddenException)
            {
                return new ConnectorTestResult
                {
                    IsSuccessful = false,
                    Message = "Azure RBAC Reader role missing on the target scope for this service principal (403)."
                };
            }

            if (doc is null)
                return new ConnectorTestResult { IsSuccessful = false, Message = "Empty response from Azure Resource Graph." };

            using (doc)
            {
                var count = 0;
                if (doc.RootElement.TryGetProperty("count", out var cEl) && cEl.ValueKind == JsonValueKind.Number)
                    count = cEl.GetInt32();
                else if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    count = data.GetArrayLength();

                return new ConnectorTestResult
                {
                    IsSuccessful = true,
                    Message = $"Connected. Service principal can read {count} subscription(s)."
                };
            }
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

    // ─── token + http ─────────────────────────────────────────────────────

    private HttpClient CreateHttpClient() =>
        _handlerOverride is not null ? new HttpClient(_handlerOverride, disposeHandler: false) : new HttpClient();

    private static async Task<string> AcquireTokenAsync(AzureResourceGraphCredentials creds, CancellationToken cancellationToken)
    {
        var credential = new ClientSecretCredential(creds.TenantId, creds.ClientId, creds.ClientSecret);
        var token = await credential.GetTokenAsync(new TokenRequestContext(new[] { ArmScope }), cancellationToken);
        return token.Token;
    }

    /// <summary>
    /// POSTs one ARG page. Builds the body with the static KQL + (optionally) the
    /// validated subscriptions / managementGroups arrays + paging options. Attaches
    /// the bearer token only after <see cref="ArgHttp.IsArmHost"/> passes. Honors
    /// 429 Retry-After with capped retries; throws <see cref="ForbiddenException"/>
    /// on 403 so the caller can skip the class.
    /// </summary>
    private async Task<JsonDocument?> PostQueryAsync(
        HttpClient http,
        string token,
        string kql,
        IReadOnlyList<string> subscriptions,
        IReadOnlyList<string> managementGroups,
        string? skipToken,
        string objectClass,
        CancellationToken cancellationToken,
        int top = PageSize)
    {
        var body = BuildRequestBody(kql, subscriptions, managementGroups, skipToken, top);

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Post, ArgEndpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            // SECURITY: never attach the bearer token unless the target host is
            // the commercial ARM endpoint. Throw before sending if it is not.
            if (!ArgHttp.IsArmHost(request.RequestUri!))
                throw new InvalidOperationException($"Refusing to attach ARM bearer token to non-ARM host '{request.RequestUri!.Host}'.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await http.SendAsync(request, cancellationToken);

            if (response.StatusCode == (HttpStatusCode)429)
            {
                if (attempt >= MaxThrottleRetries)
                    throw new HttpRequestException($"Azure Resource Graph throttled (429) after {MaxThrottleRetries} retries for class {objectClass}.");
                var delay = ResolveRetryAfter(response);
                _logger.LogWarning(
                    "AzureResourceGraph: throttled (429) on class {ObjectClass}, retry {Attempt}/{Max} after {Delay}s.",
                    objectClass, attempt + 1, MaxThrottleRetries, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new ForbiddenException();

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
    }

    // A hostile/misconfigured server returning Retry-After: 86400 must not stall the
    // run for a day. Clamp to [1s, 60s]; fall back to 5s when no usable header.
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinRetryAfter = TimeSpan.FromSeconds(1);

    private static TimeSpan ResolveRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        TimeSpan resolved;
        if (ra?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            resolved = delta;
        }
        else if (ra?.Date is { } date && date - DateTimeOffset.UtcNow > TimeSpan.Zero)
        {
            resolved = date - DateTimeOffset.UtcNow;
        }
        else
        {
            return TimeSpan.FromSeconds(5);
        }

        if (resolved > MaxRetryAfter) return MaxRetryAfter;
        if (resolved < MinRetryAfter) return MinRetryAfter;
        return resolved;
    }

    /// <summary>
    /// Builds the ARG POST body. The KQL is the verbatim static constant. Scope is
    /// expressed ONLY via the body arrays — never via string concatenation into KQL.
    /// </summary>
    internal static string BuildRequestBody(
        string kql,
        IReadOnlyList<string> subscriptions,
        IReadOnlyList<string> managementGroups,
        string? skipToken,
        int top)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("query", kql);

            if (subscriptions.Count > 0)
            {
                w.WriteStartArray("subscriptions");
                foreach (var s in subscriptions) w.WriteStringValue(s);
                w.WriteEndArray();
            }
            if (managementGroups.Count > 0)
            {
                w.WriteStartArray("managementGroups");
                foreach (var g in managementGroups) w.WriteStringValue(g);
                w.WriteEndArray();
            }

            w.WriteStartObject("options");
            w.WriteString("resultFormat", "objectArray");
            w.WriteNumber("$top", top);
            if (!string.IsNullOrEmpty(skipToken)) w.WriteString("$skipToken", skipToken);
            w.WriteEndObject();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // ─── scope parsing (GUID-validated, NEVER injected into KQL) ───────────

    /// <summary>
    /// Splits the optional comma-separated ScopeFilter into a subscriptions GUID
    /// list and a managementGroups list. Subscription entries MUST be valid GUIDs;
    /// non-GUID entries are skipped with a warning and never added (so nothing
    /// untrusted reaches the request). A single non-GUID token is treated as a
    /// management-group id.
    /// </summary>
    internal (IReadOnlyList<string> Subscriptions, IReadOnlyList<string> ManagementGroups) ParseScopeFilter(string? scopeFilter)
    {
        var subs = new List<string>();
        var mgmtGroups = new List<string>();
        if (string.IsNullOrWhiteSpace(scopeFilter)) return (subs, mgmtGroups);

        foreach (var rawEntry in scopeFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(rawEntry, out var g))
            {
                subs.Add(g.ToString());
            }
            else if (LooksLikeManagementGroupId(rawEntry))
            {
                mgmtGroups.Add(rawEntry);
            }
            else
            {
                _logger.LogWarning(
                    "AzureResourceGraph: ScopeFilter entry '{Entry}' is not a subscription GUID or a valid management-group id — skipping it.",
                    rawEntry);
            }
        }
        return (subs, mgmtGroups);
    }

    // Management-group ids are operator-chosen strings (letters, digits, dot, dash,
    // underscore, parens). Reject anything outside that set so a malformed entry
    // can't slip into the request body.
    private static bool LooksLikeManagementGroupId(string value)
    {
        foreach (var ch in value)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' or '(' or ')'))
                return false;
        }
        return value.Length > 0;
    }

    // ─── converters ───────────────────────────────────────────────────────

    private static ConnectorObject? ConvertSubscription(JsonElement row)
    {
        var id = GetString(row, "id");
        if (string.IsNullOrEmpty(id)) return null;

        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = SubscriptionClass,
            ["id"] = id
        };
        Set(attrs, "displayName", GetString(row, "name"));
        Set(attrs, "subscriptionId", GetString(row, "subscriptionId"));
        Set(attrs, "tenantId", GetString(row, "tenantId"));

        if (row.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            Set(attrs, "state", GetString(props, "state"));

        return new ConnectorObject { SourceId = id, ObjectClass = SubscriptionClass, Attributes = attrs };
    }

    private static ConnectorObject? ConvertResource(JsonElement row)
    {
        var id = GetString(row, "id");
        if (string.IsNullOrEmpty(id)) return null;

        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = ResourceClass,
            ["id"] = id
        };
        Set(attrs, "name", GetString(row, "name"));
        Set(attrs, "resourceType", GetString(row, "type"));
        Set(attrs, "location", GetString(row, "location"));
        Set(attrs, "subscriptionId", GetString(row, "subscriptionId"));
        Set(attrs, "resourceGroup", GetString(row, "resourceGroup"));

        // sku — compact: prefer sku.name, else serialize the object/value compactly.
        if (row.TryGetProperty("sku", out var sku) && sku.ValueKind != JsonValueKind.Null)
        {
            if (sku.ValueKind == JsonValueKind.Object)
            {
                var skuName = GetString(sku, "name");
                Set(attrs, "sku", string.IsNullOrEmpty(skuName) ? sku.GetRawText() : skuName);
            }
            else if (sku.ValueKind == JsonValueKind.String)
            {
                Set(attrs, "sku", sku.GetString());
            }
        }

        // tags — flatten object to "k=v;k=v".
        if (row.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
        {
            var flat = FlattenTags(tags);
            if (!string.IsNullOrEmpty(flat)) attrs["tags"] = flat;
        }

        // ── Azure Hybrid Benefit extraction (RAW — no core-count math) ──
        if (row.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            var licenseType = GetString(props, "licenseType");
            if (!string.IsNullOrEmpty(licenseType))
            {
                attrs["licenseType"] = licenseType;
                attrs["azureHybridBenefit"] = IsHybridBenefit(licenseType);
            }

            // best-effort size / vCores (null-safe, no math)
            var vmSize = GetNestedString(props, "hardwareProfile", "vmSize");
            if (!string.IsNullOrEmpty(vmSize))
            {
                attrs["size"] = vmSize;
            }
            else
            {
                var slo = GetString(props, "currentServiceObjectiveName")
                       ?? GetString(props, "requestedServiceObjectiveName");
                if (!string.IsNullOrEmpty(slo)) attrs["size"] = slo;
            }

            // sku.capacity (SQL vCores) — best-effort.
            if (row.TryGetProperty("sku", out var skuEl) && skuEl.ValueKind == JsonValueKind.Object &&
                skuEl.TryGetProperty("capacity", out var cap) && cap.ValueKind == JsonValueKind.Number)
            {
                attrs["vCores"] = cap.GetRawText();
            }
        }

        return new ConnectorObject { SourceId = id, ObjectClass = ResourceClass, Attributes = attrs };
    }

    /// <summary>
    /// Azure Hybrid Benefit flag from properties.licenseType. Windows AHB markers
    /// ("Windows_Server"/"Windows_Client") and SQL AHB markers ("BasePrice",
    /// "AHUB", "BYOL") = true. "LicenseIncluded" (PAYG) = false. Unknown = false.
    /// </summary>
    internal static bool IsHybridBenefit(string licenseType)
    {
        if (string.Equals(licenseType, "LicenseIncluded", StringComparison.OrdinalIgnoreCase))
            return false;
        return licenseType.ToLowerInvariant() switch
        {
            "windows_server" => true,
            "windows_client" => true,
            "baseprice" => true,
            "ahub" => true,
            "byol" => true,
            _ => false
        };
    }

    private static string FlattenTags(JsonElement tags)
    {
        var sb = new StringBuilder();
        foreach (var tag in tags.EnumerateObject())
        {
            if (sb.Length > 0) sb.Append(';');
            sb.Append(tag.Name).Append('=');
            sb.Append(tag.Value.ValueKind == JsonValueKind.String ? tag.Value.GetString() : tag.Value.GetRawText());
        }
        return sb.ToString();
    }

    private static string? GetString(JsonElement el, string property) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? GetNestedString(JsonElement el, string outer, string inner) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(outer, out var o) && o.ValueKind == JsonValueKind.Object
            ? GetString(o, inner)
            : null;

    private static void Set(Dictionary<string, object?> dict, string key, object? value)
    {
        if (value is null) return;
        if (value is string s && string.IsNullOrEmpty(s)) return;
        dict[key] = value;
    }

    private sealed class ForbiddenException : Exception { }
}

/// <summary>
/// Host-allowlist helper for ARM. Kept internal + static so unit tests can call
/// <see cref="IsArmHost"/> directly. Commercial cloud ONLY — sovereign clouds
/// (US Gov, China) are rejected for this build.
/// </summary>
internal static class ArgHttp
{
    private const string CommercialArmHost = "management.azure.com";

    /// <summary>
    /// True only for HTTPS requests to the EXACT commercial ARM host
    /// (case-insensitive). Rejects subdomain-spoofing ("management.azure.com.evil.com",
    /// "evilmanagement.azure.com"), other clouds (graph.microsoft.com), sovereign
    /// ARM hosts, and any non-HTTPS scheme.
    /// </summary>
    internal static bool IsArmHost(Uri uri)
    {
        if (uri is null) return false;
        if (!uri.IsAbsoluteUri) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(uri.Host, CommercialArmHost, StringComparison.OrdinalIgnoreCase);
    }
}
