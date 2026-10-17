using System.DirectoryServices.Protocols;
using System.Text;
using Conduit.Readers.ActiveDirectory;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// The lab's first full domain sync (2026-10-16) landed 36,021 group member references and 19,746
/// objectGUID copies in IdentityCenter as the literal text "System.Byte[]": DirectoryAttribute stores
/// every received value as bytes and only its indexer decodes, so a multi-valued attribute enumerated
/// as a collection leaked the raw wire values, and every ToString() downstream named the type.
/// AdAttributeValues is the one conversion now, on the way out of the protocol and into text.
/// </summary>
public class AdAttributeValuesTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void A_multi_valued_text_attribute_received_as_bytes_decodes_every_value()
    {
        // The byte[][] constructor stores values exactly as the wire does.
        var member = new DirectoryAttribute("member", Utf8("CN=a,OU=x,DC=lab"), Utf8("CN=b,OU=x,DC=lab"));
        var value = AdAttributeValues.FromDirectoryAttribute(member);
        var values = Assert.IsType<object?[]>(value);
        Assert.Equal(new object?[] { "CN=a,OU=x,DC=lab", "CN=b,OU=x,DC=lab" }, values);
        Assert.All(values, v => Assert.IsType<string>(v));
    }

    [Fact]
    public void A_single_text_value_is_a_string_and_a_binary_value_stays_bytes()
    {
        Assert.Equal("jdoe", AdAttributeValues.FromDirectoryAttribute(new DirectoryAttribute("sAMAccountName", Utf8("jdoe"))));
        var guid = Guid.NewGuid();
        var raw = AdAttributeValues.FromDirectoryAttribute(new DirectoryAttribute("objectGUID", guid.ToByteArray()));
        Assert.Equal(guid.ToByteArray(), Assert.IsType<byte[]>(raw));
        Assert.Null(AdAttributeValues.FromDirectoryAttribute(new DirectoryAttribute("description")));
    }

    [Fact]
    public void Text_for_binary_values_is_the_shape_people_and_identitycenter_read()
    {
        var guid = Guid.NewGuid();
        Assert.Equal(guid.ToString(), AdAttributeValues.ToText("objectGUID", guid.ToByteArray()));
        var sid = new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1105");
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);
        Assert.Equal("S-1-5-21-1-2-3-1105", AdAttributeValues.ToText("objectSid", sidBytes));
        var descriptor = new byte[] { 1, 0, 4, 128, 20, 0 };
        Assert.Equal(Convert.ToBase64String(descriptor), AdAttributeValues.ToText("ntSecurityDescriptor", descriptor));
        Assert.Equal("CN=a;CN=b", AdAttributeValues.ToText("member", new object?[] { "CN=a", Utf8("CN=b") }));
        Assert.DoesNotContain("System.Byte", AdAttributeValues.ToText("member", new object?[] { Utf8("x"), Utf8("y") }));
    }

    [Fact]
    public void A_connector_object_value_is_text_or_a_list_of_text_never_bytes()
    {
        var list = Assert.IsType<List<string>>(AdAttributeValues.ToTextValue("member", new object?[] { Utf8("CN=a"), "CN=b" }));
        Assert.Equal(new[] { "CN=a", "CN=b" }, list);
        Assert.Equal("4d3f2e1d-0000-4000-8000-000000000000", AdAttributeValues.ToTextValue("objectGUID", Guid.Parse("4d3f2e1d-0000-4000-8000-000000000000").ToByteArray()));
        Assert.Null(AdAttributeValues.ToTextValue("x", null));
    }

    [Fact]
    public void The_reader_and_the_source_use_the_one_conversion()
    {
        var session = File.ReadAllText(Path.Combine(Repo(), "src", "Conduit.Readers.ActiveDirectory", "LdapSession.cs"));
        Assert.Contains("AdAttributeValues.FromDirectoryAttribute(entry.Attributes[name])", session);
        Assert.DoesNotContain("attribute.Cast<object?>().ToArray()", session);
        var source = File.ReadAllText(Path.Combine(Repo(), "src", "Conduit.Connectors.ActiveDirectory", "ActiveDirectorySource.cs"));
        Assert.Contains("attrs[key] = AdAttributeValues.ToTextValue(key, attrs[key]);", source);
        Assert.DoesNotContain("GetValues(typeof(object))", source);
        Assert.DoesNotContain("private static object? ConvertAttributeValue", source);
    }

    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
