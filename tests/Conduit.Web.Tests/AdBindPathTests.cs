using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Conduit.Connectors.ActiveDirectory;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// One Active Directory bind (AdLdapBinder) for the sink, the source (reads and the connection test)
/// and the container browser. Until 2026-10-16 the source and the browser bound plain Negotiate on
/// whatever port they were given, so an AD connection on :636 tested and read as "The LDAP server is
/// unavailable" regardless of the DC, while the sink alone knew LDAPS, StartTLS and the certificate
/// policy. Jacob read the symptom as the server going away.
/// </summary>
public class AdBindPathTests
{
    [Theory]
    [InlineData("ActiveDirectorySink.cs")]
    [InlineData("ActiveDirectorySource.cs")]
    [InlineData("ActiveDirectoryContainerBrowser.cs")]
    public void Every_ad_path_binds_through_the_shared_binder(string file)
    {
        var src = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Connectors.ActiveDirectory", file)));
        Assert.Contains("AdLdapBinder.Bind(", src);
        Assert.DoesNotContain("new LdapConnection(", src);
    }

    [Fact]
    public void The_binder_is_the_only_place_a_connection_is_constructed()
    {
        var dir = RepoFile(Path.Combine("src", "Conduit.Connectors.ActiveDirectory"));
        var owners = Directory.EnumerateFiles(dir, "*.cs")
            .Where(f => File.ReadAllText(f).Contains("new LdapConnection("))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(new[] { "AdLdapBinder.cs" }, owners);
    }

    [Fact]
    public void The_stored_credential_carries_the_tls_policy_for_every_path()
    {
        var parsed = AdLdapBinder.ParseCredentialJson(
            "{\"Username\":\"DOMAIN\\\\svc\",\"Password\":\"x\",\"AllowUntrustedCertificate\":true,\"ExpectedServerCertificateThumbprint\":\" \"}");
        Assert.NotNull(parsed);
        Assert.Equal("DOMAIN\\svc", parsed!.Value.Username);
        Assert.True(parsed.Value.AllowUntrustedCertificate);
        Assert.Null(parsed.Value.ExpectedServerCertificateThumbprint);
        Assert.Null(AdLdapBinder.ParseCredentialJson("{\"Username\":\"\"}"));
        Assert.Null(AdLdapBinder.ParseCredentialJson("not json"));
    }

    [Theory]
    [InlineData("dc01.lab.test", "dc01.lab.test", 389)]
    [InlineData("dc01.lab.test:636", "dc01.lab.test", 636)]
    [InlineData("192.168.1.60:389", "192.168.1.60", 389)]
    public void Host_and_port_parse_the_same_way_everywhere(string domain, string host, int port)
    {
        Assert.Equal((host, port), AdLdapBinder.ParseHostPort(domain));
    }

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));
}
