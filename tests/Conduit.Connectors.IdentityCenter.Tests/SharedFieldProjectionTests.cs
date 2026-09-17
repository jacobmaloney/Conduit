using Conduit.Mapping;
using Xunit;

namespace Conduit.Connectors.IdentityCenter.Tests;

public class SharedFieldProjectionTests
{
    [Fact]
    public void LookupPreservesExactLowercaseFallbackAndPresentNull()
    {
        var source = new Dictionary<string, object?> { ["MAIL"] = "upper", ["mail"] = "lower", ["Mail"] = null };
        Assert.Equal(new SelectedAttribute(true, null), SourceAttributes.Read(source, "Mail", SourceLookupMode.ExactThenLowerThenIgnoreCase));
        Assert.Equal(new SelectedAttribute(true, "lower"), SourceAttributes.Read(source, "mAiL", SourceLookupMode.ExactThenLowerThenIgnoreCase));
        Assert.Equal(new SelectedAttribute(false, null), SourceAttributes.Read(source, "mAiL"));
        Assert.Equal(new SelectedAttribute(true, "upper"), SourceAttributes.Read(
            new Dictionary<string, object?> { ["MAIL"] = "upper" }, "mail", SourceLookupMode.ExactThenLowerThenIgnoreCase));
        Assert.Equal(new SelectedAttribute(true, null), SourceAttributes.Read(
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["MAIL"] = null }, "mail"));
    }

    [Theory]
    [InlineData(DefaultWhen.Null, null, true)]
    [InlineData(DefaultWhen.Null, "", false)]
    [InlineData(DefaultWhen.Null, " ", false)]
    [InlineData(DefaultWhen.NullOrEmptyString, null, true)]
    [InlineData(DefaultWhen.NullOrEmptyString, "", true)]
    [InlineData(DefaultWhen.NullOrEmptyString, " ", false)]
    [InlineData(DefaultWhen.NullOrWhiteSpaceString, " ", true)]
    [InlineData(DefaultWhen.NullOrWhiteSpaceString, "0", false)]
    [InlineData(DefaultWhen.NullOrWhiteSpaceString, 0, false)]
    public void DefaultConditionsDoNotTreatZeroOrWhitespaceAsMissingUnlessConfigured(DefaultWhen when, object? value, bool expected)
    {
        Assert.Equal(expected, AttributeDefaults.NeedsDefault(value, when));
        Assert.Equal(expected ? "fallback" : value, AttributeDefaults.Apply(value, "fallback", when));
    }

    [Theory]
    [InlineData(false, null, OutputPresence.SourcePresent, false)]
    [InlineData(true, null, OutputPresence.SourcePresent, true)]
    [InlineData(true, null, OutputPresence.NonNullValue, false)]
    [InlineData(false, null, OutputPresence.Always, true)]
    [InlineData(true, "", OutputPresence.NonNullValue, true)]
    [InlineData(true, "value", OutputPresence.SourcePresent, true)]
    public void EmissionDistinguishesOmittedNullAndEmpty(bool present, string? value, OutputPresence presence, bool expected)
    {
        var source = new Dictionary<string, object?>();
        if (present) source["input"] = value;
        var result = FieldProjection.Project(source, new("input", "output") { Presence = presence });
        Assert.Equal(expected, result.ShouldWrite);
        Assert.Equal(present, result.SourceFound);
        Assert.False(result.UsedDefault);
        Assert.Equal(value, result.Value);
        Assert.Equal("output", result.TargetAttribute);
        Assert.Equal(present ? 1 : 0, source.Count);
    }

    [Theory]
    [InlineData(false, "Fallback", 0)]
    [InlineData(true, "FALLBACK", 1)]
    public void DefaultTransformationOrderingIsExplicit(bool transformDefault, string expected, int calls)
    {
        var count = 0;
        var rule = new FieldProjectionRule("missing", "target") {
            Default = new("Fallback", Transform: transformDefault),
            Transform = value => { count++; return value?.ToString()?.ToUpperInvariant(); } };
        var result = FieldProjection.Project(new Dictionary<string, object?>(), rule);
        Assert.Equal(expected, result.Value);
        Assert.True(result.UsedDefault); Assert.True(result.ShouldWrite); Assert.False(result.SourceFound);
        Assert.Equal(calls, count);
    }

    [Fact]
    public void TransformationCanProduceOrRemovePresenceWithoutChangingSource()
    {
        var source = new Dictionary<string, object?> { ["input"] = "original" };
        var rule = new FieldProjectionRule("input", "target") { Transform = _ => null, Presence = OutputPresence.NonNullValue };
        Assert.False(FieldProjection.Project(source, rule).ShouldWrite);
        var generated = FieldProjection.Project(source, rule with { SourceAttribute = "missing", Transform = _ => "generated" });
        Assert.True(generated.ShouldWrite); Assert.Equal("generated", generated.Value);
        Assert.Equal("original", Assert.Single(source).Value);
    }

    [Fact]
    public void FailedTransformationPropagatesAndCannotBecomeAProjection()
    {
        var source = new Dictionary<string, object?> { ["input"] = "original" };
        var error = new InvalidOperationException("bad mapping");
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => FieldProjection.Project(source,
            new("input", "target") { Transform = _ => throw error })));
        Assert.Equal("original", Assert.Single(source).Value);
        Assert.Equal("original", FieldProjection.Project(source, new("input", "target")).Value);
    }

    [Fact]
    public void InvalidPoliciesRefuseBeforeCallingTransformation()
    {
        var calls = 0;
        var source = new Dictionary<string, object?>();
        var rule = new FieldProjectionRule("input", "target") { Transform = value => { calls++; return value; } };
        Assert.Contains("output presence", Assert.Throws<ArgumentOutOfRangeException>(() =>
            FieldProjection.Project(source, rule with { Presence = (OutputPresence)999 })).Message);
        Assert.Contains("source lookup", Assert.Throws<ArgumentOutOfRangeException>(() =>
            FieldProjection.Project(source, rule with { Lookup = (SourceLookupMode)999 })).Message);
        Assert.Contains("default condition", Assert.Throws<ArgumentOutOfRangeException>(() =>
            FieldProjection.Project(source, rule with { Default = new(null, (DefaultWhen)999) })).Message);
        Assert.Equal(0, calls); Assert.Empty(source);
        Assert.False(FieldProjection.Project(source, rule).ShouldWrite);
        Assert.Equal(1, calls);
    }
}
