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
                    var roster = await TryGetTeamRosterAsync(client, t.Id, cancellationToken);
                    emitted++;
                    yield return MapTeam(t, roster.Members, roster.Owners);
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

        // ── subscribedSku: tenant license SKUs — one small collection, no paging cliffs ──
        if (string.Equals(objectClass, "subscribedSku", StringComparison.OrdinalIgnoreCase))
        {
            var skus = await client.SubscribedSkus.GetAsync(cancellationToken: cancellationToken);
            while (skus?.Value != null)
            {
                foreach (var sku in skus.Value)
                {
                    if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                    if (string.IsNullOrEmpty(sku.Id)) continue;
                    emitted++;
                    yield return MapSubscribedSku(sku);
                }
                if (string.IsNullOrEmpty(skus.OdataNextLink)) break;
                skus = await client.SubscribedSkus.WithUrl(skus.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
            }
            yield break;
        }

        // ── drive: document libraries per site. Same Sites.Read.All scope as /sites;
        //    a failure on ONE site's drives warns and skips that site only. ──
        if (string.Equals(objectClass, "Drive", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var s in await ListAllSitesAsync(client, cancellationToken))
            {
                if (string.IsNullOrEmpty(s.Id)) continue;
                List<Microsoft.Graph.Models.Drive> drives;
                try
                {
                    var page = await client.Sites[s.Id].Drives.GetAsync(cancellationToken: cancellationToken);
                    drives = page?.Value ?? new List<Microsoft.Graph.Models.Drive>();
                }
                catch (Microsoft.Graph.Models.ODataErrors.ODataError ex)
                {
                    _logger.LogWarning("SharePoint: skipping drives for site {SiteId} ({Message}).", s.Id, ex.Error?.Message ?? ex.Message);
                    continue;
                }
                foreach (var d in drives)
                {
                    if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                    if (string.IsNullOrEmpty(d.Id)) continue;
                    emitted++;
                    yield return MapDrive(d, s.DisplayName ?? s.Name);
                }
            }
            yield break;
        }

        // ── list: SharePoint lists per site (document libraries appear here too,
        //    flagged by their template). Same per-site failure isolation as drives. ──
        if (string.Equals(objectClass, "List", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var s in await ListAllSitesAsync(client, cancellationToken))
            {
                if (string.IsNullOrEmpty(s.Id)) continue;
                List<Microsoft.Graph.Models.List> lists;
                try
                {
                    var page = await client.Sites[s.Id].Lists.GetAsync(cancellationToken: cancellationToken);
                    lists = page?.Value ?? new List<Microsoft.Graph.Models.List>();
                }
                catch (Microsoft.Graph.Models.ODataErrors.ODataError ex)
                {
                    _logger.LogWarning("SharePoint: skipping lists for site {SiteId} ({Message}).", s.Id, ex.Error?.Message ?? ex.Message);
                    continue;
                }
                foreach (var l in lists)
                {
                    if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                    if (string.IsNullOrEmpty(l.Id)) continue;
                    emitted++;
                    yield return MapList(l, s.DisplayName ?? s.Name);
                }
            }
            yield break;
        }

        // Any class we don't recognize is an honest error, not a silent site sync —
        // a wrong class here used to fall through and emit the whole /sites list.
        if (!string.Equals(objectClass, "Site", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"SharePoint source does not support object class '{objectClass}'. " +
                "Supported: site, team, channel, channelfile, drive, list, subscribedSku, sharepointgroup.");
        }

        // ── site: The /sites list carries NO storage figures — those come
        // from the getSharePointSiteUsageDetail usage report, which we fetch once
        // and join by site URL. A 403 on the report (missing Reports.Read.All)
        // skips enrichment with a warning but every site still emits.
        var storageByUrl = await TryGetSiteStorageAsync(creds, cancellationToken);

        // Collect the flat /sites list first so we can resolve each site's parent
        // by webUrl path containment (the tenant returns no parentReference on the
        // /sites collection). Site counts are modest vs. users; buffering is cheap.
        var allSites = await ListAllSitesAsync(client, cancellationToken);

        // Group-connected (modern) sites: resolve the backing M365 group per site and
        // carry its member ids, so sites get member edges like teams do and IC's
        // member add/remove can target the backing group. Best-effort — Graph has no
        // per-site membership surface, so classic sites simply have no entry here.
        var backingBySiteId = await TryGetSiteBackingGroupsAsync(client, cancellationToken);

        var parentById = BuildSiteHierarchy(allSites);
        foreach (var s in allSites)
        {
            if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
            parentById.TryGetValue(s.Id ?? string.Empty, out var parentSiteId);
            backingBySiteId.TryGetValue(s.Id ?? string.Empty, out var backing);
            emitted++;
            yield return MapSite(s, parentSiteId, storageByUrl, backing.GroupId, backing.MemberIds);
        }
    }

    /// <summary>
    /// Maps root-site id -> (backing Unified group id, that group's member ids).
    /// One pass over the tenant's Unified groups: per group, /groups/{id}/sites/root
    /// gives the connected site; /groups/{id}/members gives the ids. Per-group
    /// failures skip that group only; a listing failure returns an empty map (sites
    /// still emit, minus membership).
    /// </summary>
    private async Task<Dictionary<string, (string GroupId, List<string> MemberIds)>> TryGetSiteBackingGroupsAsync(
        GraphServiceClient client, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, (string, List<string>)>(StringComparer.Ordinal);
        try
        {
            var groups = await client.Groups.GetAsync(req =>
            {
                req.QueryParameters.Filter = "groupTypes/any(c:c eq 'Unified')";
                req.QueryParameters.Select = new[] { "id" };
                req.QueryParameters.Top = 100;
            }, cancellationToken);

            while (groups?.Value != null)
            {
                foreach (var g in groups.Value)
                {
                    if (string.IsNullOrEmpty(g.Id)) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var rootSite = await client.Groups[g.Id].Sites["root"]
                            .GetAsync(req => req.QueryParameters.Select = new[] { "id" }, cancellationToken);
                        if (string.IsNullOrEmpty(rootSite?.Id)) continue;

                        var memberIds = new List<string>();
                        var members = await client.Groups[g.Id].Members.GetAsync(req =>
                        {
                            req.QueryParameters.Select = new[] { "id" };
                            req.QueryParameters.Top = 999;
                        }, cancellationToken);
                        while (members?.Value != null)
                        {
                            foreach (var m in members.Value)
                                if (!string.IsNullOrEmpty(m.Id)) memberIds.Add(m.Id!);
                            if (string.IsNullOrEmpty(members.OdataNextLink)) break;
                            members = await client.Groups[g.Id].Members
                                .WithUrl(members.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
                        }

                        map[rootSite!.Id!] = (g.Id!, memberIds);
                    }
                    catch (Microsoft.Graph.Models.ODataErrors.ODataError)
                    {
                        // Group without a provisioned site (or no access) — skip it.
                    }
                }
                if (string.IsNullOrEmpty(groups.OdataNextLink)) break;
                groups = await client.Groups.WithUrl(groups.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "SharePoint: could not resolve site backing groups ({Message}) — sites emit without member edges this run.",
                ex.Message);
        }
        return map;
    }

    /// <summary>The flat tenant /sites collection, fully paged. Shared by site/drive/list.</summary>
    private static async Task<List<Microsoft.Graph.Models.Site>> ListAllSitesAsync(
        GraphServiceClient client, CancellationToken cancellationToken)
    {
        var allSites = new List<Microsoft.Graph.Models.Site>();
        var sites = await client.Sites.GetAsync(req => req.QueryParameters.Top = 100, cancellationToken);
        while (sites?.Value != null)
        {
            allSites.AddRange(sites.Value);
            if (string.IsNullOrEmpty(sites.OdataNextLink)) break;
            sites = await client.Sites.WithUrl(sites.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }
        return allSites;
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
    /// A team's roster from one /teams/{id}/members walk: every member's stable source
    /// id, plus the members whose <c>roles</c> carry "owner". Owners is null when the
    /// walk did not happen (403) so MapTeam stamps nothing rather than ownerCount=0.
    /// </summary>
    internal sealed record TeamRoster(List<string> Members, IReadOnlyList<Microsoft.Graph.Models.ConversationMember>? Owners);

    /// <summary>
    /// Lists a team's members and returns their stable member source ids plus the
    /// owner subset. A 403 (TeamMember.Read.All missing) warns and returns an empty
    /// member list with null owners — the team still emits, just without edges.
    /// </summary>
    private async Task<TeamRoster> TryGetTeamRosterAsync(
        GraphServiceClient client, string? teamId, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        if (string.IsNullOrEmpty(teamId)) return new TeamRoster(ids, null);
        var owners = new List<Microsoft.Graph.Models.ConversationMember>();
        try
        {
            var page = await client.Teams[teamId].Members.GetAsync(cancellationToken: cancellationToken);
            while (page?.Value != null)
            {
                foreach (var m in page.Value)
                {
                    var id = MemberSourceId(m);
                    if (!string.IsNullOrEmpty(id)) ids.Add(id!);
                    if (IsTeamOwner(m)) owners.Add(m);
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
            return new TeamRoster(ids, null);
        }
        return new TeamRoster(ids, owners);
    }

    /// <summary>Team owners are the conversation members whose roles include "owner".</summary>
    internal static bool IsTeamOwner(Microsoft.Graph.Models.ConversationMember member) =>
        member.Roles is { Count: > 0 } roles &&
        roles.Any(r => string.Equals(r, "owner", StringComparison.OrdinalIgnoreCase));

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
        IReadOnlyDictionary<string, SiteStorage>? storageByUrl,
        string? backingGroupId = null,
        List<string>? memberIds = null)
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
        // Group-connected sites: the backing M365 group id (IC's member add/remove
        // targets this) + its member ids (feed the membership second pass).
        if (!string.IsNullOrEmpty(backingGroupId)) attrs["groupId"] = backingGroupId;
        if (memberIds is { Count: > 0 }) attrs["members"] = memberIds;
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

    internal static ConnectorObject MapDrive(Microsoft.Graph.Models.Drive d, string? siteName)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = "drive",
            ["id"] = d.Id,
            ["objectGuid"] = d.Id,
            ["displayName"] = d.Name,
            ["cn"] = d.Name,
            ["name"] = d.Name,
            ["driveType"] = d.DriveType,
            ["webUrl"] = d.WebUrl,
            ["description"] = d.Description,
            ["siteName"] = siteName
        };
        if (d.Quota?.Total is long total) attrs["quotaTotal"] = total;
        if (d.Quota?.Used is long used) attrs["quotaUsed"] = used;
        if (!string.IsNullOrEmpty(d.Quota?.State)) attrs["quotaState"] = d.Quota!.State;
        return new ConnectorObject
        {
            SourceId = d.Id ?? string.Empty,
            ObjectClass = "Drive",
            Attributes = attrs
        };
    }

    internal static ConnectorObject MapList(Microsoft.Graph.Models.List l, string? siteName)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = "list",
            ["id"] = l.Id,
            ["objectGuid"] = l.Id,
            ["displayName"] = l.DisplayName ?? l.Name,
            ["cn"] = l.Name,
            ["name"] = l.Name,
            ["webUrl"] = l.WebUrl,
            ["description"] = l.Description,
            ["siteName"] = siteName,
            ["listTemplate"] = l.ListProp?.Template,
            ["createdDateTime"] = l.CreatedDateTime?.ToString("o"),
            ["lastModifiedDateTime"] = l.LastModifiedDateTime?.ToString("o")
        };
        return new ConnectorObject
        {
            SourceId = l.Id ?? string.Empty,
            ObjectClass = "List",
            Attributes = attrs
        };
    }

    internal static ConnectorObject MapSubscribedSku(Microsoft.Graph.Models.SubscribedSku sku)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = "subscribedsku",
            ["id"] = sku.Id,
            ["objectGuid"] = sku.Id,
            ["skuPartNumber"] = sku.SkuPartNumber,
            ["displayName"] = sku.SkuPartNumber,
            ["cn"] = sku.SkuPartNumber,
            ["skuId"] = sku.SkuId?.ToString(),
            ["appliesTo"] = sku.AppliesTo
        };
        if (sku.ConsumedUnits is int consumed) attrs["consumedUnits"] = consumed;
        if (sku.PrepaidUnits?.Enabled is int enabled) attrs["prepaidEnabled"] = enabled;
        if (sku.PrepaidUnits?.Suspended is int suspended) attrs["prepaidSuspended"] = suspended;
        if (sku.ServicePlans is { Count: > 0 } plans) attrs["servicePlanCount"] = plans.Count;
        return new ConnectorObject
        {
            SourceId = sku.Id ?? string.Empty,
            ObjectClass = "SubscribedSku",
            Attributes = attrs
        };
    }

    private static readonly JsonSerializerOptions OwnerJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record TeamOwner(string id, string? displayName, string? upn);

    internal static ConnectorObject MapTeam(
        Microsoft.Graph.Models.Team t, List<string> members,
        IReadOnlyList<Microsoft.Graph.Models.ConversationMember>? owners = null)
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
        // Owners land as attributes only: IC has no multi-owner edge table (Objects.OwnerObjectId
        // is bound from the single-valued AD managedBy DN). ownerIds = the owners' AAD user ids
        // (their SourceUniqueId on IC) so the read side can JOIN without parsing the JSON.
        // upn is the AadUserConversationMember email — /teams/{id}/members carries no UPN.
        if (owners is not null)
        {
            var projected = new List<TeamOwner>(owners.Count);
            var ids = new List<string>(owners.Count);
            foreach (var o in owners)
            {
                var id = MemberSourceId(o);
                if (string.IsNullOrEmpty(id)) continue;
                ids.Add(id!);
                projected.Add(new TeamOwner(id!, o.DisplayName,
                    (o as Microsoft.Graph.Models.AadUserConversationMember)?.Email));
            }
            attrs["ownerCount"] = projected.Count;
            if (projected.Count > 0)
            {
                attrs["owners"] = JsonSerializer.Serialize(projected, OwnerJsonOptions);
                attrs["ownerIds"] = ids;
            }
        }
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
