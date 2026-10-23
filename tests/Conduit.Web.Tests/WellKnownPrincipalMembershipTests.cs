using System.Runtime.CompilerServices;
using Conduit.Sync.Orchestration;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// SYNC-MEMBERS-01 (Jacob 10-22, Sync Projects "0 of 155 succeeded"): the lab's built-in groups hold
/// five edges to well-known-SID foreign security principals (S-1-5-11 Authenticated Users ×3, S-1-5-4
/// Interactive, S-1-5-9 Enterprise Domain Controllers). They are not accounts, IdentityCenter never holds
/// an object for them, so every run reported membersUnresolved=5, kept its checkpoint and ended Partial.
/// They are now left out of the push and counted in the run log; a trusted-domain account still is not.
/// </summary>
public class WellKnownPrincipalMembershipTests
{
    [Theory]
    [InlineData("CN=S-1-5-11,CN=ForeignSecurityPrincipals,DC=Domain,DC=Local")]
    [InlineData("CN=S-1-5-4,CN=ForeignSecurityPrincipals,DC=Domain,DC=Local")]
    [InlineData("CN=S-1-5-9,CN=ForeignSecurityPrincipals,DC=Domain,DC=Local")]
    [InlineData("cn=S-1-1-0,cn=foreignsecurityprincipals,dc=domain,dc=local")]
    public void A_well_known_SID_principal_is_left_out(string dn) =>
        Assert.True(SyncProjectOrchestrator.IsWellKnownForeignPrincipal(dn));

    [Theory]
    // A trusted-domain account: real, and must stay visible as unresolved.
    [InlineData("CN=S-1-5-21-1004336348-1177238915-682003330-1105,CN=ForeignSecurityPrincipals,DC=Domain,DC=Local")]
    [InlineData("CN=Amanda Walker,OU=Contractors,OU=Users,OU=Organization,DC=Domain,DC=Local")]
    [InlineData("CN=S-1-5-11,OU=Groups,DC=Domain,DC=Local")]
    [InlineData("CN=Authenticated Users,CN=ForeignSecurityPrincipals,DC=Domain,DC=Local")]
    public void Anything_else_is_still_pushed(string dn) =>
        Assert.False(SyncProjectOrchestrator.IsWellKnownForeignPrincipal(dn));

    [Fact]
    public void The_filter_runs_on_Active_Directory_sources_at_capture_and_the_count_is_logged()
    {
        var src = File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..",
            "src", "Conduit.Sync", "Orchestration", "SyncProjectOrchestrator.cs"))).Replace("\r\n", "\n");
        Assert.Contains("if (sourceIsActiveDirectory)\n                    wellKnownPrincipalEdges += memberIds.RemoveAll(IsWellKnownForeignPrincipal);", src);
        Assert.Contains("left out {wellKnownPrincipalEdges} edge(s) to well-known security principals", src);
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
