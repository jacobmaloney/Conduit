using System;
using System.Collections.Generic;
using System.Linq;
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
/// EntraID: { TenantId, ClientId, ClientSecret }. Object classes: "Site" (with a
/// parentSiteId hierarchy ref), "Team" (with member edges), "channel" (teamId
/// parent ref), and "channelfile" (a bounded set of top-level channel files,
/// channelId parent ref). Sink for SharePoint is intentionally not shipped — write-back is
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
    //   Sites.Read.All     — /sites enumeration + /sites/{id}/sites subsite
    //                        hierarchy + the getSharePointSiteUsageDetail storage
    //                        report used to enrich "site" objects.
    //   Group.Read.All     — Microsoft 365 / Teams group reads.
    //   Reports.Read.All   — usage reports (site storage). Optional; a 403 only
    //                        drops storage enrichment, the site still emits.
    //   Team.ReadBasic.All — /teams listing ("team" class spine).
    //   TeamMember.Read.All — /teams/{id}/members (team membership edges).
    //   Channel.ReadBasic.All — /teams/{id}/channels ("channel" class spine).
    //   Files.Read.All     — channel filesFolder + drive items ("channelfile").
    // Per-class 403 handling: a missing scope on a SPINE listing (teams, channels)
    // aborts that class loudly; a 403 on an ENRICHMENT (members, channel files,
    // subsite hierarchy, storage) warns + drops only those columns/children.

    // Channel-file fetch is bounded — directory libraries are unbounded, so we
    // emit at most this many top-level file refs per channel (no recursion).
    internal const int MaxChannelFiles = 50;

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

        // ── team: list teams, fetch members per team, emit membership edges ──────
        if (string.Equals(objectClass, "Team", StringComparison.OrdinalIgnoreCase))
        {
            Microsoft.Graph.Models.TeamCollectionResponse? teams = null;
            try
            {
                teams = await client.Teams.GetAsync(req => req.QueryParameters.Top = 100, cancellationToken);
            }
            catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (IsForbidden(ex))
            {
                _logger.LogWarning(
                    "SharePoint: skipping class team — app registration lacks scope Team.ReadBasic.All (403).");
                yield break;
            }
            while (teams?.Value != null)
            {
                foreach (var t in teams.Value)
                {
                    if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                    var members = await TryGetTeamMembersAsync(client, t.Id, cancellationToken);
                    emitted++;
                    yield return MapTeam(t, members);
                }
                if (string.IsNullOrEmpty(teams.OdataNextLink)) break;
                teams = await client.Teams.WithUrl(teams.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
            }
            yield break;
        }

        // ── channel: for each team, list its channels (parent ref = teamId) ──────
        if (string.Equals(objectClass, "channel", StringComparison.OrdinalIgnoreCase))
        {
            Microsoft.Graph.Models.TeamCollectionResponse? teams = null;
            try
            {
                teams = await client.Teams.GetAsync(req => req.QueryParameters.Top = 100, cancellationToken);
            }
            catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (IsForbidden(ex))
            {
                _logger.LogWarning(
                    "SharePoint: skipping class channel — listing parent teams requires Team.ReadBasic.All (403).");
                yield break;
            }
            while (teams?.Value != null)
            {
                foreach (var t in teams.Value)
                {
                    Microsoft.Graph.Models.ChannelCollectionResponse? channels = null;
                    try
                    {
                        channels = await client.Teams[t.Id].Channels.GetAsync(cancellationToken: cancellationToken);
                    }
                    catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (IsForbidden(ex))
                    {
                        _logger.LogWarning(
                            "SharePoint: skipping channels for team {TeamId} — app registration lacks scope Channel.ReadBasic.All (403).",
                            t.Id);
                        continue;
                    }
                    while (channels?.Value != null)
                    {
                        foreach (var ch in channels.Value)
                        {
                            if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                            emitted++;
                            yield return MapChannel(ch, t.Id, t.DisplayName);
                        }
                        if (string.IsNullOrEmpty(channels.OdataNextLink)) break;
                        channels = await client.Teams[t.Id].Channels
                            .WithUrl(channels.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
                    }
                }
                if (string.IsNullOrEmpty(teams.OdataNextLink)) break;
                teams = await client.Teams.WithUrl(teams.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
            }
            yield break;
        }

        // ── channelfile: bounded top-level file refs per channel (parent = channelId)
        if (string.Equals(objectClass, "channelfile", StringComparison.OrdinalIgnoreCase))
        {
            Microsoft.Graph.Models.TeamCollectionResponse? teams = null;
            try
            {
                teams = await client.Teams.GetAsync(req => req.QueryParameters.Top = 100, cancellationToken);
            }
            catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (IsForbidden(ex))
            {
                _logger.LogWarning(
                    "SharePoint: skipping class channelfile — listing parent teams requires Team.ReadBasic.All (403).");
                yield break;
            }
            while (teams?.Value != null)
            {
                foreach (var t in teams.Value)
                {
                    // Stop issuing per-team/per-channel Graph calls once MaxObjects is hit.
                    if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                    Microsoft.Graph.Models.ChannelCollectionResponse? channels = null;
                    try
                    {
                        channels = await client.Teams[t.Id].Channels.GetAsync(cancellationToken: cancellationToken);
                    }
                    catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (IsForbidden(ex))
                    {
                        _logger.LogWarning(
                            "SharePoint: skipping channel files for team {TeamId} — Channel.ReadBasic.All missing (403).",
                            t.Id);
                        continue;
                    }
                    foreach (var ch in channels?.Value ?? new List<Microsoft.Graph.Models.Channel>())
                    {
                        if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                        var files = await TryGetChannelFilesAsync(client, t.Id, ch.Id, cancellationToken);
                        foreach (var f in files)
                        {
                            if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                            emitted++;
                            yield return MapChannelFile(f, ch.Id, t.Id);
                        }
                    }
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

        // Collect the flat /sites list first so we can resolve each site's parent
        // by webUrl path containment (the tenant returns no parentReference on the
        // /sites collection). Site counts are modest vs. users; buffering is cheap.
        var allSites = new List<Microsoft.Graph.Models.Site>();
        var sites = await client.Sites.GetAsync(req => req.QueryParameters.Top = 100, cancellationToken);
        while (sites?.Value != null)
        {
            allSites.AddRange(sites.Value);
            if (string.IsNullOrEmpty(sites.OdataNextLink)) break;
            sites = await client.Sites.WithUrl(sites.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }

        var parentById = BuildSiteHierarchy(allSites);
        foreach (var s in allSites)
        {
            if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
            parentById.TryGetValue(s.Id ?? string.Empty, out var parentSiteId);
            emitted++;
            yield return MapSite(s, parentSiteId, storageByUrl);
        }
    }

    /// <summary>
    /// Resolves the channel's filesFolder driveItem then lists its children,
    /// capped at <see cref="MaxChannelFiles"/> top-level items (no recursion into
    /// subfolders, no paging past the cap). A 403 (Files.Read.All missing) or any
    /// resolution miss warns and yields an empty list — channels still emit.
    /// </summary>
    private async Task<List<Microsoft.Graph.Models.DriveItem>> TryGetChannelFilesAsync(
        GraphServiceClient client, string? teamId, string? channelId, CancellationToken cancellationToken)
    {
        var result = new List<Microsoft.Graph.Models.DriveItem>();
        if (string.IsNullOrEmpty(teamId) || string.IsNullOrEmpty(channelId)) return result;
        try
        {
            var folder = await client.Teams[teamId].Channels[channelId].FilesFolder
                .GetAsync(cancellationToken: cancellationToken);
            var driveId = folder?.ParentReference?.DriveId;
            var itemId = folder?.Id;
            if (string.IsNullOrEmpty(driveId) || string.IsNullOrEmpty(itemId)) return result;

            var children = await client.Drives[driveId].Items[itemId].Children
                .GetAsync(req => req.QueryParameters.Top = MaxChannelFiles, cancellationToken);
            foreach (var item in children?.Value ?? new List<Microsoft.Graph.Models.DriveItem>())
            {
                if (result.Count >= MaxChannelFiles) break;
                result.Add(item);
            }
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (IsForbidden(ex))
        {
            _logger.LogWarning(
                "SharePoint: skipping files for channel {ChannelId} — app registration lacks scope Files.Read.All (403).",
                channelId);
        }
        return result;
    }

    /// <summary>
    /// Lists a team's members and returns their stable member source ids. A 403
    /// (TeamMember.Read.All missing) warns and returns an empty list — the team
    /// still emits, just without membership edges.
    /// </summary>
    private async Task<List<string>> TryGetTeamMembersAsync(
        GraphServiceClient client, string? teamId, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        if (string.IsNullOrEmpty(teamId)) return ids;
        try
        {
            var page = await client.Teams[teamId].Members.GetAsync(cancellationToken: cancellationToken);
            while (page?.Value != null)
            {
                foreach (var m in page.Value)
                {
                    var id = MemberSourceId(m);
                    if (!string.IsNullOrEmpty(id)) ids.Add(id!);
                }
                if (string.IsNullOrEmpty(page.OdataNextLink)) break;
                page = await client.Teams[teamId].Members
                    .WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
            }
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (IsForbidden(ex))
        {
            _logger.LogWarning(
                "SharePoint: skipping members for team {TeamId} — app registration lacks scope TeamMember.Read.All (403).",
                teamId);
        }
        return ids;
    }

    // ── Pure mapping / hierarchy helpers (unit-tested without live Graph) ────────

    /// <summary>
    /// Member edge id: prefer the resolved AAD user object id (joins to the IC
    /// user object), falling back to the conversation-member id. Empty when neither.
    /// </summary>
    internal static string? MemberSourceId(Microsoft.Graph.Models.ConversationMember member)
    {
        if (member is Microsoft.Graph.Models.AadUserConversationMember aad &&
            !string.IsNullOrEmpty(aad.UserId))
            return aad.UserId;
        return member.Id;
    }

    /// <summary>
    /// Maps a flat list of sites to each site's parent site id. A site B is a child
    /// of site A when A.webUrl is the longest other site webUrl that is a path
    /// prefix of B.webUrl. Roots (no containing site) are absent from the map (the
    /// caller treats a miss as an empty parent). Pure — no Graph calls.
    /// </summary>
    internal static Dictionary<string, string> BuildSiteHierarchy(
        IReadOnlyList<Microsoft.Graph.Models.Site> sites)
    {
        var parentById = new Dictionary<string, string>(StringComparer.Ordinal);
        var withUrl = sites
            .Where(s => !string.IsNullOrEmpty(s.Id) && !string.IsNullOrEmpty(s.WebUrl))
            .ToList();

        foreach (var child in withUrl)
        {
            var childUrl = NormalizeUrl(child.WebUrl!);
            string? bestParentId = null;
            var bestParentLen = -1;
            foreach (var candidate in withUrl)
            {
                if (ReferenceEquals(candidate, child)) continue;
                var candUrl = NormalizeUrl(candidate.WebUrl!);
                if (candUrl.Length >= childUrl.Length) continue;
                if (!childUrl.StartsWith(candUrl + "/", StringComparison.OrdinalIgnoreCase)) continue;
                if (candUrl.Length > bestParentLen)
                {
                    bestParentLen = candUrl.Length;
                    bestParentId = candidate.Id;
                }
            }
            if (!string.IsNullOrEmpty(bestParentId))
                parentById[child.Id!] = bestParentId!;
        }
        return parentById;
    }

    private static string NormalizeUrl(string url) => url.TrimEnd('/');

    internal static ConnectorObject MapSite(
        Microsoft.Graph.Models.Site s,
        string? parentSiteId,
        IReadOnlyDictionary<string, SiteStorage>? storageByUrl)
    {
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
            ["parentSiteId"] = parentSiteId,
            ["whenCreated"] = s.CreatedDateTime?.ToString("o")
        };
        if (storageByUrl is not null && !string.IsNullOrEmpty(s.WebUrl) &&
            storageByUrl.TryGetValue(s.WebUrl!, out var storage))
        {
            if (storage.UsedBytes.HasValue) attrs["StorageUsedBytes"] = storage.UsedBytes.Value;
            if (storage.AllocatedBytes.HasValue) attrs["StorageAllocatedBytes"] = storage.AllocatedBytes.Value;
            if (storage.FileCount.HasValue) attrs["FileCount"] = storage.FileCount.Value;
        }
        return new ConnectorObject
        {
            SourceId = s.Id ?? string.Empty,
            ObjectClass = "Site",
            Attributes = attrs
        };
    }

    internal static ConnectorObject MapTeam(
        Microsoft.Graph.Models.Team t, List<string> members)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = "team",
            ["id"] = t.Id,
            ["objectGuid"] = t.Id,
            ["displayName"] = t.DisplayName,
            ["cn"] = t.DisplayName,
            ["description"] = t.Description,
            ["webUrl"] = t.WebUrl,
            // Membership edges — the orchestrator's second pass + IGroupMembershipEmittingSink
            // carry these to IC /api/objects/group-memberships/bulk (no sink change).
            ["members"] = members
        };
        return new ConnectorObject
        {
            SourceId = t.Id ?? string.Empty,
            ObjectClass = "Team",
            Attributes = attrs
        };
    }

    internal static ConnectorObject MapChannel(
        Microsoft.Graph.Models.Channel ch, string? teamId, string? teamName)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = "channel",
            ["id"] = ch.Id,
            ["objectGuid"] = ch.Id,
            ["displayName"] = ch.DisplayName,
            ["cn"] = ch.DisplayName,
            ["description"] = ch.Description,
            ["membershipType"] = ch.MembershipType?.ToString(),
            ["webUrl"] = ch.WebUrl,
            ["teamId"] = teamId,
            ["teamName"] = teamName,
            ["createdDateTime"] = ch.CreatedDateTime?.ToString("o")
        };
        return new ConnectorObject
        {
            SourceId = ch.Id ?? string.Empty,
            ObjectClass = "Channel",
            Attributes = attrs
        };
    }

    internal static ConnectorObject MapChannelFile(
        Microsoft.Graph.Models.DriveItem f, string? channelId, string? teamId)
    {
        // A DriveItem.Id is unique only within its drive; compose it with the
        // channel id so the SourceId is globally unique across drives/channels.
        var sourceId = string.IsNullOrEmpty(channelId) ? (f.Id ?? string.Empty) : $"{channelId}:{f.Id}";
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = "channelfile",
            ["id"] = sourceId,
            ["objectGuid"] = sourceId,
            ["driveItemId"] = f.Id,
            ["displayName"] = f.Name,
            ["cn"] = f.Name,
            ["webUrl"] = f.WebUrl,
            ["size"] = f.Size,
            ["isFolder"] = f.Folder is not null,
            ["channelId"] = channelId,
            ["teamId"] = teamId,
            ["lastModifiedDateTime"] = f.LastModifiedDateTime?.ToString("o")
        };
        return new ConnectorObject
        {
            SourceId = sourceId,
            ObjectClass = "channelfile",
            Attributes = attrs
        };
    }

    private static bool IsForbidden(Microsoft.Graph.Models.ODataErrors.ODataError ex)
    {
        if (ex.ResponseStatusCode == 403) return true;
        var code = ex.Error?.Code;
        return string.Equals(code, "Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "Forbidden", StringComparison.OrdinalIgnoreCase);
    }

    internal readonly record struct SiteStorage(long? UsedBytes, long? AllocatedBytes, long? FileCount);

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
