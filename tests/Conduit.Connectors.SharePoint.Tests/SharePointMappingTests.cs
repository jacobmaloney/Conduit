using System.Collections.Generic;
using System.Linq;
using Conduit.Connectors.SharePoint;
using Conduit.Sync.Templates;
using Microsoft.Graph.Models;
using Xunit;

namespace Conduit.Connectors.SharePoint.Tests;

/// <summary>
/// Covers the SharePoint collaboration fetch (Increment 2): the site-tree
/// hierarchy mapping, the Team/Channel/channelfile -> ConnectorObject mappers,
/// team member-edge extraction, the channel-file cap, and the generator /
/// blueprint / template wiring. None require a live Graph tenant — the pure
/// helpers take plain Graph model POCOs (no GraphServiceClient, which is a sealed
/// Kiota client that cannot be mocked).
/// </summary>
public class SharePointMappingTests
{
    private static Site Site(string id, string webUrl, string? display = null) =>
        new() { Id = id, WebUrl = webUrl, DisplayName = display ?? id };

    // ─── (a) site-tree hierarchy ─────────────────────────────────────────────

    [Fact]
    public void BuildSiteHierarchy_assigns_parent_to_subsites_and_leaves_roots_empty()
    {
        var sites = new List<Site>
        {
            Site("root", "https://contoso.sharepoint.com/sites/hr"),
            Site("child", "https://contoso.sharepoint.com/sites/hr/payroll"),
            Site("grandchild", "https://contoso.sharepoint.com/sites/hr/payroll/eu"),
            Site("other", "https://contoso.sharepoint.com/sites/it"),
        };

        var parents = SharePointSource.BuildSiteHierarchy(sites);

        // child's parent is the longest containing site = root.
        Assert.Equal("root", parents["child"]);
        // grandchild's parent is the longest containing site = child (not root).
        Assert.Equal("child", parents["grandchild"]);
        // roots have no entry in the map.
        Assert.False(parents.ContainsKey("root"));
        Assert.False(parents.ContainsKey("other"));
    }

    [Fact]
    public void BuildSiteHierarchy_is_trailing_slash_insensitive()
    {
        var sites = new List<Site>
        {
            Site("root", "https://contoso.sharepoint.com/sites/hr/"),
            Site("child", "https://contoso.sharepoint.com/sites/hr/payroll"),
        };

        var parents = SharePointSource.BuildSiteHierarchy(sites);
        Assert.Equal("root", parents["child"]);
    }

    [Fact]
    public void BuildSiteHierarchy_does_not_treat_url_prefix_substring_as_parent()
    {
        // "hr-archive" is NOT a child of "hr": prefix match must be on a path segment
        // boundary (the "/" separator), so a string-prefix overlap is rejected.
        var sites = new List<Site>
        {
            Site("hr", "https://contoso.sharepoint.com/sites/hr"),
            Site("hrArchive", "https://contoso.sharepoint.com/sites/hr-archive"),
        };

        var parents = SharePointSource.BuildSiteHierarchy(sites);
        Assert.False(parents.ContainsKey("hrArchive"));
    }

    [Fact]
    public void MapSite_emits_lowercase_class_stable_id_and_parent_ref()
    {
        var s = Site("s1", "https://contoso.sharepoint.com/sites/hr", "HR");
        var obj = SharePointSource.MapSite(s, "rootSite", storageByUrl: null);

        Assert.Equal("Site", obj.ObjectClass);
        Assert.Equal("s1", obj.SourceId);
        Assert.Equal("site", obj.Attributes["objectClass"]);
        Assert.Equal("s1", obj.Attributes["id"]);
        Assert.Equal("rootSite", obj.Attributes["parentSiteId"]);
        Assert.Equal("HR", obj.Attributes["displayName"]);
    }

    [Fact]
    public void MapSite_joins_storage_when_present_by_weburl()
    {
        var s = Site("s1", "https://contoso.sharepoint.com/sites/hr");
        var storage = new Dictionary<string, SharePointSource.SiteStorage>
        {
            ["https://contoso.sharepoint.com/sites/hr"] =
                new SharePointSource.SiteStorage(1024, 4096, 7)
        };

        var obj = SharePointSource.MapSite(s, parentSiteId: null, storageByUrl: storage);

        Assert.Equal(1024L, obj.Attributes["StorageUsedBytes"]);
        Assert.Equal(4096L, obj.Attributes["StorageAllocatedBytes"]);
        Assert.Equal(7L, obj.Attributes["FileCount"]);
    }

    // ─── (b) Team mapping + member edges ─────────────────────────────────────

    [Fact]
    public void MapTeam_emits_team_class_with_members_list()
    {
        var team = new Team { Id = "t1", DisplayName = "Engineering", Description = "Eng team" };
        var members = new List<string> { "u1", "u2", "u3" };

        var obj = SharePointSource.MapTeam(team, members);

        Assert.Equal("Team", obj.ObjectClass);
        Assert.Equal("t1", obj.SourceId);
        Assert.Equal("team", obj.Attributes["objectClass"]);
        Assert.Equal("Engineering", obj.Attributes["displayName"]);

        var emitted = Assert.IsAssignableFrom<List<string>>(obj.Attributes["members"]);
        Assert.Equal(new[] { "u1", "u2", "u3" }, emitted);
    }

    [Fact]
    public void MemberSourceId_prefers_aad_user_id_then_falls_back_to_member_id()
    {
        var aad = new AadUserConversationMember { Id = "membership-1", UserId = "user-guid-1" };
        Assert.Equal("user-guid-1", SharePointSource.MemberSourceId(aad));

        // A non-user conversation member (or one without a resolved UserId) falls back
        // to the membership id.
        var bare = new ConversationMember { Id = "membership-2" };
        Assert.Equal("membership-2", SharePointSource.MemberSourceId(bare));

        var aadNoUser = new AadUserConversationMember { Id = "membership-3", UserId = null };
        Assert.Equal("membership-3", SharePointSource.MemberSourceId(aadNoUser));
    }

    // ─── (c) Channel mapping (teamId parent ref) ─────────────────────────────

    [Fact]
    public void MapChannel_emits_channel_class_with_team_parent_refs()
    {
        var ch = new Channel
        {
            Id = "c1",
            DisplayName = "General",
            Description = "default",
            MembershipType = ChannelMembershipType.Standard
        };

        var obj = SharePointSource.MapChannel(ch, "t1", "Engineering");

        Assert.Equal("Channel", obj.ObjectClass);
        Assert.Equal("c1", obj.SourceId);
        Assert.Equal("channel", obj.Attributes["objectClass"]);
        Assert.Equal("t1", obj.Attributes["teamId"]);
        Assert.Equal("Engineering", obj.Attributes["teamName"]);
        Assert.Equal("General", obj.Attributes["displayName"]);
        Assert.Equal("Standard", obj.Attributes["membershipType"]);
    }

    // ─── (d) channel-file cap ────────────────────────────────────────────────

    [Fact]
    public void MapChannelFile_emits_channelfile_class_with_channel_parent_ref()
    {
        var item = new DriveItem { Id = "f1", Name = "spec.docx", Size = 2048 };
        var obj = SharePointSource.MapChannelFile(item, "c1", "t1");

        Assert.Equal("channelfile", obj.ObjectClass);
        // SourceId is composed with the channel id (DriveItem.Id is drive-local).
        Assert.Equal("c1:f1", obj.SourceId);
        Assert.Equal("f1", obj.Attributes["driveItemId"]);
        Assert.Equal("channelfile", obj.Attributes["objectClass"]);
        Assert.Equal("c1", obj.Attributes["channelId"]);
        Assert.Equal("t1", obj.Attributes["teamId"]);
        Assert.Equal("spec.docx", obj.Attributes["displayName"]);
        Assert.Equal(2048L, obj.Attributes["size"]);
    }

    [Fact]
    public void ChannelFile_cap_bounds_emission_to_MaxChannelFiles()
    {
        // The connector's child-children fetch caps at MaxChannelFiles. Emulate the
        // capping logic (take top N) over an over-sized item list and assert the
        // emitted set is bounded and stable in order.
        var items = Enumerable.Range(0, 250)
            .Select(i => new DriveItem { Id = $"f{i}", Name = $"file{i}.txt" })
            .ToList();

        var capped = items.Take(SharePointSource.MaxChannelFiles)
            .Select(f => SharePointSource.MapChannelFile(f, "c1", "t1"))
            .ToList();

        Assert.Equal(SharePointSource.MaxChannelFiles, capped.Count);
        Assert.True(SharePointSource.MaxChannelFiles < items.Count);
        Assert.Equal("c1:f0", capped.First().SourceId);
        Assert.Equal($"c1:f{SharePointSource.MaxChannelFiles - 1}", capped.Last().SourceId);
    }

    // ─── wiring: generator / template / blueprint ────────────────────────────

    [Fact]
    public void Generator_advertises_collaboration_classes()
    {
        var generator = new SyncProjectGenerator(null!);
        var classes = generator.GetObjectClasses("SharePoint", GenerationMode.SharePointCollaboration);

        foreach (var cls in new[] { "site", "team", "channel", "channelfile", "drive", "list" })
            Assert.Contains(cls, classes);
    }

    [Fact]
    public void Templates_exist_for_every_emitted_collaboration_class()
    {
        Assert.NotNull(AttributeTemplateCatalog.Get("SharePoint", "Site"));
        Assert.NotNull(AttributeTemplateCatalog.Get("SharePoint", "Team"));
        Assert.NotNull(AttributeTemplateCatalog.Get("SharePoint", "Channel"));
        Assert.NotNull(AttributeTemplateCatalog.Get("SharePoint", "channelfile"));
    }

    [Fact]
    public void Site_template_carries_parentSiteId_and_Team_template_carries_members()
    {
        var site = AttributeTemplateCatalog.Get("SharePoint", "Site");
        Assert.NotNull(site);
        Assert.Contains(site!, e => e.SourceAttribute == "parentSiteId");

        var team = AttributeTemplateCatalog.Get("SharePoint", "Team");
        Assert.NotNull(team);
        Assert.Contains(team!, e => e.SourceAttribute == "members");

        var channel = AttributeTemplateCatalog.Get("SharePoint", "Channel");
        Assert.NotNull(channel);
        Assert.Contains(channel!, e => e.SourceAttribute == "teamId");

        var file = AttributeTemplateCatalog.Get("SharePoint", "channelfile");
        Assert.NotNull(file);
        Assert.Contains(file!, e => e.SourceAttribute == "channelId");
    }

    [Fact]
    public void Blueprint_catalog_has_sharepoint_collaboration_card()
    {
        var bp = SyncProjectBlueprintCatalog.GetById("sharepoint-collaboration-governance");
        Assert.NotNull(bp);
        Assert.Equal("SharePoint", bp!.SourceSystemType);
        Assert.Equal(GenerationMode.SharePointCollaboration, bp.Mode);

        var forSp = SyncProjectBlueprintCatalog.ForSourceSystemType("SharePoint");
        Assert.Contains(forSp, b => b.Id == "sharepoint-collaboration-governance");
    }

    [Fact]
    public void Only_sharepointgroup_remains_deferred_among_collaboration_classes()
    {
        Assert.True(SyncProjectBlueprintCatalog.IsDeferredClass("sharepointgroup"));
        foreach (var cls in new[] { "channel", "channelfile", "drive", "list" })
            Assert.False(SyncProjectBlueprintCatalog.IsDeferredClass(cls));
    }
}
