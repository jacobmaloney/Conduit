using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// An LDAP class name can arrive from configuration, so it must never be concatenated into a
/// filter unescaped, and an uncommon class must never fall through to <c>(objectClass=*)</c> —
/// that would quietly return the whole directory instead of the requested class.
///
/// <para>These pins broke on 2026-09-22, the first time CI ever ran the suite, and the reason is
/// worth keeping: the protection had not been removed, it had been MOVED. The inline
/// <c>EscapeLdapFilterValue</c> these tests named became <see cref="AdQuery"/>.Escape in
/// Conduit.Readers.ActiveDirectory, and the per-class filter became AdQuery.FilterForClass. The
/// new escape is stronger — it also handles backslash and NUL, and escapes the backslash FIRST,
/// which matters because doing it last would re-escape the sequences it had just inserted.</para>
///
/// <para>So they now pin the behaviour at its real address, and each assertion says what it is
/// defending. A refactor should move these with the code rather than delete them.</para>
/// </summary>
public class ActiveDirectoryClassFilterTests
{
    private const string SourceFile = "ActiveDirectorySource.cs";
    private const string QueryFile = "AdQuery.cs";

    [Fact]
    public void Uncommon_ad_classes_are_filtered_to_the_requested_class_for_live_and_deleted_reads()
    {
        var source = File.ReadAllText(FindRepoFile(Path.Combine(
            "src", "Conduit.Connectors.ActiveDirectory", SourceFile)));

        // The fallthrough this exists to prevent: an unrecognised class returning the directory.
        Assert.DoesNotContain("_          => \"(objectClass=*)\"", source);
        Assert.DoesNotContain("_ => \"(objectClass=*)\"", source);

        // Deleted (tombstone) reads: the default arm narrows to the requested class, escaped.
        Assert.Contains("(objectClass={AdQuery.Escape(objectClass)})", source);

        // Live reads go through the shared per-class filter rather than building their own.
        var query = File.ReadAllText(FindRepoFile(Path.Combine(
            "src", "Conduit.Readers.ActiveDirectory", QueryFile)));
        Assert.Contains("FilterForClass", source);
        Assert.Contains("_ => $\"(objectClass={Escape(objectClass)})\"", query);
    }

    [Fact]
    public void Dynamic_ldap_class_names_are_filter_escaped()
    {
        var query = File.ReadAllText(FindRepoFile(Path.Combine(
            "src", "Conduit.Readers.ActiveDirectory", QueryFile)));

        Assert.Contains("static string Escape", query);

        // RFC 4515 §3. Backslash FIRST: escaping it last would re-escape the \2a, \28 and \29
        // this very expression had just introduced.
        var escape = query[query.IndexOf("static string Escape", StringComparison.Ordinal)..];
        escape = escape[..escape.IndexOf('\n')];
        Assert.True(
            escape.IndexOf("\"\\\\\"", StringComparison.Ordinal) <
            escape.IndexOf("\"*\"", StringComparison.Ordinal),
            "backslash must be escaped before the sequences that contain one: " + escape);

        Assert.Contains(".Replace(\"\\\\\", \"\\\\5c\")", query);
        Assert.Contains(".Replace(\"*\", \"\\\\2a\")", query);
        Assert.Contains(".Replace(\"(\", \"\\\\28\")", query);
        Assert.Contains(".Replace(\")\", \"\\\\29\")", query);
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
