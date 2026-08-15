using Xunit;

namespace Conduit.Web.Tests;

public class ActiveDirectoryClassFilterTests
{
    [Fact]
    public void Uncommon_ad_classes_are_filtered_to_the_requested_class_for_live_and_deleted_reads()
    {
        var source = File.ReadAllText(FindRepoFile(Path.Combine(
            "src", "Conduit.Connectors.ActiveDirectory", "ActiveDirectorySource.cs")));

        Assert.DoesNotContain("_          => \"(objectClass=*)\"", source);
        Assert.Contains("$\"(objectClass={EscapeLdapFilterValue(objectClass)})\"", source);
        Assert.Contains("$\"(&(isDeleted=TRUE)(objectClass={EscapeLdapFilterValue(objectClass)})(whenChanged>={generalized}))\"", source);
    }

    [Fact]
    public void Dynamic_ldap_class_names_are_filter_escaped()
    {
        var source = File.ReadAllText(FindRepoFile(Path.Combine(
            "src", "Conduit.Connectors.ActiveDirectory", "ActiveDirectorySource.cs")));

        Assert.Contains("private static string EscapeLdapFilterValue", source);
        Assert.Contains(".Replace(\"*\", \"\\\\2a\"", source);
        Assert.Contains(".Replace(\"(\", \"\\\\28\"", source);
        Assert.Contains(".Replace(\")\", \"\\\\29\"", source);
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }
}
