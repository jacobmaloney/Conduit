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

namespace Conduit.Connectors.Okta;

/// <summary>
/// Okta source — paged enumeration via Link: rel="next" header. Object classes:
/// "User" (default), "Group". Attribute parity with IC's OktaQueryService: emits
/// both Okta-native names (login, email) and AD-compatible aliases
/// (sAMAccountName, sn, l, st, etc.) so downstream sinks see consistent keys.
/// </summary>
public sealed class OktaSource : IConnectorSource
{
    private readonly Guid _tenantId;
    private readonly IHttpClientFactory _httpFactory;
    private readonly CredentialProtector _protector;
    private readonly ILogger<OktaSource> _logger;

    public OktaSource(Guid tenantId, IHttpClientFactory httpFactory, CredentialProtector protector, ILogger<OktaSource> logger)
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
        => EnumerateInternalAsync(objectClass, scope, null, new OktaWatermark(), cancellationToken);

    /// <summary>
    /// Phase 2 incremental: combines scope.QueryExpression with
    /// <c>lastUpdated gt "&lt;cursor&gt;"</c> in Okta's filter grammar. Cursor is an
    /// ISO timestamp of the highest lastUpdated seen during this run.
    /// </summary>
    public Task<SyncEnumerationResult> EnumerateAsync(
        string objectClass,
        SyncProjectScope scope,
        SyncCursor? cursor,
        CancellationToken cancellationToken)
    {
        var watermark = new OktaWatermark();
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
        OktaWatermark watermark,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var creds = await OktaCredentialReader.ReadAsync(_protector, _tenantId)
            ?? throw new InvalidOperationException($"No 'okta' credential for tenant {_tenantId}.");
        var client = OktaCredentialReader.BuildClient(_httpFactory, creds);

        var pageSize = scope.PageSize > 0 && scope.PageSize <= 200 ? scope.PageSize : 200;

        // Build filter (project filter + Phase 2 incremental clause).
        string? combined = null;
        if (!string.IsNullOrWhiteSpace(scope.QueryExpression)) combined = scope.QueryExpression;
        if (!string.IsNullOrWhiteSpace(sinceIsoUtc))
        {
            var increment = $"lastUpdated gt \"{sinceIsoUtc}\"";
            combined = combined is null ? increment : $"({combined}) and {increment}";
        }
        var filterPart = combined is null ? "" : $"&filter={Uri.EscapeDataString(combined)}";

        string url = string.Equals(objectClass, "Group", StringComparison.OrdinalIgnoreCase)
            ? $"{creds.OrgUrl}/api/v1/groups?limit={pageSize}{filterPart}"
            : $"{creds.OrgUrl}/api/v1/users?limit={pageSize}{filterPart}";

        var emitted = 0;
        await foreach (var obj in EnumeratePagedAsync(client, url, objectClass, watermark, cancellationToken))
        {
            if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
            emitted++;
            yield return obj;
        }
    }

    internal sealed class OktaWatermark
    {
        public DateTime? Max { get; private set; }
        public void Observe(DateTime dt) { if (Max is null || dt > Max.Value) Max = dt; }
        public string IsoSafeOrNow() => (Max ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var creds = await OktaCredentialReader.ReadAsync(_protector, _tenantId);
            if (creds is null) return new ConnectorTestResult { IsSuccessful = false, Message = "No 'okta' credential stored." };
            var client = OktaCredentialReader.BuildClient(_httpFactory, creds);
            var resp = await client.GetAsync($"{creds.OrgUrl}/api/v1/users?limit=1", cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return new ConnectorTestResult { IsSuccessful = false, Message = $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}" };
            return new ConnectorTestResult { IsSuccessful = true, Message = $"Connected to {creds.OrgUrl}." };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

    private static async IAsyncEnumerable<ConnectorObject> EnumeratePagedAsync(
        HttpClient client, string url, string objectClass, OktaWatermark watermark,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? next = url;
        while (!string.IsNullOrEmpty(next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resp = await client.GetAsync(next, cancellationToken);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    // Phase 2: track watermark off lastUpdated for incremental cursor.
                    if (el.TryGetProperty("lastUpdated", out var luEl)
                        && luEl.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(luEl.GetString(), null,
                            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var dt))
                    {
                        watermark.Observe(dt);
                    }

                    var converted = string.Equals(objectClass, "Group", StringComparison.OrdinalIgnoreCase)
                        ? ConvertGroup(el)
                        : ConvertUser(el);
                    if (converted != null) yield return converted;
                }
            }
            next = null;
            if (resp.Headers.TryGetValues("Link", out var links))
            {
                foreach (var link in links)
                {
                    if (!link.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase)) continue;
                    var start = link.IndexOf('<') + 1;
                    var end = link.IndexOf('>');
                    if (start > 0 && end > start) { next = link[start..end]; break; }
                }
            }
        }
    }

    private static ConnectorObject? ConvertUser(JsonElement el)
    {
        var id = Str(el, "id");
        if (string.IsNullOrEmpty(id)) return null;
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = id,
            ["objectGuid"] = id,
            ["objectClass"] = "user"
        };
        var status = Str(el, "status");
        if (status != null)
        {
            var active = status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);
            attrs["status"] = status;
            attrs["active"] = active;
            attrs["accountEnabled"] = active;
            attrs["userAccountControl"] = active ? 512 : 514;
        }
        Set(attrs, "lastLogin", Str(el, "lastLogin"));
        Set(attrs, "whenCreated", Str(el, "created"));
        Set(attrs, "whenChanged", Str(el, "lastUpdated"));
        if (el.TryGetProperty("profile", out var p))
        {
            var login = Str(p, "login");
            Set(attrs, "login", login);
            Set(attrs, "userPrincipalName", login);
            Set(attrs, "userName", login);
            Set(attrs, "sAMAccountName", login?.Split('@')[0]);
            Set(attrs, "cn", login);
            Set(attrs, "email", Str(p, "email"));
            Set(attrs, "mail", Str(p, "email"));
            var first = Str(p, "firstName");
            var last = Str(p, "lastName");
            Set(attrs, "givenName", first);
            Set(attrs, "surname", last);
            Set(attrs, "sn", last);
            Set(attrs, "familyName", last);
            Set(attrs, "displayName", Str(p, "displayName") ?? ($"{first} {last}".Trim()));
            Set(attrs, "department", Str(p, "department"));
            Set(attrs, "title", Str(p, "title"));
            Set(attrs, "jobTitle", Str(p, "title"));
            Set(attrs, "company", Str(p, "organization"));
            Set(attrs, "companyName", Str(p, "organization"));
            Set(attrs, "telephoneNumber", Str(p, "primaryPhone"));
            Set(attrs, "mobilePhone", Str(p, "mobilePhone"));
            Set(attrs, "mobile", Str(p, "mobilePhone"));
            Set(attrs, "streetAddress", Str(p, "streetAddress"));
            Set(attrs, "city", Str(p, "city"));
            Set(attrs, "l", Str(p, "city"));
            Set(attrs, "state", Str(p, "state"));
            Set(attrs, "st", Str(p, "state"));
            Set(attrs, "postalCode", Str(p, "zipCode"));
            Set(attrs, "country", Str(p, "countryCode"));
            Set(attrs, "co", Str(p, "countryCode"));
            Set(attrs, "employeeId", Str(p, "employeeNumber"));
            Set(attrs, "employeeNumber", Str(p, "employeeNumber"));
            Set(attrs, "division", Str(p, "division"));
            var mgr = Str(p, "managerId");
            if (!string.IsNullOrEmpty(mgr)) attrs["manager"] = mgr;
        }
        return new ConnectorObject { SourceId = id!, ObjectClass = "User", Attributes = attrs };
    }

    private static ConnectorObject? ConvertGroup(JsonElement el)
    {
        var id = Str(el, "id");
        if (string.IsNullOrEmpty(id)) return null;
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = id,
            ["objectGuid"] = id,
            ["objectClass"] = "group"
        };
        var name = el.TryGetProperty("profile", out var p) ? Str(p, "name") : null;
        var desc = el.TryGetProperty("profile", out var p2) ? Str(p2, "description") : null;
        Set(attrs, "displayName", name);
        Set(attrs, "cn", name);
        Set(attrs, "description", desc);
        Set(attrs, "groupType", Str(el, "type"));
        Set(attrs, "Type", "Security");
        Set(attrs, "whenCreated", Str(el, "created"));
        Set(attrs, "whenChanged", Str(el, "lastUpdated"));
        return new ConnectorObject { SourceId = id!, ObjectClass = "Group", Attributes = attrs };
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Set(Dictionary<string, object?> dict, string key, object? value)
    {
        if (value is null) return;
        if (value is string s && string.IsNullOrEmpty(s)) return;
        dict[key] = value;
    }
}
