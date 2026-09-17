using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Sync.Orchestration;
using Xunit;

namespace Conduit.Connectors.IdentityCenter.Tests;

public class ConnectorMappingPlanTests
{
    [Theory]
    [InlineData(false, null, null, false, false, null)]
    [InlineData(true, null, null, false, true, null)]
    [InlineData(true, null, "upper", false, false, null)]
    [InlineData(true, null, "upper", true, true, null)]
    [InlineData(false, null, null, true, true, null)]
    [InlineData(false, null, "default:guest | upper", false, true, "GUEST")]
    [InlineData(false, null, "const:fixed", false, true, "fixed")]
    [InlineData(false, null, "prefix:account_", false, true, "account_")]
    [InlineData(true, "", null, false, true, "")]
    [InlineData(true, "", "default:guest", false, true, "guest")]
    [InlineData(true, " ", "default:guest", false, true, " ")]
    [InlineData(true, "Ada", "unknown", false, true, "Ada")]
    public void SavedDefinitionsKeepTheirPresenceAndDefaultResults(bool present, string? value, string? expression,
        bool required, bool expectedPresent, string? expectedValue)
    {
        var source = new ConnectorObject { SourceId = "account-1", ObjectClass = "user" };
        if (present) source.Attributes["input"] = value;
        var plan = new ConnectorMappingPlan([new() { SourceAttribute = "input", SinkAttribute = "output", TransformExpr = expression, IsRequired = required }]);
        var result = plan.Apply(source);
        Assert.Equal(expectedPresent, result.Attributes.ContainsKey("output"));
        if (expectedPresent) Assert.Equal(expectedValue, result.Attributes["output"]);
        Assert.Equal("account-1", result.SourceId); Assert.Equal("user", result.ObjectClass);
        Assert.NotSame(source, result); Assert.NotSame(source.Attributes, result.Attributes);
        Assert.Equal(present ? 1 : 0, source.Attributes.Count);
        if (present) Assert.Equal(value, source.Attributes["input"]);
    }

    [Fact]
    public void StructuralFieldsSurviveWithoutOverwritingExplicitNulls()
    {
        var source = new ConnectorObject { Attributes = new() {
            ["displayName"] = "Ada", ["userName"] = "ada", ["UserName"] = "ADA", ["sAMAccountName"] = "a",
            ["distinguishedName"] = "CN=Ada", ["DN"] = "CN=other", ["unmapped"] = "omit" } };
        var plan = new ConnectorMappingPlan([new() { SourceAttribute = "displayName", SinkAttribute = "name" }]);
        var result = plan.Apply(source);
        Assert.Equal("CN=Ada", result.Attributes["DN"]);
        Assert.Equal("ada", result.Attributes["userName"]); Assert.Equal("ADA", result.Attributes["UserName"]);
        Assert.Equal("a", result.Attributes["sAMAccountName"]); Assert.False(result.Attributes.ContainsKey("unmapped"));
        var overrides = new ConnectorMappingPlan([
            new() { SourceAttribute = "missing", SinkAttribute = "DN", IsRequired = true },
            new() { SourceAttribute = "missing", SinkAttribute = "userName", IsRequired = true }]).Apply(source);
        Assert.Null(overrides.Attributes["DN"]); Assert.Null(overrides.Attributes["userName"]);
        Assert.Equal("CN=Ada", source.Attributes["distinguishedName"]);
    }

    [Fact]
    public void DuplicateTargetsKeepSuppliedOrderAndOmittedFieldsDoNotOverwrite()
    {
        var source = new ConnectorObject { Attributes = new() { ["a"] = "first", ["b"] = "last" } };
        AttributeMapping[] definitions = [new() { SourceAttribute = "a", SinkAttribute = "target", SortOrder = 20 },
            new() { SourceAttribute = "b", SinkAttribute = "target", SortOrder = 10 },
            new() { SourceAttribute = "missing", SinkAttribute = "target" }];
        Assert.Equal("last", new ConnectorMappingPlan(definitions).Apply(source).Attributes["target"]);
        definitions[2].IsRequired = true;
        Assert.Null(new ConnectorMappingPlan(definitions).Apply(source).Attributes["target"]);
    }

    [Fact]
    public void PlanSnapshotsDefinitionsButReadsEachObjectsValues()
    {
        var definition = new AttributeMapping { SourceAttribute = "a", SinkAttribute = "target", TransformExpr = "upper" };
        var definitions = new List<AttributeMapping> { definition };
        var plan = new ConnectorMappingPlan(definitions);
        definition.SourceAttribute = "b"; definition.SinkAttribute = "changed"; definition.TransformExpr = "lower"; definitions.Clear();
        Assert.Equal("ONE", plan.Apply(new() { Attributes = new() { ["a"] = "one" } }).Attributes["target"]);
        Assert.Equal("TWO", plan.Apply(new() { Attributes = new() { ["a"] = "two" } }).Attributes["target"]);
    }

    [Fact]
    public void PassthroughComparerAndArraySemanticsArePreserved()
    {
        var values = new[] { "Ada", "Grace" };
        var source = new ConnectorObject { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["MAIL"] = values } };
        Assert.Same(source, new ConnectorMappingPlan([]).Apply(source));
        var result = new ConnectorMappingPlan([
            new() { SourceAttribute = "mail", SinkAttribute = "raw" },
            new() { SourceAttribute = "mail", SinkAttribute = "first", TransformExpr = "lower" }]).Apply(source);
        Assert.Same(values, result.Attributes["raw"]); Assert.Equal("ada", result.Attributes["first"]);
        Assert.Equal("Ada", values[0]);
        var sensitive = new ConnectorObject { Attributes = new() { ["MAIL"] = "Ada" } };
        Assert.Empty(new ConnectorMappingPlan([new() { SourceAttribute = "mail", SinkAttribute = "raw" }]).Apply(sensitive).Attributes);
    }

    [Fact]
    public void FailedMappingDoesNotReturnPartialOutputOrMutateSource()
    {
        var source = new ConnectorObject { Attributes = new() { ["name"] = "Ada" } };
        var plan = new ConnectorMappingPlan([
            new() { SourceAttribute = "name", SinkAttribute = "first" },
            new() { SourceAttribute = "name", SinkAttribute = "second", TransformExpr = "replace::bad" }]);
        Assert.Equal("oldValue", Assert.Throws<ArgumentException>(() => plan.Apply(source)).ParamName);
        Assert.Equal("Ada", Assert.Single(source.Attributes).Value);
        Assert.Equal("Ada", new ConnectorMappingPlan([new() { SourceAttribute = "name", SinkAttribute = "valid" }]).Apply(source).Attributes["valid"]);
    }
}
