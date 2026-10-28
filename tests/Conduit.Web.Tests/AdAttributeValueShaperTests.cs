using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Conduit.Web.Services;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// ALLOW-LIST-02 (IdentityCenter UC015-D06, UC017-F03/F06). The widened allow-list and the ONE value
/// shaper that turns the date IdentityCenter sends into the FILETIME AD stores for accountExpires, on
/// both executors. No LDAP in a unit test: the shaper is pure, the create executor's BuildCreateObject
/// is pure, and the allow-list membership is pinned by source.
/// </summary>
public class AdAttributeValueShaperTests
{
    // ── the shaper ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("never")]
    [InlineData("NEVER")]
    public void Nothing_zero_and_never_all_mean_never_expires(string? value)
    {
        Assert.True(AdAttributeValueShaper.TryShapeAccountExpires(value, out var shaped, out var error));
        Assert.Equal("0", shaped);
        Assert.Null(error);
    }

    [Fact]
    public void A_filetime_passes_through_untouched()
    {
        Assert.True(AdAttributeValueShaper.TryShapeAccountExpires("133800000000000000", out var shaped, out _));
        Assert.Equal("133800000000000000", shaped);
    }

    [Fact]
    public void A_date_becomes_the_end_of_that_day_in_local_time()
    {
        Assert.True(AdAttributeValueShaper.TryShapeAccountExpires("2026-12-31", out var shaped, out _));
        var expected = DateTime.SpecifyKind(new DateTime(2027, 1, 1, 0, 0, 0), DateTimeKind.Local).ToFileTime();
        Assert.Equal(expected.ToString(CultureInfo.InvariantCulture), shaped);
        Assert.Equal(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Local), DateTime.FromFileTime(long.Parse(shaped!, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void A_date_time_becomes_that_instant()
    {
        Assert.True(AdAttributeValueShaper.TryShapeAccountExpires("2026-12-31T17:00:00Z", out var shaped, out _));
        Assert.Equal(new DateTime(2026, 12, 31, 17, 0, 0, DateTimeKind.Utc).ToFileTimeUtc().ToString(CultureInfo.InvariantCulture), shaped);

        Assert.True(AdAttributeValueShaper.TryShapeAccountExpires("2026-12-31T17:00:00-08:00", out var withOffset, out _));
        Assert.Equal(new DateTime(2027, 1, 1, 1, 0, 0, DateTimeKind.Utc).ToFileTimeUtc().ToString(CultureInfo.InvariantCulture), withOffset);
    }

    [Theory]
    [InlineData("next week")]
    [InlineData("31/12/2026")]
    [InlineData("1500-01-01T00:00:00Z")]
    [InlineData("99999999999999999999999")]
    public void Anything_else_is_refused_with_a_reason(string value)
    {
        Assert.False(AdAttributeValueShaper.TryShapeAccountExpires(value, out var shaped, out var error));
        Assert.Null(shaped);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Every_other_attribute_is_returned_as_sent()
    {
        Assert.True(AdAttributeValueShaper.TryShape("extensionAttribute8", "L", out var shaped, out _));
        Assert.Equal("L", shaped);
        Assert.True(AdAttributeValueShaper.TryShape("department", null, out var cleared, out _));
        Assert.Null(cleared);
    }

    // ── the create executor shapes the bag ───────────────────────────────────

    [Fact]
    public void BuildCreateObject_carries_accountExpires_as_a_filetime()
    {
        var payload = JsonSerializer.Deserialize<AdAgentCreateExecutor.CreateAdAccountPayload>(
            "{\"schemaVersion\":1,\"operation\":\"CreateUser\",\"sourceConnectionName\":\"corp-local\",\"sAMAccountName\":\"nora.quint\"," +
            "\"targetOu\":\"OU=Staff,DC=corp,DC=local\",\"attributes\":{\"givenName\":\"Nora\",\"accountExpires\":\"2026-12-31\",\"extensionAttribute8\":\"A\"}}")!;

        var obj = AdAgentCreateExecutor.BuildCreateObject(payload, "nora.quint", "OU=Staff,DC=corp,DC=local");

        var stored = Assert.IsType<string>(obj.Attributes["accountExpires"]);
        Assert.True(stored.All(char.IsAsciiDigit), "accountExpires is stored as a FILETIME, not the date that was sent");
        Assert.Equal("A", obj.Attributes["extensionAttribute8"]);
    }

    // ── the allow-list, pinned by source ─────────────────────────────────────

    private static readonly string Executor = Read("src/Conduit.Web/Services/AdAgentWriteExecutor.cs");
    private static readonly string Creator = Read("src/Conduit.Web/Services/AdAgentCreateExecutor.cs");

    [Theory]
    [InlineData("employeeID")]
    [InlineData("employeeType")]
    [InlineData("employeeNumber")]
    [InlineData("accountExpires")]
    [InlineData("homeDirectory")]
    [InlineData("homeDrive")]
    [InlineData("extensionAttribute1")]
    [InlineData("extensionAttribute8")]
    [InlineData("extensionAttribute15")]
    public void The_update_allow_list_names_each_widened_attribute(string name) =>
        Assert.Contains($"[\"{name}\"] = \"{name}\",", AllowListBlock());

    [Theory]
    [InlineData("userAccountControl")]
    [InlineData("unicodePwd")]
    [InlineData("sAMAccountName")]
    [InlineData("extensionAttribute16")]
    public void The_update_allow_list_still_refuses_what_it_should(string name) =>
        Assert.DoesNotContain($"[\"{name}\"]", AllowListBlock());

    [Fact]
    public void SetAttributes_shapes_every_value_before_its_first_write()
    {
        var method = Executor[Executor.IndexOf("private async Task<(bool, string)> DoSetAttributesAsync(", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("// ── SetManager", StringComparison.Ordinal)];
        var shape = method.IndexOf("AdAttributeValueShaper.TryShape(", StringComparison.Ordinal);
        var firstWrite = method.IndexOf("await sink.SetAttributeByGuidAsync(", StringComparison.Ordinal);
        Assert.True(shape > 0 && firstWrite > shape, "values are shaped (and refused) before the first write");
    }

    [Fact]
    public void The_create_executor_no_longer_forbids_accountExpires_and_shapes_it_before_containment()
    {
        var forbidden = Creator[Creator.IndexOf("ForbiddenAttributeKeys = new", StringComparison.Ordinal)..];
        forbidden = forbidden[..forbidden.IndexOf("};", StringComparison.Ordinal)];
        Assert.DoesNotContain("\"accountExpires\"", forbidden);
        Assert.Contains("\"userAccountControl\"", forbidden);

        var shape = Creator.IndexOf("AdAttributeValueShaper.TryShape(", StringComparison.Ordinal);
        var containment = Creator.IndexOf("BaseDnContainment.IsContained(", StringComparison.Ordinal);
        Assert.True(shape > 0 && containment > shape, "an unshapeable value fails before containment and before any LDAP call");
    }

    private static string AllowListBlock()
    {
        var start = Executor.IndexOf("AllowedAttributes = new", StringComparison.Ordinal);
        var end = Executor.IndexOf("PrivilegedRids", start, StringComparison.Ordinal);
        return Executor[start..end];
    }

    private static string Read(string relative, [CallerFilePath] string thisFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
        return File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    }
}
