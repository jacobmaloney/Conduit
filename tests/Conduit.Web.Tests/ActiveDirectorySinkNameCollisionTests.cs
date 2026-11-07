using Conduit.Connectors.ActiveDirectory;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// NAME-COLLISION-01. Granite day 4 (2026-11-06): a second Maria Zamora (sAMAccountName mzamora) was created in the OU
/// that already held CN=Maria Zamora (mzamora1). AD answered "entry already exists" and the sink LINKED the new person
/// to the first one's account. An entry at the DN is this account only when it carries this sAMAccountName; anyone
/// else's is never linked, and the new account is created as "Name (sam)".
/// </summary>
public class ActiveDirectorySinkNameCollisionTests
{
    [Theory]
    [InlineData("mzamora", "mzamora", true)]
    [InlineData("MZamora", "mzamora", true)]
    [InlineData(" mzamora ", "mzamora", true)]
    [InlineData("mzamora1", "mzamora", false)]
    [InlineData("mchen1", "mchen2", false)]
    [InlineData(null, "mzamora", false)]
    [InlineData("", "mzamora", false)]
    public void Only_the_same_sAMAccountName_is_the_same_account(string? existing, string sam, bool same) =>
        Assert.Equal(same, ActiveDirectorySink.IsSameAccount(existing, sam));

    [Fact]
    public void A_taken_name_is_created_as_name_and_account()
    {
        Assert.Equal("Maria Zamora (mzamora)", ActiveDirectorySink.CollisionCn("Maria Zamora", "mzamora"));
    }

    [Fact]
    public void The_user_create_paths_link_only_their_own_account()
    {
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "src", "Conduit.Connectors.ActiveDirectory", "ActiveDirectorySink.cs"));
        // Both user add paths go through the collision check; neither links on a bare "entry already exists" any more.
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(source, @"TryAddUser\(conn, add(Request)?, sam!\)").Count);
        Assert.DoesNotContain("AD create hit an existing entry at {Dn} — linking (not creating).", source);
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Conduit.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
