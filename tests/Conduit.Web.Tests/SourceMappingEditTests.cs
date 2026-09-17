using Conduit.Mapping;
using Conduit.SourceBrowsing;
using Xunit;

namespace Conduit.Web.Tests;

public class SourceMappingEditTests
{
    [Fact] public void AdSchemaRejectsConflictingAliasesAndUnsupportedObjectClasses()
    {
        var schema = Conduit.Connectors.ActiveDirectory.ActiveDirectorySink.MappingSchema("User")!;
        Assert.Single(SourceMappingEdit.Validate([new("mail", "email")], schema));
        Assert.Equal("DuplicateTarget", Assert.Throws<SourceBrowseException>(() => SourceMappingEdit.Validate([new("mail", "email"), new("other", "mail")], schema)).Code);
        Assert.Null(Conduit.Connectors.ActiveDirectory.ActiveDirectorySink.MappingSchema("Computer"));
    }
    [Fact] public void PasswordAgeMetadataIsAvailableWithoutExposingPasswordValues()
    {
        Assert.False(SourceBrowseSample.IsSensitive("PasswordLastSet"));
        Assert.True(SourceBrowseSample.IsSensitive("Password")); Assert.True(SourceBrowseSample.IsSensitive("PasswordLastSetSecret"));
        Assert.Null(SourceMappingEdit.ValueError("133000000000000000", new("PasswordLastSet", DataType: "AD timestamp")));
        Assert.Null(SourceMappingEdit.ValueError("0", new("PasswordLastSet", DataType: "AD timestamp")));
        Assert.NotNull(SourceMappingEdit.ValueError("bad timestamp", new("PasswordLastSet", DataType: "AD timestamp")));
        Assert.Null(SourceMappingEdit.ValueError("yes", new("IsActive", DataType: "Boolean or yes/no")));
    }
    [Theory] [InlineData("Integer", "abc")] [InlineData("Guid", "not-a-guid")] [InlineData("Boolean", "yes")]
    [InlineData("DateTime", "tomorrow")]
    public void InvalidRawTypesAreRefused(string type, string value)
    {
        var schema = new SourceMappingSchema("Fixture", [new("target", DataType: type)], "");
        var row = Assert.Single(SourceMappingPreview.Render([new("source", "target")], _ => new(true, value), _ => new("target", value, true, true, false), schema));
        Assert.StartsWith("Invalid:", row.Outcome);
        Assert.Equal("InvalidDefault", Assert.Throws<SourceBrowseException>(() => SourceMappingEdit.Validate([new("source", "target", DefaultValue: value)], schema)).Code);
    }
    [Fact] public void RequiredValueAndLengthAreValidatedBeforeDisplayClipping()
    {
        Assert.Equal("A value is required.", SourceMappingEdit.ValueError(null, new("name", Required: true)));
        Assert.Null(SourceMappingEdit.ValueError(null, new("name")));
        var value = new string('a', 2000); var schema = new SourceMappingSchema("Fixture", [new("name", MaxLength: 1500)], "");
        var row = Assert.Single(SourceMappingPreview.Render([new("source", "name")], _ => new(true, value), _ => new("name", value, true, true, false), schema));
        Assert.StartsWith("Invalid:", row.Outcome); Assert.Contains("shortened", row.Output);
    }
    [Fact] public void AdditionalFieldsCannotImpersonateKnownTargetsOrReservedMetadata()
    {
        var schema = new SourceMappingSchema("Fixture", [new("Email")], "", "Extra");
        Assert.Single(SourceMappingEdit.Validate([new("name", "Extension1", TargetType: "Extra")], schema));
        foreach (var target in new[] { "email", "_system", "bad/name" })
            Assert.Equal("UnknownTarget", Assert.Throws<SourceBrowseException>(() => SourceMappingEdit.Validate([new("name", target, TargetType: "Extra")], schema)).Code);
    }
    [Fact] public void DuplicateIdsCannotReplaceDifferentRows()
    {
        var id = Guid.NewGuid(); var schema = new SourceMappingSchema("Fixture", [new("a"), new("b")], "");
        Assert.Equal("DuplicateMappingId", Assert.Throws<SourceBrowseException>(() => SourceMappingEdit.Validate([new("name", "a", Id: id), new("name", "b", Id: id)], schema)).Code);
    }
    [Theory] [InlineData("upper")] [InlineData("trim|lower|prefix:x")] [InlineData("replace:old:new")]
    [InlineData("substring:2:3")] [InlineData("const:")]
    public void SupportedAuthoringExpressionsMatchExistingDialect(string expression) => Assert.Null(AttributeTransformExpression.ValidationError(expression));
    [Theory] [InlineData("typo")] [InlineData("replace::x")] [InlineData("substring:-1")]
    [InlineData("substring:1:2:3")] [InlineData("upper:value")] [InlineData("prefix")]
    public void InvalidAuthoringExpressionsAreRefused(string expression) => Assert.NotNull(AttributeTransformExpression.ValidationError(expression));
    [Fact] public void StrictAuthoringDoesNotChangeLegacyRuntimePassthrough() => Assert.Equal("Ada", AttributeTransformExpression.Apply("typo", "Ada"));
}
