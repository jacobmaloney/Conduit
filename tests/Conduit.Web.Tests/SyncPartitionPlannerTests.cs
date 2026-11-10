using System.DirectoryServices.Protocols;
using Conduit.Connectors.ActiveDirectory;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Sync.Orchestration;
using Conduit.Web.Services;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// IdentityCenter SPLIT-ENUM-01 (UC037-D06/D09), the Conduit half: the containers one level under a scope become the
/// containers a split read runs one at a time; a container run reads only its container, one level deep for the base
/// itself, with the deleted-objects pass left to the first container; the payload IdentityCenter writes is read back
/// exactly, and a malformed one is not a partition.
/// </summary>
public class SyncPartitionPlannerTests
{
    private static DirectoryContainerNode Node(string dn) => new() { Name = dn.Split(',')[0], DistinguishedName = dn };

    [Fact]
    public void The_base_reads_one_level_deep_then_each_child_container_as_a_subtree_and_the_first_carries_tombstones()
    {
        var plan = SyncPartitionPlanner.Build(new[]
        {
            new SyncPartitionBase("DC=example,DC=com", new[] { Node("OU=Sales,DC=example,DC=com"), Node("CN=Users,DC=example,DC=com"), Node("OU=Plant,DC=example,DC=com") }),
        });

        Assert.Equal(4, plan.Count);
        Assert.All(plan, p => Assert.Equal(4, p.Count));
        Assert.Equal(new[] { 1, 2, 3, 4 }, plan.Select(p => p.Index));
        Assert.Equal("DC=example,DC=com (this level)", plan[0].Label);
        Assert.Equal(new[] { "DC=example,DC=com" }, plan[0].IncludedBaseDNs);
        Assert.True(plan[0].OneLevel);
        Assert.True(plan[0].CarriesTombstones);
        Assert.Equal(new[] { "OU=Sales,DC=example,DC=com", "CN=Users,DC=example,DC=com", "OU=Plant,DC=example,DC=com" }, plan.Skip(1).Select(p => p.Label));
        Assert.All(plan.Skip(1), p => { Assert.False(p.OneLevel); Assert.False(p.CarriesTombstones); Assert.Equal(new[] { p.Label }, p.IncludedBaseDNs); });
    }

    [Fact]
    public void Excluded_containers_are_left_out_overlapping_bases_are_read_once_and_a_scope_without_children_is_one_container()
    {
        var plan = SyncPartitionPlanner.Build(new[]
        {
            new SyncPartitionBase("OU=Org,DC=example,DC=com", new[] { Node("OU=Sales,OU=Org,DC=example,DC=com"), Node("OU=Old,OU=Org,DC=example,DC=com"), Node("OU=Plant,OU=Org,DC=example,DC=com") }),
            new SyncPartitionBase("OU=Plant,OU=Org,DC=example,DC=com", new[] { Node("OU=Line1,OU=Plant,OU=Org,DC=example,DC=com") }),
        }, new[] { "OU=Old,OU=Org,DC=example,DC=com" });

        Assert.Equal(new[]
        {
            "OU=Org,DC=example,DC=com (this level)",
            "OU=Sales,OU=Org,DC=example,DC=com",
            "OU=Plant,OU=Org,DC=example,DC=com",          // once, although it is also a base
            "OU=Line1,OU=Plant,OU=Org,DC=example,DC=com",
        }, plan.Select(p => p.Label));
        Assert.Single(plan, p => p.CarriesTombstones);
        Assert.True(plan[0].CarriesTombstones);

        var whole = SyncPartitionPlanner.Build(new[] { new SyncPartitionBase("DC=example,DC=com", Array.Empty<DirectoryContainerNode>()) });
        Assert.Single(whole);
        Assert.True(whole[0].OneLevel);

        Assert.Empty(SyncPartitionPlanner.Build(new[] { new SyncPartitionBase("OU=Old,OU=Org,DC=example,DC=com", new[] { Node("OU=X,OU=Old,OU=Org,DC=example,DC=com") }) }, new[] { "OU=Old, OU=Org, DC=example, DC=com" }));
    }

    [Fact]
    public void A_partition_applied_to_a_steps_scope_reads_only_its_container_and_keeps_the_rest_of_the_scope()
    {
        var step = new SyncProjectScope { SyncProjectId = Guid.NewGuid(), LdapFilter = "(department=Plant)", PageSize = 250, MaxObjects = 10, IncludeDeleted = true, RequestedAttributes = new List<string> { "mail" } };
        step.SetIncludedBaseList(new[] { "DC=example,DC=com" });
        step.SetExcludedBaseList(new[] { "OU=Old,DC=example,DC=com" });

        var baseRead = new SyncRunPartition(1, 3, "DC=example,DC=com (this level)", new[] { "DC=example,DC=com" }, true, true).ApplyTo(step);
        Assert.Equal(new[] { "DC=example,DC=com" }, baseRead.GetIncludedBaseList());
        Assert.True(baseRead.ContainerOnly);
        Assert.False(baseRead.SuppressTombstones);
        Assert.Equal(SearchScope.OneLevel, ActiveDirectorySource.DepthFor(baseRead));

        var child = new SyncRunPartition(2, 3, "OU=Sales,DC=example,DC=com", new[] { "OU=Sales,DC=example,DC=com" }, false, false).ApplyTo(step);
        Assert.Equal(new[] { "OU=Sales,DC=example,DC=com" }, child.GetIncludedBaseList());
        Assert.Equal(new[] { "OU=Old,DC=example,DC=com" }, child.GetExcludedBaseList());
        Assert.Equal("(department=Plant)", child.LdapFilter);
        Assert.Equal((250, 10, true), (child.PageSize, child.MaxObjects, child.IncludeDeleted));
        Assert.Same(step.RequestedAttributes, child.RequestedAttributes);
        Assert.False(child.ContainerOnly);
        Assert.True(child.SuppressTombstones);
        Assert.Equal(SearchScope.Subtree, ActiveDirectorySource.DepthFor(child));

        // The step's own scope is untouched, and a plain scope reads a subtree with its tombstones.
        Assert.Equal(new[] { "DC=example,DC=com" }, step.GetIncludedBaseList());
        Assert.False(step.ContainerOnly);
        Assert.False(step.SuppressTombstones);
        Assert.Equal(SearchScope.Subtree, ActiveDirectorySource.DepthFor(step));

        Assert.Throws<InvalidOperationException>(() => new SyncRunPartition(1, 1, "x", Array.Empty<string>(), false, true).ApplyTo(step));
        Assert.Equal("Partition 1/3: DC=example,DC=com (this level) (this level only)", new SyncRunPartition(1, 3, "DC=example,DC=com (this level)", new[] { "DC=example,DC=com" }, true, true).Describe());
    }

    [Theory]
    [InlineData("""{"TriggerType":"Manual","Split":"Container"}""", true, false)]
    [InlineData("""{"TriggerType":"Manual","split":"container"}""", true, false)]
    [InlineData("""{"TriggerType":"Manual"}""", false, false)]
    [InlineData("""{"Split":"Container","Partition":{"Index":1,"Count":2,"IncludedBaseDNs":["OU=a"],"ParentJobId":"bbbbbbbb-0000-0000-0000-00000000000b"}}""", false, true)]
    [InlineData("not json", false, false)]
    [InlineData(null, false, false)]
    public void A_plan_item_and_a_container_item_are_told_apart_by_their_payload(string? payload, bool plan, bool partition)
    {
        Assert.Equal(plan, IcPartitionPayload.IsPlan(payload));
        Assert.Equal(partition, IcPartitionPayload.ReadPartition(payload) is not null);
    }

    [Fact]
    public void The_partition_IdentityCenter_writes_is_read_back_and_a_malformed_one_is_not_one()
    {
        const string payload = """{"TriggerType":"Manual","TriggeredBy":"nora.quint","Partition":{"Index":3,"Count":12,"Label":"OU=Sales,DC=example,DC=com","IncludedBaseDNs":[" OU=Sales,DC=example,DC=com "],"OneLevel":false,"CarriesTombstones":true,"ParentJobId":"bbbbbbbb-0000-0000-0000-00000000000b"}}""";
        var read = IcPartitionPayload.ReadPartition(payload)!;
        Assert.Equal((3, 12, "OU=Sales,DC=example,DC=com", false, true), (read.Index, read.Count, read.Label, read.OneLevel, read.CarriesTombstones));
        Assert.Equal(new[] { "OU=Sales,DC=example,DC=com" }, read.IncludedBaseDNs);

        Assert.Null(IcPartitionPayload.ReadPartition("""{"Partition":{"Index":1,"Count":2,"IncludedBaseDNs":[]}}"""));
        Assert.Null(IcPartitionPayload.ReadPartition("""{"Partition":{"Index":0,"Count":2,"IncludedBaseDNs":["OU=a"]}}"""));
        Assert.Null(IcPartitionPayload.ReadPartition("""{"Partition":{"Index":3,"Count":2,"IncludedBaseDNs":["OU=a"]}}"""));
        Assert.Null(IcPartitionPayload.ReadPartition("""{"Partition":"OU=a"}"""));
        Assert.True(IcPartitionPayload.HasPartition("""{"Partition":{"Index":1}}"""));
        Assert.False(IcPartitionPayload.HasPartition("""{"Split":"Container"}"""));
    }

    [Fact]
    public void The_plan_answer_is_read_as_IdentityCenter_writes_it()
    {
        var answer = IcSyncJobChannel.ParsePlanAnswer("""{"outcome":"Queued","reason":null,"children":[{"jobId":"11111111-1111-1111-1111-111111111111","index":2,"label":"OU=Sales","serverName":"Conduit B"},{"jobId":"22222222-2222-2222-2222-222222222222","index":1,"label":"DC=x (this level)","serverName":"Conduit A"}]}""")!;
        Assert.Equal("Queued", answer.Outcome);
        Assert.Equal(2, answer.Children.Count);
        Assert.Equal("Conduit B", answer.Children[0].ServerName);
        Assert.Null(IcSyncJobChannel.ParsePlanAnswer("[]"));
        Assert.Null(IcSyncJobChannel.ParsePlanAnswer("nope"));

        var body = System.Text.Json.JsonDocument.Parse(IcSyncJobChannel.BuildPlanJson(Guid.Empty,
            new[] { new SyncRunPartition(1, 1, "DC=x (this level)", new[] { "DC=x" }, true, true) })).RootElement;
        var partition = body.GetProperty("partitions")[0];
        Assert.Equal("DC=x (this level)", partition.GetProperty("label").GetString());
        Assert.Equal("DC=x", partition.GetProperty("includedBaseDNs")[0].GetString());
        Assert.True(partition.GetProperty("oneLevel").GetBoolean());
        Assert.True(partition.GetProperty("carriesTombstones").GetBoolean());
    }
}
