using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Conduit.Connectors.ActiveDirectory;
using Conduit.Web.Services;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// OBJECT-DELETE-01 (IdentityCenter UC017; Jacob 2026-11-15 "Yes, any object"): every refusal is decided from what the
/// agent read, before any write, in plain words. No Tree Delete control: a subtree is listed once and judged whole
/// (protection, containment, the count the person confirmed), then deleted deepest first, one object per request by
/// GUID, each re-read and re-judged first; a change mid-run stops the run and reports what it deleted.
/// </summary>
public class ObjectDeleteTests
{
    private const string Domain = "DC=corp,DC=local";
    private const string Config = "CN=Configuration,DC=corp,DC=local";
    private const string Schema = "CN=Schema,CN=Configuration,DC=corp,DC=local";
    private static readonly string[] Contexts = { Domain, Config, Schema, "DC=DomainDnsZones,DC=corp,DC=local" };
    private static readonly string[] Bases = { Domain };
    private static bool Permitted(string dn) => BaseDnContainment.IsStrictlyBelow(dn, Bases);

    private static AdDeleteNode Node(string dn, string cls = "user", bool critical = false, bool adminCount = false, int uac = 0x200, Guid? guid = null) =>
        new(guid ?? Guid.NewGuid(), dn, new[] { "top", cls }, critical, adminCount, uac);

    private static AdDeleteFacts Facts(AdDeleteNode target, int children = 0, IReadOnlyList<AdDeleteNode>? subtree = null) =>
        new(target, Contexts, Config, Schema, children, subtree);

    private static string Refusal(AdDeleteFacts facts, bool subtree = false, int? confirmed = null, Func<string, bool>? permitted = null)
    {
        var (plan, refusal) = AdObjectDeletePlan.Decide(facts, subtree, confirmed, permitted ?? Permitted);
        Assert.Null(plan);
        Assert.NotNull(refusal);
        return refusal!;
    }

    private const string Ou = "OU=Contractors," + Domain;
    private static AdDeleteNode OuNode() => Node(Ou, "organizationalUnit", uac: 0);

    private static List<AdDeleteNode> Subtree() => new()
    {
        Node("CN=Ana Rios," + Ou),
        Node("OU=Remote," + Ou, "organizationalUnit", uac: 0),
        Node("CN=Bo Lee,OU=Remote," + Ou),
    };

    [Fact]
    public void A_leaf_is_one_delete_by_GUID_with_no_tree_control()
    {
        var target = Node("CN=Ana Rios," + Ou);
        var (plan, refusal) = AdObjectDeletePlan.Decide(Facts(target), includeSubtree: false, null, Permitted);
        Assert.Null(refusal);
        Assert.Equal(new[] { target }, plan!.Order);

        var request = AdObjectDeletion.BuildRequest(target.ObjectGuid);
        Assert.Equal($"<GUID={target.ObjectGuid}>", request.DistinguishedName);
        Assert.Empty(request.Controls);
        var source = File.ReadAllText(Src("src/Conduit.Connectors.ActiveDirectory/AdObjectDeletion.cs"));
        Assert.DoesNotContain("TreeDeleteControl", source);
        Assert.DoesNotContain("1.2.840.113556.1.4.805", source);
    }

    [Fact]
    public void A_subtree_is_ordered_deepest_first_with_the_target_last()
    {
        var sub = Subtree();
        var (plan, refusal) = AdObjectDeletePlan.Decide(Facts(OuNode(), 2, sub), includeSubtree: true, confirmedCount: 3, Permitted);
        Assert.Null(refusal);
        Assert.Equal(new[] { "CN=Bo Lee,OU=Remote," + Ou, "CN=Ana Rios," + Ou, "OU=Remote," + Ou, Ou },
            plan!.Order.Select(n => n.DistinguishedName));
        Assert.Equal(3, plan.DescendantCount);
    }

    [Fact]
    public void A_container_with_children_is_refused_without_the_subtree()
    {
        Assert.Equal("it has 3 children; delete it with everything under it, or empty it first.", Refusal(Facts(OuNode(), 3)));
        // A container past one page of children reads "more than 500", never a wrong exact count (2026-11-16).
        Assert.Equal("it has more than 500 children; delete it with everything under it, or empty it first.", Refusal(Facts(OuNode(), 501)));
        Assert.Equal("it has 1 child; delete it with everything under it, or empty it first.", Refusal(Facts(OuNode(), 1)));
        Assert.Contains("has 1 child;", Refusal(Facts(OuNode(), 1)));
    }

    [Fact]
    public void A_listed_subtree_larger_than_the_confirmed_count_is_refused()
    {
        Assert.Equal("the directory holds 3 objects under it, more than the 2 the confirm showed; nothing was deleted.",
            Refusal(Facts(OuNode(), 2, Subtree()), subtree: true, confirmed: 2));
        Assert.Contains("did not say how many", Refusal(Facts(OuNode(), 2, Subtree()), subtree: true, confirmed: null));
        var big = Enumerable.Range(0, AdObjectDeletePlan.MaxObjectsPerRun + 1).Select(i => Node($"CN=u{i}," + Ou)).ToList();
        Assert.Contains("more than 150 objects", Refusal(Facts(OuNode(), 151, big), subtree: true, confirmed: 1000));
    }

    [Fact]
    public void A_protected_descendant_refuses_the_whole_subtree()
    {
        var sub = Subtree();
        sub.Add(Node("CN=Svc Admin," + Ou, adminCount: true));
        Assert.Equal("the objects under it include CN=Svc Admin," + Ou + ", which is protected by AdminSDHolder (adminCount is 1); nothing was deleted.",
            Refusal(Facts(OuNode(), 3, sub), subtree: true, confirmed: 10));
        Assert.Contains("domain controller's computer account",
            Refusal(Facts(OuNode(), 1, new[] { Node("CN=DC9," + Ou, "computer", uac: 0x2000) }), subtree: true, confirmed: 10));
        Assert.Contains("critical system object",
            Refusal(Facts(OuNode(), 1, new[] { Node("CN=X," + Ou, "container", critical: true, uac: 0) }), subtree: true, confirmed: 10));
    }

    [Fact]
    public void An_out_of_base_descendant_refuses_the_whole_subtree()
    {
        bool OnlyContractorsTop(string dn) => Permitted(dn) && !dn.StartsWith("CN=Bo Lee", StringComparison.Ordinal);
        Assert.Contains("CN=Bo Lee,OU=Remote," + Ou + ", which is not below a permitted base DN",
            Refusal(Facts(OuNode(), 2, Subtree()), subtree: true, confirmed: 10, permitted: OnlyContractorsTop));
    }

    [Fact]
    public void The_domain_root_partitions_and_protected_targets_are_refused()
    {
        foreach (var head in Contexts)
            Assert.Contains("head of a naming context", Refusal(Facts(Node(head, "domainDNS", uac: 0), 12), subtree: true, confirmed: 99));
        Assert.Contains("configuration partition", Refusal(Facts(Node("CN=Sites," + Config, "sitesContainer", uac: 0))));
        Assert.Contains("schema partition", Refusal(Facts(Node("CN=Person," + Schema, "classSchema", uac: 0))));
        Assert.Equal("it is a critical system object (isCriticalSystemObject is TRUE).", Refusal(Facts(Node("CN=Users," + Domain, "container", critical: true, uac: 0))));
        Assert.Equal("it is protected by AdminSDHolder (adminCount is 1).", Refusal(Facts(Node("CN=Jo Admin," + Ou, adminCount: true))));
        Assert.Contains("domain controller's computer account", Refusal(Facts(Node("CN=DC2,OU=Servers," + Domain, "computer", uac: 0x2000 | 0x80000))));
        Assert.Contains("Domain Controllers OU", Refusal(Facts(Node("CN=Stray,OU=Domain Controllers," + Domain, "contact", uac: 0))));
    }

    [Fact]
    public void A_delete_must_lie_strictly_below_a_permitted_base()
    {
        Assert.False(BaseDnContainment.IsStrictlyBelow(Ou, new[] { Ou }));
        Assert.False(BaseDnContainment.IsStrictlyBelow("ou=contractors, dc=corp, dc=local", new[] { Ou }));
        Assert.True(BaseDnContainment.IsStrictlyBelow("CN=Ana Rios," + Ou, new[] { Ou }));
        Assert.False(BaseDnContainment.IsStrictlyBelow("CN=Ana Rios,OU=Other," + Domain, new[] { Ou }));
        Assert.False(BaseDnContainment.IsStrictlyBelow("CN=Ana Rios," + Ou, null));
        Assert.True(BaseDnContainment.IsContained(Ou, new[] { Ou }));   // creates and moves keep the inclusive rule
        Assert.Contains("not below a permitted base DN", Refusal(Facts(OuNode()), permitted: dn => BaseDnContainment.IsStrictlyBelow(dn, new[] { Ou })));
    }

    /// <summary>A directory in memory: objects by GUID, a hook that changes one just before its read.</summary>
    private sealed class FakeDirectory : IAdDeleteDirectory
    {
        public readonly Dictionary<Guid, AdDeleteNode> Objects = new();
        public readonly List<Guid> DeleteCalls = new();
        public Func<AdDeleteNode, AdDeleteNode?>? OnRead;
        public Guid? NonLeaf;
        public int? DropAfterDeletes;

        public FakeDirectory(IEnumerable<AdDeleteNode> nodes) { foreach (var n in nodes) Objects[n.ObjectGuid] = n; }

        public AdDeleteNode? Read(Guid g)
        {
            if (!Objects.TryGetValue(g, out var n)) return null;
            return OnRead is null ? n : OnRead(n);
        }

        public AdDeleteOutcome Delete(Guid g)
        {
            if (DropAfterDeletes is { } n && DeleteCalls.Count >= n)
                throw new System.DirectoryServices.Protocols.LdapException(81, "The server is not operational.");
            DeleteCalls.Add(g);
            if (g == NonLeaf) return AdDeleteOutcome.NotLeaf;
            return Objects.Remove(g) ? AdDeleteOutcome.Deleted : AdDeleteOutcome.Gone;
        }
    }

    private static (AdDeletePlan Plan, FakeDirectory Dir) Planned()
    {
        var target = OuNode();
        var sub = Subtree();
        var (plan, _) = AdObjectDeletePlan.Decide(Facts(target, 2, sub), includeSubtree: true, confirmedCount: 3, Permitted);
        return (plan!, new FakeDirectory(sub.Append(target)));
    }

    [Fact]
    public void A_run_deletes_one_object_per_call_deepest_first_and_finishes()
    {
        var (plan, dir) = Planned();
        var run = AdObjectDeletePlan.Run(dir, plan, Contexts, Permitted);
        Assert.True(run.Complete);
        Assert.Equal(plan.Order.Select(n => n.ObjectGuid), dir.DeleteCalls);
        Assert.Equal(4, run.Deleted.Count);
        Assert.Empty(dir.Objects);
    }

    [Fact]
    public void An_object_that_turned_protected_mid_run_stops_the_run_and_reports_what_was_deleted()
    {
        var (plan, dir) = Planned();
        var second = plan.Order[1];
        dir.OnRead = n => n.ObjectGuid == second.ObjectGuid ? n with { AdminCount = true } : n;
        var run = AdObjectDeletePlan.Run(dir, plan, Contexts, Permitted);
        Assert.False(run.Complete);
        Assert.Equal(new[] { plan.Order[0].ObjectGuid }, run.Deleted.Select(d => d.ObjectGuid));
        Assert.Contains("AdminSDHolder", run.StoppedReason);
        Assert.Equal(new[] { plan.Order[0].ObjectGuid }, dir.DeleteCalls);   // nothing after the stop
    }

    [Fact]
    public void An_object_that_moved_out_or_gained_children_stops_the_run()
    {
        var (plan, dir) = Planned();
        var first = plan.Order[0];
        dir.OnRead = n => n.ObjectGuid == first.ObjectGuid ? n with { DistinguishedName = "CN=Bo Lee,OU=Elsewhere," + Domain } : n;
        var moved = AdObjectDeletePlan.Run(dir, plan, Contexts, Permitted);
        Assert.Empty(moved.Deleted);
        Assert.Contains("moved since it was listed", moved.StoppedReason);
        Assert.Empty(dir.DeleteCalls);

        var (plan2, dir2) = Planned();
        dir2.NonLeaf = plan2.Order[2].ObjectGuid;   // OU=Remote gained a child after the listing
        var grew = AdObjectDeletePlan.Run(dir2, plan2, Contexts, Permitted);
        Assert.Equal(2, grew.Deleted.Count);
        Assert.Contains("has objects under it that were not listed", grew.StoppedReason);
        Assert.Equal(3, dir2.DeleteCalls.Count);
    }

    [Fact]
    public void A_connection_drop_mid_run_stops_and_still_reports_exactly_what_was_deleted()
    {
        var (plan, dir) = Planned();
        dir.DropAfterDeletes = 2;
        var run = AdObjectDeletePlan.Run(dir, plan, Contexts, Permitted);
        Assert.False(run.Complete);
        Assert.Equal(plan.Order.Take(2).Select(n => n.ObjectGuid), run.Deleted.Select(d => d.ObjectGuid));
        Assert.Contains("could not be reached", run.StoppedReason);
        Assert.Contains("The server is not operational.", run.StoppedReason);
        using var doc = JsonDocument.Parse(AdObjectDeletePlan.ResultJson(plan.Target, run));
        Assert.Equal(2, doc.RootElement.GetProperty("deleted").GetArrayLength());
        Assert.False(doc.RootElement.GetProperty("complete").GetBoolean());

        var executor = File.ReadAllText(Src("src/Conduit.Web/Services/AdAgentWriteExecutor.cs"));
        var runAt = executor.IndexOf("AdObjectDeletePlan.Run(new LdapDeleteDirectory(conn)", StringComparison.Ordinal);
        Assert.True(executor.IndexOf("LastResultJson = AdObjectDeletePlan.ResultJson(plan.Target, run);", runAt, StringComparison.Ordinal) > runAt);
    }

    [Fact]
    public void A_subtree_whose_result_would_not_fit_is_refused_before_any_write()
    {
        // 140 objects with long non-ASCII names: the result escapes each such character to six bytes.
        var name = new string((char)0x00e9, 120);
        var sub = Enumerable.Range(0, 140).Select(i => Node($"CN={name}{i}," + Ou)).ToList();
        Assert.Equal("too many objects with long names to report in one delete; delete part of the subtree first.",
            Refusal(Facts(OuNode(), 140, sub), subtree: true, confirmed: 140));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(AdObjectDeletePlan.ResultJson(OuNode(), new AdDeleteRun(sub, null))) > AdObjectDeletePlan.MaxResultBytes);
        var shortNames = Enumerable.Range(0, 140).Select(i => Node($"CN=u{i}," + Ou)).ToList();
        Assert.NotNull(AdObjectDeletePlan.Decide(Facts(OuNode(), 140, shortNames), true, 140, Permitted).Plan);
    }

    [Fact]
    public void The_result_lists_every_deleted_object_by_GUID()
    {
        var (plan, dir) = Planned();
        var run = AdObjectDeletePlan.Run(dir, plan, Contexts, Permitted);
        using var doc = JsonDocument.Parse(AdObjectDeletePlan.ResultJson(plan.Target, run));
        var root = doc.RootElement;
        Assert.Equal(Ou, root.GetProperty("distinguishedName").GetString());
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.Equal(4, root.GetProperty("deletedCount").GetInt32());
        var deleted = root.GetProperty("deleted").EnumerateArray().ToList();
        Assert.Equal(plan.Order.Select(n => n.ObjectGuid.ToString()), deleted.Select(d => d.GetProperty("objectGuid").GetString()));
        Assert.Equal("organizationalUnit", deleted[^1].GetProperty("objectClass").GetString());
    }

    [Fact]
    public void The_payload_IdentityCenter_sends_round_trips_through_the_executors_parse()
    {
        const string payload = "{\"schemaVersion\":1,\"objectGuid\":\"0786a4f0-5555-4555-8555-000000000011\",\"dnHint\":\"OU=Contractors,DC=corp,DC=local\","
            + "\"connectionId\":\"a29f3008-050b-44d4-b56d-2dc1bc5d6b56\",\"sourceConnectionName\":\"corp\",\"objectClass\":\"user\",\"operation\":\"DeleteObject\","
            + "\"includeSubtree\":true,\"interactive\":true,\"confirmedCount\":7}";
        var guid = Guid.Parse("0786a4f0-5555-4555-8555-000000000011");
        Assert.Equal((guid, true, true, (int?)7), AdAgentWriteExecutor.ReadDeleteRequest(payload));
        Assert.Equal((guid, false, false, (int?)null), AdAgentWriteExecutor.ReadDeleteRequest(
            payload.Replace(",\"includeSubtree\":true,\"interactive\":true,\"confirmedCount\":7", "")));
        Assert.Null(AdAgentWriteExecutor.ReadDeleteRequest(payload.Replace("DeleteObject", "MoveObject")));
    }

    private static string Src(string relative, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void A_system_delete_keeps_the_original_class_list_and_never_takes_a_subtree()
    {
        var executor = File.ReadAllText(Src("src/Conduit.Web/Services/AdAgentWriteExecutor.cs"));
        var body = executor[executor.IndexOf("private async Task<(bool, string)> DoDeleteObjectAsync", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("public static (Guid ObjectGuid", StringComparison.Ordinal)];
        Assert.Contains("if (includeSubtree && !interactive)", body);
        Assert.Contains("if (!interactive && !(target.IsClass(\"user\") || target.IsClass(\"group\") ||", body);
        Assert.Contains("only user, group, computer, or contact objects may be deleted through this channel.", body);
        var strict = body.IndexOf("BaseDnContainment.IsStrictlyBelow(dn, permitted)", StringComparison.Ordinal);
        var decided = body.IndexOf("AdObjectDeletePlan.Decide(facts, includeSubtree, p.ConfirmedCount, IsPermitted)", StringComparison.Ordinal);
        var run = body.IndexOf("AdObjectDeletePlan.Run(new LdapDeleteDirectory(conn), plan, facts.NamingContexts, IsPermitted, ct)", StringComparison.Ordinal);
        Assert.True(strict >= 0 && decided > strict && run > decided);
        Assert.DoesNotContain("BaseDnContainment.IsContained", body);
    }
}
