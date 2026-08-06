using Conduit.Connectors.Okta;
using Xunit;

namespace Conduit.Connectors.Cloud.Tests;

/// <summary>
/// Guards the Okta dispatch set. Application used to be advertised by the catalog
/// and generator but silently fall through to the /users endpoint — this seam is
/// what keeps that from regressing.
/// </summary>
public class OktaSourceDispatchTests
{
    [Theory]
    [InlineData("user")]
    [InlineData("group")]
    [InlineData("application")]
    public void OktaSource_supports_each_class_case_insensitively(string cls)
    {
        Assert.True(OktaSource.IsSupportedClass(cls));
        Assert.True(OktaSource.IsSupportedClass(cls.ToUpperInvariant()));
        Assert.True(OktaSource.IsSupportedClass(cls.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("computer")]
    [InlineData("organizationalUnit")]
    [InlineData("policy")]
    [InlineData("")]
    public void OktaSource_rejects_unknown_classes(string cls)
    {
        Assert.False(OktaSource.IsSupportedClass(cls));
    }

    [Fact]
    public void OktaSource_supported_set_is_exactly_user_group_application()
    {
        Assert.Equal(new[] { "user", "group", "application" }, OktaSource.SupportedClasses);
    }
}
