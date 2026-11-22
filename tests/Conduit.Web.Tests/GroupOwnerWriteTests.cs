using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// GROUP-OWNER-01: set or clear the owner (managedBy) of an EXISTING group through the agent. A group
/// create could already carry an owner; nothing could change it afterwards (IdentityCenter UC039,
/// Jacob 2026-11-21: "fix the gap"). The operation is its own, like SetManager for a user, and is
/// stricter than SetManager in three ways this file pins: the owner arrives as an objectGuid and is
/// resolved against the directory, a privileged group is refused, and a clear has to be asked for.
///
/// No LDAP in a unit test (the convention of AgentPriorValuesTests), so the checks, their order and
/// the result shape are pinned by source.
/// </summary>
public class GroupOwnerWriteTests
{
    private static readonly string Executor = Read("src/Conduit.Web/Services/AdAgentWriteExecutor.cs").Replace("\r\n", "\n");

    private static string Method()
    {
        var start = Executor.IndexOf("private async Task<(bool, string)> DoSetGroupOwnerAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "DoSetGroupOwnerAsync is gone.");
        var method = Executor[start..];
        return method[..method.IndexOf("// ── Enable / Disable", StringComparison.Ordinal)];
    }

    [Fact]
    public void The_operation_is_in_the_closed_set_and_is_dispatched()
    {
        Assert.Contains("private const string OpSetGroupOwner = \"SetGroupOwner\";", Executor);

        var allowed = Executor[Executor.IndexOf("private static readonly HashSet<string> AllowedOperations", StringComparison.Ordinal)..];
        allowed = allowed[..allowed.IndexOf("};", StringComparison.Ordinal)];
        Assert.Contains("OpSetGroupOwner", allowed);

        Assert.Contains("OpSetGroupOwner => await DoSetGroupOwnerAsync(sink, target, p, ct),", Executor);
    }

    [Fact]
    public void It_refuses_in_this_order_before_it_reads_or_writes_anything()
    {
        var method = Method();
        int At(string text)
        {
            var i = method.IndexOf(text, StringComparison.Ordinal);
            Assert.True(i >= 0, "missing: " + text);
            return i;
        }

        var notAGroup = At("if (!target.IsClass(\"group\"))");
        var privileged = At("if (IsPrivilegedGroup(target, out var why))");
        var noAttributes = At("if (p.Attributes is { Count: > 0 })");
        var ownerOrClear = At("if (clear == hasOwner)");
        var resolveOwner = At("await sink.ResolveByGuidAsync(p.OwnerObjectGuid!, ct)");
        var ownerIsUser = At("if (!owner.IsClass(\"user\") || owner.IsClass(\"computer\"))");
        var priorRead = At("await sink.ReadAttributesByGuidAsync(p.ObjectGuid!, new[] { \"managedBy\" }, ct)");
        var write = At("await sink.SetAttributeByGuidAsync(p.ObjectGuid!, \"managedBy\", ownerDn, ct)");

        Assert.True(notAGroup < privileged && privileged < noAttributes && noAttributes < ownerOrClear
                    && ownerOrClear < resolveOwner && resolveOwner < ownerIsUser && ownerIsUser < priorRead && priorRead < write,
            "the refusals no longer all come before the read and the write, in order.");
    }

    [Fact]
    public void A_privileged_group_is_refused_by_the_membership_rule_not_a_copy_of_it()
    {
        var method = Method();
        Assert.Contains("IsPrivilegedGroup(target, out var why)", method);
        Assert.DoesNotContain("PrivilegedRids", method);
        Assert.DoesNotContain("PrivilegedNames", method);
        Assert.Contains("refusing to change the owner of a privileged group", method);
    }

    [Fact]
    public void The_owner_is_resolved_from_a_guid_and_never_taken_as_a_distinguished_name()
    {
        var method = Method();

        // The only value ever written is the resolved object's own DN.
        Assert.Contains("ownerDn = owner.DistinguishedName;", method);
        Assert.Single(Regex.Matches(method, @"ownerDn = (?!null)"));
        Assert.DoesNotContain("DnHint", method);
        Assert.DoesNotContain("JsonValueToString", method);        // nothing is read out of the attribute bag
        Assert.Contains("ApplyObjectWrite: ownerObjectGuid is not a GUID.", method);
    }

    [Fact]
    public void A_clear_has_to_be_asked_for()
    {
        var method = Method();
        Assert.Contains("var clear = p.ClearOwner == true;", method);
        Assert.Contains("SetGroupOwner names no owner. Send ownerObjectGuid, or clearOwner to remove the owner.", method);
        Assert.Contains("SetGroupOwner names an owner and asks to clear it; send one or the other.", method);

        Assert.Contains("[JsonPropertyName(\"ownerObjectGuid\")] public string? OwnerObjectGuid { get; set; }", Executor);
        Assert.Contains("[JsonPropertyName(\"clearOwner\")] public bool? ClearOwner { get; set; }", Executor);
    }

    [Fact]
    public void It_writes_managedBy_only_and_reports_what_was_there_before()
    {
        var method = Method();
        Assert.Single(Regex.Matches(method, @"sink\.SetAttributeByGuidAsync\("));
        Assert.Contains("LastResultJson = JsonSerializer.Serialize(new { priorValues = prior, ownerDn });", method);
        Assert.Contains("if (r.Outcome == SinkWriteOutcome.Failed)", method);
    }

    [Fact]
    public void The_SetAttributes_and_SetManager_sections_are_where_the_other_tests_expect_them()
    {
        // AgentPriorValuesTests and AdAttributeValueShaperTests cut SetAttributes at the SetManager marker.
        var setAttributes = Executor.IndexOf("private async Task<(bool, string)> DoSetAttributesAsync(", StringComparison.Ordinal);
        var setManager = Executor.IndexOf("// ── SetManager", StringComparison.Ordinal);
        var groupOwner = Executor.IndexOf("// ── SetGroupOwner (GROUP-OWNER-01)", StringComparison.Ordinal);
        Assert.True(setAttributes > 0 && setManager > setAttributes && groupOwner > setManager);

        // SetAttributes is still user-only: a group's owner does not ride in on the user attribute list.
        Assert.DoesNotContain("allowed[\"managedBy\"]", Executor);
    }

    private static string Read(string relative, [CallerFilePath] string thisFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relative.Replace('/', Path.DirectorySeparatorChar))));
}
