using System;
using System.Collections.Generic;
using System.Linq;
using Conduit.Connectors.ActiveDirectory;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// DYNAMIC-GROUPS-01 (UC034-D01, D02): the Active Roles definition as the lab's groups carry it, read without a
/// directory; the precedence the help states, applied to what each rule matched; the difference a rebuild would write.
/// The two blobs are the lab's shapes (dynamo_#2 and Test Group 99, read over LDAP 2026-11-02) with full GUIDs.
/// </summary>
public class DynamicGroupRulesTests
{
    private const string Container = "607949aa-1111-4111-8111-000000000001";
    private const string Query = "978ce413-2222-4222-8222-000000000002";
    private const string Explicit1 = "e21fc5aa-3333-4333-8333-000000000003";
    private const string Explicit2 = "f8fe5b65-4444-4444-8444-000000000004";
    private const string Group99 = "0786a4f0-5555-4555-8555-000000000005";
    private const string Service = "06b43ba8-b20f-486b-9ce5-1d90101e6d34";

    private const string LabFilter = "(&(|(&(objectCategory=person)(objectSid=*)(!(sAMAccountType:1.2.840.113556.1.4.804:=3)))(&(objectCategory=person)(!(objectSid=*)))(&(objectCategory=group)(groupType:1.2.840.113556.1.4.804:=14)))(!(userAccountControl:1.2.840.113556.1.4.804:=2048)))";

    /// <summary>The dynamo_#2 shape: three explicit includes around one query, '&amp;' raw as the service writes it.</summary>
    private static readonly string Dynamo2 =
        $"<DynamicGroupXML Conditions=\"[0x3;{Container};][0x1;{Query};{LabFilter}][0x3;{Explicit1};][0x3;{Explicit2};][DG]\" OriginatingService=\"{Service}\" />";

    /// <summary>The Test Group 99 shape: one Include Group Members rule.</summary>
    private static readonly string TestGroup99 =
        $"<DynamicGroupXML Conditions=\"[0x5;{Group99};][DG]\" OriginatingService=\"5ce50057-6666-4666-8666-000000000006\" />";

    [Fact]
    public void The_lab_blob_with_a_raw_ampersand_parses_to_its_four_rules_and_its_service()
    {
        Assert.True(DynamicGroupRules.TryParse(Dynamo2, out var definition, out var error), error);
        var rules = definition!.Rules;
        Assert.Equal(4, rules.Count);
        Assert.Equal(DynamicGroupRuleType.IncludeExplicitly, rules[0].Type);
        Assert.Equal(Guid.Parse(Container), rules[0].ObjectGuid);
        Assert.Null(rules[0].Filter);
        Assert.Equal(DynamicGroupRuleType.IncludeByQuery, rules[1].Type);
        Assert.Equal(Guid.Parse(Query), rules[1].ObjectGuid);
        Assert.Equal(LabFilter, rules[1].Filter);   // verbatim, '&' and the bit-matching rules intact
        Assert.Equal(DynamicGroupRuleType.IncludeExplicitly, rules[2].Type);
        Assert.Equal(DynamicGroupRuleType.IncludeExplicitly, rules[3].Type);
        Assert.Equal(Guid.Parse(Service), definition.OriginatingService);
    }

    [Fact]
    public void A_well_formed_xml_blob_parses_the_same_with_the_ampersand_escaped()
    {
        var escaped = Dynamo2.Replace("&", "&amp;");
        Assert.True(DynamicGroupRules.TryParse(escaped, out var definition, out var error), error);
        Assert.Equal(LabFilter, definition!.Rules[1].Filter);
    }

    [Fact]
    public void The_group_members_blob_parses_to_one_rule()
    {
        Assert.True(DynamicGroupRules.TryParse(TestGroup99, out var definition, out var error), error);
        var rule = Assert.Single(definition!.Rules);
        Assert.Equal(DynamicGroupRuleType.IncludeGroupMembers, rule.Type);
        Assert.Equal(Guid.Parse(Group99), rule.ObjectGuid);
        Assert.True(rule.IsInclude);
        Assert.True(rule.IsGroupMembers);
    }

    [Theory]
    [InlineData("0x2", DynamicGroupRuleType.ExcludeByQuery)]
    [InlineData("0x4", DynamicGroupRuleType.ExcludeExplicitly)]
    [InlineData("0x6", DynamicGroupRuleType.ExcludeGroupMembers)]
    [InlineData("0x7", DynamicGroupRuleType.RetainDeprovisioned)]
    [InlineData("3", DynamicGroupRuleType.IncludeExplicitly)]
    public void Every_type_code_is_read_in_hex_or_decimal(string code, DynamicGroupRuleType expected)
    {
        Assert.True(DynamicGroupRules.TryParseConditions($"[{code};{Explicit1};]", out var rules, out var error), error);
        Assert.Equal(expected, Assert.Single(rules!).Type);
    }

    [Theory]
    [InlineData(null, "no DynamicGroupXML")]
    [InlineData("", "no DynamicGroupXML")]
    [InlineData("just a note", "no DynamicGroupXML")]
    [InlineData("<DynamicGroupXML OriginatingService=\"x\" />", "no Conditions")]
    [InlineData("<DynamicGroupXML Conditions=\"[0x9;e21fc5aa-3333-4333-8333-000000000003;][DG]\" />", "not one of the seven")]
    [InlineData("<DynamicGroupXML Conditions=\"[0x3;not-a-guid;][DG]\" />", "names no objectGUID")]
    [InlineData("<DynamicGroupXML Conditions=\"[0x3;e21fc5aa-3333-4333-8333-000000000003;\" />", "Unclosed")]
    [InlineData("<DynamicGroupXML Conditions=\"[0x3][DG]\" />", "not type;objectGUID;filter")]
    [InlineData("<DynamicGroupXML Conditions=\"\" />", "empty")]
    public void A_blob_that_is_not_a_definition_is_refused_with_the_reason(string? blob, string reason)
    {
        Assert.False(DynamicGroupRules.TryParse(blob, out var definition, out var error));
        Assert.Null(definition);
        Assert.Contains(reason, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_filter_with_a_semicolon_stays_whole()
    {
        Assert.True(DynamicGroupRules.TryParseConditions($"[0x1;{Container};(description=a;b)][DG]", out var rules, out var error), error);
        Assert.Equal("(description=a;b)", Assert.Single(rules!).Filter);
    }

    [Fact]
    public void The_precedence_is_the_helps_order()
    {
        var order = new[]
        {
            DynamicGroupRuleType.ExcludeExplicitly, DynamicGroupRuleType.IncludeExplicitly, DynamicGroupRuleType.ExcludeByQuery,
            DynamicGroupRuleType.ExcludeGroupMembers, DynamicGroupRuleType.IncludeByQuery, DynamicGroupRuleType.IncludeGroupMembers,
        };
        for (var i = 1; i < order.Length; i++)
            Assert.True(DynamicGroupRules.Precedence(order[i - 1]) > DynamicGroupRules.Precedence(order[i]), order[i - 1] + " over " + order[i]);
        Assert.Equal(0, DynamicGroupRules.Precedence(DynamicGroupRuleType.RetainDeprovisioned));
    }

    private static DynamicGroupRule Rule(DynamicGroupRuleType type) => new(type, Guid.NewGuid(), null);

    [Fact]
    public void An_object_is_decided_by_the_highest_precedence_rule_that_matched_it()
    {
        var evaluations = new List<(DynamicGroupRule, IReadOnlyCollection<string>)>
        {
            (Rule(DynamicGroupRuleType.IncludeByQuery), new[] { "CN=A,OU=X", "CN=B,OU=X", "CN=C,OU=X", "CN=D,OU=X" }),
            (Rule(DynamicGroupRuleType.ExcludeByQuery), new[] { "CN=B,OU=X", "CN=C,OU=X" }),        // B and C out by query
            (Rule(DynamicGroupRuleType.IncludeExplicitly), new[] { "CN=C,OU=X", "CN=E,OU=X" }),     // C back in, E in: explicit beats query
            (Rule(DynamicGroupRuleType.ExcludeExplicitly), new[] { "CN=E,OU=X" }),                   // E out: the strongest rule
            (Rule(DynamicGroupRuleType.IncludeGroupMembers), new[] { "CN=F,OU=X", "CN=B,OU=X" }),   // F in; B still out (exclude by query beats include members)
            (Rule(DynamicGroupRuleType.ExcludeGroupMembers), new[] { "CN=D,OU=X" }),                 // D out: exclude members beats include by query
        };

        var desired = DynamicGroupRules.Decide(evaluations);

        Assert.Equal(new[] { "CN=A,OU=X", "CN=C,OU=X", "CN=F,OU=X" }, desired);
    }

    [Fact]
    public void An_exclude_rule_alone_puts_nothing_in_and_the_group_is_never_its_own_member()
    {
        var self = "CN=Dynamo,OU=X";
        var evaluations = new List<(DynamicGroupRule, IReadOnlyCollection<string>)>
        {
            (Rule(DynamicGroupRuleType.ExcludeExplicitly), new[] { "CN=Z,OU=X" }),
            (Rule(DynamicGroupRuleType.IncludeByQuery), new[] { "cn=dynamo,ou=x", "CN=A,OU=X" }),
            (Rule(DynamicGroupRuleType.RetainDeprovisioned), new[] { "CN=R,OU=X" }),   // a unit's rule: nothing for a group
        };

        Assert.Equal(new[] { "CN=A,OU=X" }, DynamicGroupRules.Decide(evaluations, self));
    }

    [Fact]
    public void Names_compare_case_insensitively_and_keep_their_first_spelling()
    {
        var evaluations = new List<(DynamicGroupRule, IReadOnlyCollection<string>)>
        {
            (Rule(DynamicGroupRuleType.IncludeByQuery), new[] { "CN=Casey,OU=X" }),
            (Rule(DynamicGroupRuleType.IncludeGroupMembers), new[] { "cn=casey,ou=x" }),
        };
        Assert.Equal(new[] { "CN=Casey,OU=X" }, DynamicGroupRules.Decide(evaluations));
    }

    [Fact]
    public void The_difference_is_what_a_rebuild_would_write()
    {
        var desired = new[] { "CN=A,OU=X", "CN=B,OU=X", "CN=C,OU=X" };
        var current = new[] { "cn=b,ou=x", "CN=Native,OU=X", "CN=Gone,OU=X" };

        var delta = DynamicGroupRules.Diff(desired, current);

        Assert.Equal(new[] { "CN=A,OU=X", "CN=C,OU=X" }, delta.Adds);
        Assert.Equal(new[] { "CN=Gone,OU=X", "CN=Native,OU=X" }, delta.Removes);   // a native add is undone by the next evaluation (R01)
        Assert.Same(desired, delta.Desired);
    }

    // ── DYNAMIC-GROUPS-04: the serializer and the writability check ─────────

    [Fact]
    public void A_definition_round_trips_through_the_serializer_in_the_services_own_spelling()
    {
        Assert.True(DynamicGroupRules.TryParse(Dynamo2, out var definition, out var error), error);
        var text = DynamicGroupRules.Serialize(definition!.Rules, definition.OriginatingService);

        Assert.Equal(Dynamo2, text);   // byte for byte the lab's shape: hex types, raw '&', [DG], the service GUID
        Assert.True(DynamicGroupRules.TryParse(text, out var again, out error), error);
        Assert.Equal(definition.Rules, again!.Rules);
        Assert.Equal(definition.OriginatingService, again.OriginatingService);
    }

    [Fact]
    public void A_definition_written_without_a_service_names_IdentityCenters()
    {
        var text = DynamicGroupRules.Serialize(new[] { new DynamicGroupRule(DynamicGroupRuleType.IncludeExplicitly, Guid.Parse(Explicit1), null) });
        Assert.Equal($"<DynamicGroupXML Conditions=\"[0x3;{Explicit1};][DG]\" OriginatingService=\"{DynamicGroupRules.IdentityCenterService}\" />", text);
        Assert.NotEqual(Guid.Parse(Service), DynamicGroupRules.IdentityCenterService);   // the lab's Administration Service will not take it for its own
    }

    [Fact]
    public void An_empty_rule_list_is_a_dynamic_group_with_nothing_to_include()
    {
        var text = DynamicGroupRules.Serialize(Array.Empty<DynamicGroupRule>());
        Assert.True(DynamicGroupRules.TryParse(text, out var definition, out var error), error);
        Assert.Empty(definition!.Rules);
    }

    [Theory]
    [InlineData(DynamicGroupRuleType.IncludeByQuery, "(objectCategory=person)", true, null)]
    [InlineData(DynamicGroupRuleType.ExcludeByQuery, "(&(objectCategory=person)(department=Finance))", true, null)]
    [InlineData(DynamicGroupRuleType.IncludeExplicitly, null, true, null)]
    [InlineData(DynamicGroupRuleType.ExcludeGroupMembers, null, true, null)]
    [InlineData(DynamicGroupRuleType.RetainDeprovisioned, null, false, "six membership rule types")]
    [InlineData(DynamicGroupRuleType.IncludeByQuery, null, false, "needs an LDAP filter")]
    [InlineData(DynamicGroupRuleType.IncludeByQuery, "objectCategory=person", false, "needs an LDAP filter")]
    [InlineData(DynamicGroupRuleType.IncludeByQuery, "(&(a=1)(b=2)", false, "needs an LDAP filter")]
    [InlineData(DynamicGroupRuleType.IncludeByQuery, "(cn=a[1])", false, "needs an LDAP filter")]
    public void Only_a_groups_six_types_with_a_sound_filter_are_writable(DynamicGroupRuleType type, string? filter, bool writable, string? reason)
    {
        var ok = DynamicGroupRules.IsWritable(new DynamicGroupRule(type, Guid.Parse(Explicit1), filter), out var why);
        Assert.Equal(writable, ok);
        if (reason is not null) Assert.Contains(reason, why);
    }

    [Fact]
    public void A_rule_without_an_object_is_not_writable()
    {
        Assert.False(DynamicGroupRules.IsWritable(new DynamicGroupRule(DynamicGroupRuleType.IncludeExplicitly, Guid.Empty, null), out var why));
        Assert.Contains("names no object", why);
    }

    [Fact]
    public void The_wizards_names_are_the_report_vocabulary()
    {
        Assert.Equal("Include by Query", DynamicGroupRules.Describe(DynamicGroupRuleType.IncludeByQuery));
        Assert.Equal("Exclude Group Members", DynamicGroupRules.Describe(DynamicGroupRuleType.ExcludeGroupMembers));
        Assert.Equal("accountNameHistory", DynamicGroupRules.AttributeName);
    }
}
