using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.Scim;

/// <summary>
/// SCIM 2.0 source — paged via ?startIndex=&amp;count=. Mirrors IC's SCIM
/// connector key extraction: id is SCIM "id" (UUID); attributes flattened into
/// userName/emails[0]/name.givenName/name.familyName/etc.
/// </summary>
public sealed class ScimSource : IConnectorSource
{
    private readonly Guid _tenantId;
    private readonly IHttpClientFactory _httpFactory;
    private readonly CredentialProtector _protector;
    private readonly ILogger<ScimSource> _logger;

    public ScimSource(Guid tenantId, IHttpClientFactory httpFactory, CredentialProtector protector, ILogger<ScimSource> logger)
    {
        _tenantId = tenantId;
        _httpFactory = httpFactory;
        _protector = protector;
        _logger = logger;
    }

    public IAsyncEnumerable<ConnectorObject> ReadAsync(
        string objectClass,
        SyncProjectScope scope,
        CancellationToken cancellationToken)
        => EnumerateInternalAsync(objectClass, scope, null, new HighWatermark(), cancellationToken);

    /// <summary>
    /// Phase 2 incremental: builds a SCIM filter combining the project's
    /// QueryExpression (if any) with <c>meta.lastModified gt "&lt;cursor.Token&gt;"</c>.
    /// Tracks the maximum meta.lastModified seen and returns it as the new
    /// cursor.
    /// </summary>
    public Task<SyncEnumerationResult> EnumerateAsync(
        string objectClass,
        SyncProjectScope scope,
        SyncCursor? cursor,
        CancellationToken cancellationToken)
    {
        var watermark = new HighWatermark();
        var isIncremental = cursor is not null && !string.IsNullOrWhiteSpace(cursor.Token);
        var stream = EnumerateInternalAsync(objectClass, scope, cursor?.Token, watermark, cancellationToken);

        return Task.FromResult(new SyncEnumerationResult
        {
            Objects = stream,
            ResolveNewCursor = () => new SyncCursor
            {
                Token = watermark.IsoSafeOrNow(),
                IssuedAt = DateTime.UtcNow
            },
            IsIncremental = isIncremental
        });
    }

    private async IAsyncEnumerable<ConnectorObject> EnumerateInternalAsync(
        string objectClass,
        SyncProjectScope scope,
        string? sinceIsoUtc,
        HighWatermark watermark,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var creds = await ScimCredentialReader.ReadAsync(_protector, _tenantId)
            ?? throw new InvalidOperationException($"No 'scim' credential for tenant {_tenantId}.");
        var client = ScimCredentialReader.BuildClient(_httpFactory, creds);
        var pageSize = scope.PageSize > 0 && scope.PageSize <= 200 ? scope.PageSize : 100;
        var endpoint = string.Equals(objectClass, "Group", StringComparison.OrdinalIgnoreCase)
            ? creds.GroupEndpoint
            : creds.UserEndpoint;

        // Combine project filter + Phase 2 watermark filter.
        string? combined = null;
        if (!string.IsNullOrWhiteSpace(scope.QueryExpression)) combined = scope.QueryExpression;
        if (!string.IsNullOrWhiteSpace(sinceIsoUtc))
        {
            var increment = $"meta.lastModified gt \"{sinceIsoUtc}\"";
            combined = combined is null ? increment : $"({combined}) and {increment}";
        }
        var filterPart = combined is null ? string.Empty : $"&filter={Uri.EscapeDataString(combined)}";

        var startIndex = 1;
        var totalResults = int.MaxValue;
        var emitted = 0;
        while (startIndex <= totalResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = $"{creds.BaseUrl}{endpoint}?startIndex={startIndex}&count={pageSize}{filterPart}";
            var resp = await client.GetAsync(url, cancellationToken);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
            if (doc.RootElement.TryGetProperty("totalResults", out var totEl) && totEl.TryGetInt32(out var tot))
                totalResults = tot;
            if (!doc.RootElement.TryGetProperty("Resources", out var resourcesEl)
                || resourcesEl.ValueKind != JsonValueKind.Array)
                yield break;

            var batch = new List<ConnectorObject>();
            foreach (var el in resourcesEl.EnumerateArray())
            {
                // Track high watermark from meta.lastModified.
                if (el.TryGetProperty("meta", out var metaEl)
                    && metaEl.TryGetProperty("lastModified", out var lmEl)
                    && lmEl.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(lmEl.GetString(), null,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var dt))
                {
                    watermark.Observe(dt);
                }

                var converted = string.Equals(objectClass, "Group", StringComparison.OrdinalIgnoreCase)
                    ? ConvertGroup(el)
                    : ConvertUser(el);
                if (converted != null) batch.Add(converted);
            }

            foreach (var converted in batch)
            {
                if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                emitted++;
                yield return converted;
            }
            if (batch.Count == 0) yield break;
            startIndex += pageSize;
        }
    }

    private sealed class HighWatermark
    {
        public DateTime? Max { get; private set; }
        public bool Sealed { get; set; }

        public void Observe(DateTime dt)
        {
            if (Max is null || dt > Max.Value) Max = dt;
        }

        /// <summary>
        /// ISO 8601 string of the highest observed lastModified, OR "now" when
        /// the watermark wasn't populated (empty page, or filter excluded all).
        /// Either way the next run resumes safely with no gap and no double-read.
        /// </summary>
        public string IsoSafeOrNow()
            => (Max ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var creds = await ScimCredentialReader.ReadAsync(_protector, _tenantId);
            if (creds is null) return new ConnectorTestResult { IsSuccessful = false, Message = "No 'scim' credential stored." };
            var client = ScimCredentialReader.BuildClient(_httpFactory, creds);
            var resp = await client.GetAsync($"{creds.BaseUrl}{creds.UserEndpoint}?startIndex=1&count=1", cancellationToken);
            return resp.IsSuccessStatusCode
                ? new ConnectorTestResult { IsSuccessful = true, Message = $"SCIM endpoint reachable at {creds.BaseUrl}." }
                : new ConnectorTestResult { IsSuccessful = false, Message = $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}" };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

    private static ConnectorObject? ConvertUser(JsonElement el)
    {
        var id = Str(el, "id");
        if (string.IsNullOrEmpty(id)) return null;
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = "user",
            ["id"] = id,
            ["objectGuid"] = id
        };
        Set(attrs, "userName", Str(el, "userName"));
        Set(attrs, "displayName", Str(el, "displayName"));
        Set(attrs, "externalId", Str(el, "externalId"));
        Set(attrs, "title", Str(el, "title"));
        Set(attrs, "jobTitle", Str(el, "title"));
        Set(attrs, "userType", Str(el, "userType"));
        Set(attrs, "preferredLanguage", Str(el, "preferredLanguage"));
        Set(attrs, "locale", Str(el, "locale"));
        Set(attrs, "timezone", Str(el, "timezone"));
        if (el.TryGetProperty("active", out var actEl))
        {
            if (actEl.ValueKind == JsonValueKind.True) { attrs["active"] = true; attrs["accountEnabled"] = true; attrs["userAccountControl"] = 512; }
            else if (actEl.ValueKind == JsonValueKind.False) { attrs["active"] = false; attrs["accountEnabled"] = false; attrs["userAccountControl"] = 514; }
        }
        if (el.TryGetProperty("name", out var nameEl))
        {
            var gn = Str(nameEl, "givenName");
            var fn = Str(nameEl, "familyName");
            var formatted = Str(nameEl, "formatted");
            Set(attrs, "givenName", gn);
            Set(attrs, "surname", fn);
            Set(attrs, "sn", fn);
            Set(attrs, "familyName", fn);
            if (string.IsNullOrEmpty(Str(el, "displayName")) && !string.IsNullOrEmpty(formatted))
                attrs["displayName"] = formatted;
        }
        if (el.TryGetProperty("emails", out var emailsEl) && emailsEl.ValueKind == JsonValueKind.Array)
        {
            JsonElement? primary = null;
            foreach (var em in emailsEl.EnumerateArray())
            {
                if (em.TryGetProperty("primary", out var pEl) && pEl.ValueKind == JsonValueKind.True) { primary = em; break; }
            }
            primary ??= emailsEl.GetArrayLength() > 0 ? emailsEl[0] : (JsonElement?)null;
            if (primary.HasValue)
            {
                var v = Str(primary.Value, "value");
                Set(attrs, "email", v);
                Set(attrs, "mail", v);
            }
        }
        if (el.TryGetProperty("phoneNumbers", out var phEl) && phEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in phEl.EnumerateArray())
            {
                var type = Str(p, "type")?.ToLowerInvariant();
                var val = Str(p, "value");
                switch (type)
                {
                    case "work": Set(attrs, "telephoneNumber", val); break;
                    case "mobile": Set(attrs, "mobilePhone", val); Set(attrs, "mobile", val); break;
                    case "home": Set(attrs, "homePhone", val); break;
                }
            }
        }
        // Enterprise schema extension.
        if (el.TryGetProperty("urn:ietf:params:scim:schemas:extension:enterprise:2.0:User", out var entEl)
            && entEl.ValueKind == JsonValueKind.Object)
        {
            Set(attrs, "employeeNumber", Str(entEl, "employeeNumber"));
            Set(attrs, "department", Str(entEl, "department"));
            Set(attrs, "division", Str(entEl, "division"));
            Set(attrs, "costCenter", Str(entEl, "costCenter"));
            Set(attrs, "company", Str(entEl, "organization"));
            Set(attrs, "companyName", Str(entEl, "organization"));
            if (entEl.TryGetProperty("manager", out var mgrEl))
                Set(attrs, "manager", Str(mgrEl, "value"));
        }
        return new ConnectorObject { SourceId = id!, ObjectClass = "User", Attributes = attrs };
    }

    private static ConnectorObject? ConvertGroup(JsonElement el)
    {
        var id = Str(el, "id");
        if (string.IsNullOrEmpty(id)) return null;
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = "group",
            ["id"] = id,
            ["objectGuid"] = id
        };
        Set(attrs, "displayName", Str(el, "displayName"));
        Set(attrs, "cn", Str(el, "displayName"));
        Set(attrs, "externalId", Str(el, "externalId"));
        if (el.TryGetProperty("members", out var memEl) && memEl.ValueKind == JsonValueKind.Array)
        {
            var memberIds = new List<string>();
            foreach (var m in memEl.EnumerateArray())
            {
                var v = Str(m, "value");
                if (!string.IsNullOrEmpty(v)) memberIds.Add(v!);
            }
            if (memberIds.Count > 0) attrs["members"] = memberIds;
        }
        return new ConnectorObject { SourceId = id!, ObjectClass = "Group", Attributes = attrs };
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Set(Dictionary<string, object?> d, string k, object? v)
    {
        if (v is null) return;
        if (v is string s && string.IsNullOrEmpty(s)) return;
        d[k] = v;
    }
}
