using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;

namespace Conduit.Connectors.SharePoint;

/// <summary>
/// SharePoint Online / Teams adapter — source-only (Phase 1.5 P2 scope). Uses
/// Microsoft Graph via ClientSecretCredential. Credentials shape mirrors
/// EntraID: { TenantId, ClientId, ClientSecret }. Object classes: "Site",
/// "Team". Sink for SharePoint is intentionally not shipped — write-back is
/// usually orchestrated through Graph Sites API differently per scenario.
/// </summary>
public sealed class SharePointAdapter : IConnectorAdapter
{
    public string SystemType => "SharePoint";
    public string DisplayName => "SharePoint Online";
    public bool SupportsSource => true;
    public bool SupportsSink => false;

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes { get; } = new[]
    {
        new CredentialTypeInfo
        {
            Name = "sharepoint",
            DisplayName = "SharePoint App Registration",
            Description = "Same shape as Entra — Azure AD app reg with Sites.Read.All / Group.Read.All.",
            Fields = new[]
            {
                new CredentialFieldSpec { Key = "TenantId", Label = "Tenant ID", IsRequired = true, Placeholder = "00000000-0000-0000-0000-000000000000" },
                new CredentialFieldSpec { Key = "ClientId", Label = "Client ID (Application ID)", IsRequired = true },
                new CredentialFieldSpec { Key = "ClientSecret", Label = "Client Secret", IsRequired = true, IsSecret = true },
            }
        }
    };

    private readonly CredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public SharePointAdapter(CredentialProtector protector, ILoggerFactory loggerFactory)
    {
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new SharePointSource(tenantId, _protector, _loggerFactory.CreateLogger<SharePointSource>());

    public IConnectorSink? CreateSink(Guid tenantId) => null;
}

internal sealed record SharePointCredentials(string TenantId, string ClientId, string ClientSecret);

internal static class SharePointCredentialReader
{
    public const string CredentialName = "sharepoint";

    public static async Task<SharePointCredentials?> ReadAsync(CredentialProtector p, Guid tenantId)
    {
        // Phase 3: honor per-project credential-name override (source first, sink fallback).
        var name = CredentialNameContext.Resolve(CredentialName, CredentialSide.Source);
        var raw = await p.RetrieveAsync(tenantId, name);
        if (string.IsNullOrEmpty(raw))
        {
            var sinkName = CredentialNameContext.Resolve(CredentialName, CredentialSide.Sink);
            if (!string.Equals(sinkName, name, StringComparison.OrdinalIgnoreCase))
                raw = await p.RetrieveAsync(tenantId, sinkName);
        }
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var t = doc.RootElement.TryGetProperty("TenantId", out var tEl) ? tEl.GetString() : null;
            var c = doc.RootElement.TryGetProperty("ClientId", out var cEl) ? cEl.GetString() : null;
            var s = doc.RootElement.TryGetProperty("ClientSecret", out var sEl) ? sEl.GetString() : null;
            if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(c) || string.IsNullOrEmpty(s)) return null;
            return new SharePointCredentials(t!, c!, s!);
        }
        catch { return null; }
    }

    public static GraphServiceClient CreateClient(SharePointCredentials creds)
    {
        var credential = new ClientSecretCredential(creds.TenantId, creds.ClientId, creds.ClientSecret);
        return new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
    }
}

public sealed class SharePointSource : IConnectorSource
{
    private readonly Guid _tenantId;
    private readonly CredentialProtector _protector;
    private readonly ILogger<SharePointSource> _logger;

    public SharePointSource(Guid tenantId, CredentialProtector protector, ILogger<SharePointSource> logger)
    {
        _tenantId = tenantId;
        _protector = protector;
        _logger = logger;
    }

    // ── Least-privilege app-registration scopes (application permissions) ────────
    //   Sites.Read.All   — /sites enumeration + the getSharePointSiteUsageDetail
    //                       storage report used to enrich "site" objects.
    //   Group.Read.All   — Microsoft 365 / Teams group reads.
    //   Reports.Read.All — usage reports (site storage). Optional; a 403 only
    //                      drops storage enrichment, the site still emits.
    // Per-class 403 handling: a missing scope warns + skips that class/report,
    // it never aborts the whole run.

    public async IAsyncEnumerable<ConnectorObject> ReadAsync(
        string objectClass,
        SyncProjectScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var creds = await SharePointCredentialReader.ReadAsync(_protector, _tenantId)
            ?? throw new InvalidOperationException($"No 'sharepoint' credential for tenant {_tenantId}.");
        var client = SharePointCredentialReader.CreateClient(creds);
        var emitted = 0;

        // SharePoint group enumeration is an HONEST DEFERRAL. There is no clean
        // Graph v1.0 path for per-site SharePoint groups (Owners/Members/Visitors
        // and custom site groups): /sites/{id}/permissions returns role
        // assignments by appId/principal, not the SharePoint group objects, and the
        // SP-group beta surface is unreliable. SharePoint groups live in the
        // classic SharePoint REST API (_api/web/sitegroups), which this Graph-only
        // connector deliberately does not call. Rather than fabricate a broken
        // Graph request, we yield nothing and say so plainly.
        if (string.Equals(objectClass, "sharepointgroup", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "SharePoint: 'sharepointgroup' enumeration is deferred — per-site SharePoint groups require the " +
                "SharePoint REST API (_api/web/sitegroups), not Graph v1.0. This Graph-only connector emits nothing for this class.");
            yield break;
        }

        if (string.Equals(objectClass, "Team", StringComparison.OrdinalIgnoreCase))
        {
            var teams = await client.Teams.GetAsync(req => req.QueryParameters.Top = 100, cancellationToken);
            while (teams?.Value != null)
            {
                foreach (var t in teams.Value)
                {
                    if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                    var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["objectClass"] = "team",
                        ["id"] = t.Id,
                        ["objectGuid"] = t.Id,
                        ["displayName"] = t.DisplayName,
                        ["cn"] = t.DisplayName,
                        ["description"] = t.Description,
                        ["webUrl"] = t.WebUrl
                    };
                    emitted++;
                    yield return new ConnectorObject
                    {
                        SourceId = t.Id ?? string.Empty,
                        ObjectClass = "Team",
                        Attributes = attrs
                    };
                }
                if (string.IsNullOrEmpty(teams.OdataNextLink)) break;
                teams = await client.Teams.WithUrl(teams.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
            }
            yield break;
        }

        // Default: Site. The /sites list carries NO storage figures — those come
        // from the getSharePointSiteUsageDetail usage report, which we fetch once
        // and join by site URL. A 403 on the report (missing Reports.Read.All)
        // skips enrichment with a warning but every site still emits.
        var storageByUrl = await TryGetSiteStorageAsync(creds, cancellationToken);

        var sites = await client.Sites.GetAsync(req => req.QueryParameters.Top = 100, cancellationToken);
        while (sites?.Value != null)
        {
            foreach (var s in sites.Value)
            {
                if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["objectClass"] = "site",
                    ["id"] = s.Id,
                    ["objectGuid"] = s.Id,
                    ["displayName"] = s.DisplayName,
                    ["cn"] = s.DisplayName,
                    ["description"] = s.Description,
                    ["webUrl"] = s.WebUrl,
                    ["name"] = s.Name,
                    ["whenCreated"] = s.CreatedDateTime?.ToString("o")
                };
                if (storageByUrl is not null && !string.IsNullOrEmpty(s.WebUrl) &&
                    storageByUrl.TryGetValue(s.WebUrl!, out var storage))
                {
                    if (storage.UsedBytes.HasValue) attrs["StorageUsedBytes"] = storage.UsedBytes.Value;
                    if (storage.AllocatedBytes.HasValue) attrs["StorageAllocatedBytes"] = storage.AllocatedBytes.Value;
                    if (storage.FileCount.HasValue) attrs["FileCount"] = storage.FileCount.Value;
                }
                emitted++;
                yield return new ConnectorObject
                {
                    SourceId = s.Id ?? string.Empty,
                    ObjectClass = "Site",
                    Attributes = attrs
                };
            }
            if (string.IsNullOrEmpty(sites.OdataNextLink)) break;
            sites = await client.Sites.WithUrl(sites.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }
    }

    private readonly record struct SiteStorage(long? UsedBytes, long? AllocatedBytes, long? FileCount);

    /// <summary>
    /// Fetches getSharePointSiteUsageDetail(period='D30') as JSON and indexes
    /// storage figures by site URL. Returns null when the report 403s (missing
    /// Reports.Read.All) so the caller can warn + skip enrichment without aborting.
    /// Uses a raw bearer token because the Graph SDK report endpoints are awkward.
    /// </summary>
    private async Task<Dictionary<string, SiteStorage>?> TryGetSiteStorageAsync(
        SharePointCredentials creds, CancellationToken cancellationToken)
    {
        var credential = new ClientSecretCredential(creds.TenantId, creds.ClientId, creds.ClientSecret);
        var ctx = new Azure.Core.TokenRequestContext(new[] { "https://graph.microsoft.com/.default" });
        var token = (await credential.GetTokenAsync(ctx, cancellationToken)).Token;

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        var byUrl = new Dictionary<string, SiteStorage>(StringComparer.OrdinalIgnoreCase);
        var url = "https://graph.microsoft.com/beta/reports/getSharePointSiteUsageDetail(period='D30')?$format=application/json";

        while (!string.IsNullOrEmpty(url))
        {
            using var resp = await http.GetAsync(url, cancellationToken);
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning(
                    "SharePoint: skipping site-storage enrichment — app registration lacks scope Reports.Read.All (403). Sites still emit without storage.");
                return null;
            }
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var siteUrl = el.TryGetProperty("siteUrl", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    if (string.IsNullOrEmpty(siteUrl)) continue;
                    byUrl[siteUrl!] = new SiteStorage(
                        ReadLong(el, "storageUsedInBytes"),
                        ReadLong(el, "storageAllocatedInBytes"),
                        ReadLong(el, "fileCount"));
                }
            }

            var nextLink = root.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
            if (!string.IsNullOrEmpty(nextLink) && !IsGraphHost(nextLink!))
            {
                _logger.LogWarning(
                    "SharePoint: refusing to follow non-Graph nextLink host {Host} on site-storage report; stopping paging.",
                    SafeHost(nextLink!));
                break;
            }
            url = nextLink;
        }

        return byUrl;
    }

    /// <summary>
    /// True only when <paramref name="url"/> is an absolute HTTPS URL whose host is
    /// graph.microsoft.com (or a subdomain ending in ".graph.microsoft.com"). Guards
    /// the @odata.nextLink follow so a tampered/off-host nextLink can never receive
    /// the bearer token carried on HttpClient.DefaultRequestHeaders.
    /// </summary>
    private static bool IsGraphHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return false;
        var host = uri.Host;
        return string.Equals(host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".graph.microsoft.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Host for log output only — never the full URL (it can carry tokens).</summary>
    private static string SafeHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(unparseable)";

    private static long? ReadLong(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var s)) return s;
        return null;
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var creds = await SharePointCredentialReader.ReadAsync(_protector, _tenantId);
            if (creds is null) return new ConnectorTestResult { IsSuccessful = false, Message = "No 'sharepoint' credential stored." };
            var client = SharePointCredentialReader.CreateClient(creds);
            var root = await client.Sites["root"].GetAsync(cancellationToken: cancellationToken);
            return new ConnectorTestResult { IsSuccessful = true, Message = $"Root site: {root?.WebUrl ?? "(unknown)"}." };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }
}
