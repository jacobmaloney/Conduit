using Conduit.Connectors.ActiveDirectory;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// MOVE-RDN-01: a move keeps the whole leaf RDN. IdentityCenter's drag of "CN=User3\,Employee" (2026-11-02) sent
/// ModifyDN with the RDN cut at the escaped comma and AD answered "The distinguished name contains invalid syntax".
/// </summary>
public class ActiveDirectorySinkRdnTests
{
    [Theory]
    [InlineData(@"CN=User3\,Employee,OU=Attestation,OU=Organization,DC=Domain,DC=Local", @"CN=User3\,Employee", "OU=Attestation,OU=Organization,DC=Domain,DC=Local")]
    [InlineData("CN=Plain,OU=Users,DC=corp,DC=local", "CN=Plain", "OU=Users,DC=corp,DC=local")]
    [InlineData(@"OU=Sales\, West,OU=Org,DC=corp,DC=local", @"OU=Sales\, West", "OU=Org,DC=corp,DC=local")]
    [InlineData(@"CN=Back\\slash,OU=Org,DC=corp,DC=local", @"CN=Back\\slash", "OU=Org,DC=corp,DC=local")]
    public void The_leaf_rdn_ends_at_the_first_unescaped_comma(string dn, string rdn, string parent)
    {
        var leaf = ActiveDirectorySink.SplitLeafRdn(dn);
        Assert.NotNull(leaf);
        Assert.Equal(rdn, leaf!.Value.Rdn);
        Assert.Equal(parent, leaf.Value.Parent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("CN=NoParent")]
    [InlineData("junk,OU=Org,DC=corp,DC=local")]
    public void A_dn_without_a_leaf_and_a_parent_is_refused(string dn)
    {
        Assert.Null(ActiveDirectorySink.SplitLeafRdn(dn));
    }
}
