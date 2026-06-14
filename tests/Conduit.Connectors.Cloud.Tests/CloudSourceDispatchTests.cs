using Conduit.Connectors.Aws;
using Conduit.Connectors.GoogleWorkspace;
using Xunit;

namespace Conduit.Connectors.Cloud.Tests;

/// <summary>
/// Guards the governance-class dispatch sets for the AWS, AWS Identity Center, and
/// Google Workspace sources. These do NOT require live cloud credentials — they
/// assert the static supported-class seam each source exposes (the same set its
/// ReadAsync dispatch routes by, throwing NotSupportedException for anything else).
/// </summary>
public class CloudSourceDispatchTests
{
    [Theory]
    [InlineData("user")]
    [InlineData("group")]
    [InlineData("role")]
    [InlineData("policy")]
    [InlineData("account")]
    public void AwsSource_supports_each_governance_class_case_insensitively(string cls)
    {
        Assert.True(AwsSource.IsSupportedClass(cls));
        Assert.True(AwsSource.IsSupportedClass(cls.ToUpperInvariant()));
        Assert.True(AwsSource.IsSupportedClass(cls.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("computer")]
    [InlineData("permissionSet")]
    [InlineData("organizationalUnit")]
    [InlineData("")]
    public void AwsSource_rejects_unknown_classes(string cls)
    {
        Assert.False(AwsSource.IsSupportedClass(cls));
    }

    [Fact]
    public void AwsSource_supported_set_is_exactly_the_five_governance_classes()
    {
        Assert.Equal(new[] { "user", "group", "role", "policy", "account" }, AwsSource.SupportedClasses);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("group")]
    [InlineData("permissionSet")]
    public void AwsSsoSource_supports_each_governance_class_case_insensitively(string cls)
    {
        Assert.True(AwsSsoSource.IsSupportedClass(cls));
        Assert.True(AwsSsoSource.IsSupportedClass(cls.ToUpperInvariant()));
        Assert.True(AwsSsoSource.IsSupportedClass(cls.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("role")]
    [InlineData("policy")]
    [InlineData("account")]
    public void AwsSsoSource_rejects_classes_it_does_not_emit(string cls)
    {
        Assert.False(AwsSsoSource.IsSupportedClass(cls));
    }

    [Fact]
    public void AwsSsoSource_supported_set_is_exactly_user_group_permissionSet()
    {
        Assert.Equal(new[] { "user", "group", "permissionSet" }, AwsSsoSource.SupportedClasses);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("group")]
    [InlineData("organizationalUnit")]
    [InlineData("role")]
    [InlineData("domain")]
    public void GoogleWorkspaceSource_supports_each_governance_class_case_insensitively(string cls)
    {
        Assert.True(GoogleWorkspaceSource.IsSupportedClass(cls));
        Assert.True(GoogleWorkspaceSource.IsSupportedClass(cls.ToUpperInvariant()));
        Assert.True(GoogleWorkspaceSource.IsSupportedClass(cls.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("permissionSet")]
    [InlineData("computer")]
    public void GoogleWorkspaceSource_rejects_unknown_classes(string cls)
    {
        Assert.False(GoogleWorkspaceSource.IsSupportedClass(cls));
    }

    [Fact]
    public void GoogleWorkspaceSource_supported_set_is_exactly_the_five_governance_classes()
    {
        Assert.Equal(
            new[] { "user", "group", "organizationalUnit", "role", "domain" },
            GoogleWorkspaceSource.SupportedClasses);
    }
}
