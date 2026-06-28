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

namespace Conduit.Connectors.IdentityCenter;

/// <summary>
/// IdentityCenter source. Pages through <c>GET /api/objects/query</c>, builds
/// a <see cref="ConnectorObject"/> per item, and stamps an incremental cursor
/// off the highest <c>modifiedAt</c> seen in the run.
/// </summary>
public sealed class IdentityCenterSource : IConnectorSource
{
    private readonly Guid _tenantId;
    private readonly IHttpClientFactory _httpFactory;
    private readonly CredentialProtector _protector;
    private readonly ILogger<IdentityCenterSource> _logger;

    public IdentityCenterSource(Guid tenantId, IHttpClientFactory httpFactory, CredentialProtector protector, ILogger<IdentityCenterSource> logger)
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
        => EnumerateInternalAsync(objectClass, scope, null, new Watermark(), cancellationToken);

    public Task<SyncEnumerationResult> EnumerateAsync(
        string objectClass,
        SyncProjectScope scope,
        SyncCursor? cursor,
        CancellationToken cancellationToken)
    {
        var watermark = new Watermark();
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
        string? modifiedSinceIso,
        Watermark watermark,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var creds = await IdentityCenterCredentialReader.ReadAsync(_protector, _tenantId, CredentialSide.Source)
            ?? throw new InvalidOperationException($"No 'identitycenter' credential for tenant {_tenantId}.");
        var client = IdentityCenterCredentialReader.BuildClient(_httpFactory, creds);

        // Translate Conduit's "User" / "Group" to IC's lowercased ObjectClass.
        var icClass = objectClass.ToLowerInvariant();

        // QueryExpression on the project scope (if set) overrides class.
        // scope.PageSize bounded to [1, 1000] (IC controller caps at 1000).
        var pageSize = scope.PageSize > 0 && scope.PageSize <= 1000 ? scope.PageSize : 200;
        var maxObjects = scope.MaxObjects ?? int.MaxValue;
        var emitted = 0;
        var page = 1;

        // Identities source surfaces a deterministic key (employeeId) the sink can
        // address rows by. Objects source keys on sourceUniqueId. The endpoint +
        // item shape differ per table; everything else (paging, watermark) is shared.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = creds.Table == IcTable.Identities
                ? $"{creds.BaseUrl}/api/identities/query?keyField=employeeId&page={page}&pageSize={pageSize}"
                : $"{creds.BaseUrl}/api/objects/query?objectClass={Uri.EscapeDataString(icClass)}&page={page}&pageSize={pageSize}";
            if (!string.IsNullOrWhiteSpace(modifiedSinceIso))
                url += $"&modifiedSince={Uri.EscapeDataString(modifiedSinceIso)}";

            var resp = await client.GetAsync(url, cancellationToken);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var hasMore = root.TryGetProperty("hasMore", out var hmEl) && hmEl.ValueKind == JsonValueKind.True;
            if (!root.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
                yield break;

            var pageEmitted = 0;
            foreach (var item in itemsEl.EnumerateArray())
            {
                if (emitted >= maxObjects) yield break;
                var converted = creds.Table == IcTable.Identities
                    ? ConvertIdentity(item, watermark)
                    : Convert(item, icClass, watermark);
                if (converted is null) continue;
                emitted++;
                pageEmitted++;
                yield return converted;
            }

            if (!hasMore || pageEmitted == 0) yield break;
            page++;
        }
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var creds = await IdentityCenterCredentialReader.ReadAsync(_protector, _tenantId, CredentialSide.Source);
            if (creds is null)
                return new ConnectorTestResult { IsSuccessful = false, Message = "No 'identitycenter' credential stored." };
            var client = IdentityCenterCredentialReader.BuildClient(_httpFactory, creds);
            var probeUrl = creds.Table == IcTable.Identities
                ? $"{creds.BaseUrl}/api/identities/query?keyField=employeeId&page=1&pageSize=1"
                : $"{creds.BaseUrl}/api/objects/query?objectClass=user&page=1&pageSize=1";
            var resp = await client.GetAsync(probeUrl, cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return new ConnectorTestResult { IsSuccessful = false, Message = $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}" };
            return new ConnectorTestResult { IsSuccessful = true, Message = $"Connected to {creds.BaseUrl} (table={creds.Table})." };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

    private static ConnectorObject? Convert(JsonElement item, string icClass, Watermark watermark)
    {
        var sourceUniqueId = Str(item, "sourceUniqueId");
        if (string.IsNullOrEmpty(sourceUniqueId)) return null;

        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = icClass,
            ["sourceUniqueId"] = sourceUniqueId,
            ["objectGuid"] = sourceUniqueId
        };

        Set(attrs, "id",                Str(item, "id"));
        Set(attrs, "displayName",       Str(item, "displayName"));
        Set(attrs, "cn",                Str(item, "cn"));
        Set(attrs, "dn",                Str(item, "dn"));
        Set(attrs, "userName",          Str(item, "username"));
        Set(attrs, "sAMAccountName",    Str(item, "username"));
        Set(attrs, "userPrincipalName", Str(item, "userPrincipalName"));
        if (item.TryGetProperty("isActive", out var actEl))
        {
            var active = actEl.ValueKind == JsonValueKind.True;
            attrs["isActive"] = active;
            attrs["accountEnabled"] = active;
        }

        // ModifiedAt feeds the watermark for cursor stamping.
        if (item.TryGetProperty("modifiedAt", out var mEl)
            && mEl.ValueKind == JsonValueKind.String
            && DateTime.TryParse(mEl.GetString(), null,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var mDt))
        {
            attrs["whenChanged"] = mDt.ToString("o");
            watermark.Observe(mDt);
        }

        // Merge in sparse ObjectAttributes returned by IC.
        if (item.TryGetProperty("attributes", out var attrEl) && attrEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in attrEl.EnumerateObject())
            {
                var v = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
                if (!string.IsNullOrEmpty(v)) attrs[prop.Name] = v;
            }
        }

        // Carry the object's own tag NAMES (Phase 2 tag carry-through). IC's
        // /api/objects/query surfaces these as a "tags" string array. We fold them into
        // the internal "_tags" pseudo-attribute (comma-joined) so an Objects→Identities
        // sync lands them on IdentityTags via the IC sink — ASSIGN-EXISTING-ONLY.
        var tagsCsv = JoinTags(item);
        if (tagsCsv is not null) attrs["_tags"] = tagsCsv;

        // Conduit's ConnectorObject uses ObjectClass casing "User"/"Group" — match
        // what the rest of the sink ecosystem expects.
        var canonical = icClass switch
        {
            "user" => "User",
            "group" => "Group",
            _ => icClass
        };

        return new ConnectorObject
        {
            SourceId = sourceUniqueId!,
            ObjectClass = canonical,
            Attributes = attrs
        };
    }

    /// <summary>
    /// Build a ConnectorObject from an IdentityQueryItem (table=Identities). The
    /// row's SourceId is its deterministic key (keyValue, e.g. employeeId) so an
    /// Identities→Identities round-trip is stable and an Identities→Objects sync
    /// carries a usable id. The full typed-column projection is flattened into
    /// attributes. ObjectClass is forced to "User" — Identities are always people.
    /// </summary>
    private static ConnectorObject? ConvertIdentity(JsonElement item, Watermark watermark)
    {
        var keyValue = Str(item, "keyValue");
        // Fall back to email/UPN/employeeId so a row with an empty primary key is
        // still addressable rather than silently dropped.
        var sourceId = keyValue
            ?? Str(item, "employeeId")
            ?? Str(item, "userPrincipalName")
            ?? Str(item, "primaryEmail");
        if (string.IsNullOrEmpty(sourceId)) return null;

        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = "user",
            ["keyField"] = Str(item, "keyField"),
            ["keyValue"] = keyValue,
        };

        Set(attrs, "id",                Str(item, "id"));
        Set(attrs, "displayName",       Str(item, "displayName"));
        Set(attrs, "firstName",         Str(item, "firstName"));
        Set(attrs, "lastName",          Str(item, "lastName"));
        Set(attrs, "primaryEmail",      Str(item, "primaryEmail"));
        Set(attrs, "email",             Str(item, "primaryEmail"));
        Set(attrs, "userPrincipalName", Str(item, "userPrincipalName"));
        Set(attrs, "userName",          Str(item, "username"));
        Set(attrs, "employeeId",        Str(item, "employeeId"));
        Set(attrs, "department",        Str(item, "department"));
        Set(attrs, "jobTitle",          Str(item, "jobTitle"));
        Set(attrs, "status",            Str(item, "status"));
        if (item.TryGetProperty("isActive", out var actEl))
        {
            var active = actEl.ValueKind == JsonValueKind.True;
            attrs["isActive"] = active;
            attrs["accountEnabled"] = active;
        }

        if (item.TryGetProperty("modifiedAt", out var mEl)
            && mEl.ValueKind == JsonValueKind.String
            && DateTime.TryParse(mEl.GetString(), null,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var mDt))
        {
            attrs["whenChanged"] = mDt.ToString("o");
            watermark.Observe(mDt);
        }

        // Flatten IC's typed-column projection (the "attributes" bag the IC query
        // endpoint returns) so any Identities column maps without a second call.
        if (item.TryGetProperty("attributes", out var attrEl) && attrEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in attrEl.EnumerateObject())
            {
                var v = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
                if (!string.IsNullOrEmpty(v)) attrs[prop.Name] = v;
            }
        }

        // Carry tag NAMES if the Identities query surfaces them (Phase 2 — forward-
        // compatible; the IC Identities /query does not emit "tags" today, the Objects
        // /query does). Folded into "_tags" so the IC sink lands them on IdentityTags.
        var tagsCsv = JoinTags(item);
        if (tagsCsv is not null) attrs["_tags"] = tagsCsv;

        return new ConnectorObject
        {
            SourceId = sourceId!,
            ObjectClass = "User",
            Attributes = attrs
        };
    }

    /// <summary>
    /// Read IC's "tags" string-array property off a query item and join the non-empty
    /// names with commas. Returns null when absent/empty so the caller can skip the
    /// "_tags" stamp entirely. Names flow as data only — never interpreted as SQL.
    /// </summary>
    private static string? JoinTags(JsonElement item)
    {
        if (!item.TryGetProperty("tags", out var tagsEl) || tagsEl.ValueKind != JsonValueKind.Array)
            return null;
        var names = new List<string>();
        foreach (var t in tagsEl.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.String) continue;
            var s = t.GetString();
            if (!string.IsNullOrWhiteSpace(s)) names.Add(s.Trim());
        }
        return names.Count > 0 ? string.Join(",", names) : null;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Set(Dictionary<string, object?> dict, string key, object? value)
    {
        if (value is null) return;
        if (value is string s && string.IsNullOrEmpty(s)) return;
        dict[key] = value;
    }

    internal sealed class Watermark
    {
        public DateTime? Max { get; private set; }
        public void Observe(DateTime dt) { if (Max is null || dt > Max.Value) Max = dt; }
        public string IsoSafeOrNow() => (Max ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    }
}
