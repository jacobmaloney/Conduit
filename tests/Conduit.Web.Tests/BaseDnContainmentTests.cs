using Conduit.Web.Services;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// Tests for the agent-side creation containment control (design §8): component-wise RDN-suffix
/// containment with a deny-all default. This is THE real OU containment — the IC side only checks
/// well-formedness. The bypass a naive EndsWith would allow MUST be caught here.
/// </summary>
public class BaseDnContainmentTests
{
    private static string[] One(string dn) => new[] { dn };

    [Theory]
    // Child of a permitted base -> contained.
    [InlineData("OU=Staff,OU=Users,DC=corp,DC=local", "OU=Users,DC=corp,DC=local", true)]
    // Exactly the permitted base (create directly in it) -> contained.
    [InlineData("OU=Users,DC=corp,DC=local", "OU=Users,DC=corp,DC=local", true)]
    // A sibling OU -> NOT contained.
    [InlineData("OU=Staff,DC=corp,DC=local", "OU=Users,DC=corp,DC=local", false)]
    // Case-insensitive on both type and value.
    [InlineData("ou=staff,ou=users,dc=CORP,dc=Local", "OU=Users,DC=corp,DC=local", true)]
    // A shorter DN than the base cannot be inside it.
    [InlineData("DC=corp,DC=local", "OU=Users,DC=corp,DC=local", false)]
    public void Containment_cases(string target, string permitted, bool expected)
        => Assert.Equal(expected, BaseDnContainment.IsContained(target, One(permitted)));

    [Fact]
    public void EndsWith_bypass_is_denied_component_wise()
    {
        // A value that shares a suffix substring with the base but is a DIFFERENT RDN. A naive
        // string EndsWith / substring check is exactly what this must beat.
        var permitted = One("OU=Staff,DC=corp,DC=local");
        Assert.False(BaseDnContainment.IsContained("OU=X,OU=EvilStaff,DC=corp,DC=local", permitted));
        Assert.False(BaseDnContainment.IsContained("OU=EvilStaff,DC=corp,DC=local", permitted));
        Assert.False(BaseDnContainment.IsContained("OU=NightStaff,DC=corp,DC=local", permitted));
    }

    [Fact]
    public void Escaped_comma_in_a_value_is_one_rdn_not_two()
    {
        // The permitted base has a single OU whose value literally contains a comma ("A,B").
        var permitted = One("OU=A\\,B,DC=corp,DC=local");
        // A child of that OU is contained...
        Assert.True(BaseDnContainment.IsContained("OU=Child,OU=A\\,B,DC=corp,DC=local", permitted));
        // ...and the exact escaped-comma OU is contained.
        Assert.True(BaseDnContainment.IsContained("OU=A\\,B,DC=corp,DC=local", permitted));
        // But a DN that only matches if you naively split on the raw comma is NOT contained.
        Assert.False(BaseDnContainment.IsContained("OU=B,DC=corp,DC=local", permitted));
    }

    [Fact]
    public void Empty_allow_list_denies_everything()
    {
        Assert.False(BaseDnContainment.IsContained("OU=Staff,DC=corp,DC=local", System.Array.Empty<string>()));
    }

    [Fact]
    public void Null_allow_list_denies()
    {
        Assert.False(BaseDnContainment.IsContained("OU=Staff,DC=corp,DC=local", null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a dn")]           // no '='
    [InlineData("OU=,DC=corp")]        // empty value
    [InlineData("OU=Staff,,DC=corp")]  // doubled comma
    public void Malformed_or_blank_target_is_denied(string? target)
    {
        Assert.False(BaseDnContainment.IsContained(target, One("OU=Staff,DC=corp,DC=local")));
    }

    [Fact]
    public void Malformed_permitted_base_grants_nothing_but_a_valid_one_still_matches()
    {
        // A malformed base in the set must not grant anything (fail-closed); a valid one alongside it works.
        var mixed = new[] { "this is not a dn", "OU=Users,DC=corp,DC=local" };
        Assert.True(BaseDnContainment.IsContained("OU=Staff,OU=Users,DC=corp,DC=local", mixed));

        var onlyMalformed = new[] { "garbage", "OU=,DC=x" };
        Assert.False(BaseDnContainment.IsContained("OU=Staff,OU=Users,DC=corp,DC=local", onlyMalformed));
    }

    [Fact]
    public void Any_of_several_permitted_bases_matches()
    {
        var permitted = new[] { "OU=Contractors,DC=corp,DC=local", "OU=Staff,DC=corp,DC=local" };
        Assert.True(BaseDnContainment.IsContained("CN=x,OU=Staff,DC=corp,DC=local", permitted));
        Assert.False(BaseDnContainment.IsContained("CN=x,OU=Admins,DC=corp,DC=local", permitted));
    }

    // ── IsWellFormedDn (used by the settings UI to reject a malformed base DN at save) ──

    [Theory]
    [InlineData("OU=Staff,OU=Users,DC=corp,DC=local")]
    [InlineData("DC=corp,DC=local")]
    [InlineData("OU=A\\,B,DC=corp,DC=local")]   // escaped comma
    [InlineData("ou=staff,dc=corp,dc=local")]   // lower-case
    public void Well_formed_dns_are_accepted(string dn) => Assert.True(BaseDnContainment.IsWellFormedDn(dn));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a dn")]           // no '='
    [InlineData("OU=,DC=corp")]        // empty value
    [InlineData("OU=Staff,,DC=corp")]  // doubled comma
    public void Malformed_dns_are_rejected(string? dn) => Assert.False(BaseDnContainment.IsWellFormedDn(dn));
}
