using System.Collections;
using System.Globalization;
using Conduit.Mapping;
using Conduit.Sync.Orchestration;
using Xunit;

namespace Conduit.Connectors.IdentityCenter.Tests;

public class SharedMappingTests
{
    [Theory]
    [InlineData("trim | lower | prefix:user_", "  ADA  ", "user_ada")]
    [InlineData("UPPER", "ada", "ADA")]
    [InlineData("unknown | trim", " ada ", "ada")]
    [InlineData("replace:a:A | suffix::end", "ada", "AdA:end")]
    [InlineData("prefix:urn:test:", "42", "urn:test:42")]
    [InlineData("default:guest | upper", null, "GUEST")]
    [InlineData("default:guest", "", "guest")]
    [InlineData("default:guest", " ", " ")]
    [InlineData("const:fixed", "ignored", "fixed")]
    [InlineData("const:", null, "")]
    [InlineData("trim", null, null)]
    [InlineData("prefix:x", null, "x")]
    [InlineData("suffix:x", null, "x")]
    [InlineData("substring:2", "abcdef", "cdef")]
    [InlineData("substring:2:2", "abcdef", "cd")]
    [InlineData("substring:2:999", "abcdef", "cdef")]
    [InlineData("substring:-1", "abcdef", "")]
    [InlineData("substring:8", "abcdef", "")]
    [InlineData("substring:2:0", "abcdef", "")]
    [InlineData("substring:bad", "abcdef", "abcdef")]
    [InlineData("substring:2:bad", "abcdef", "cdef")]
    [InlineData("replace:bad", "abcdef", "abcdef")]
    [InlineData(" || trim || ", " ada ", "ada")]
    public void SavedConduitExpressionsKeepTheirResults(string expression, string? input, string? expected)
    {
        Assert.Equal(expected, AttributeTransformer.Apply(expression, input));
        Assert.Equal(expected, AttributeTransformExpression.Apply(expression, input));
    }

    [Theory]
    [InlineData("const:", true)]
    [InlineData("default:x", true)]
    [InlineData("trim | PREFIX:x", true)]
    [InlineData("suffix:x", true)]
    [InlineData("upper | trim", false)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    public void MissingSourceWriteEligibilityIsPreserved(string expression, bool expected)
    {
        Assert.Equal(expected, AttributeTransformer.ProducesValueWhenSourceMissing(expression));
        Assert.Equal(expected, AttributeTransformExpression.ProducesValueWhenSourceMissing(expression));
    }

    [Fact]
    public void CoercionAndUnknownOperationsKeepTheirExistingContract()
    {
        var original = new ArrayList { " ada ", "ignored" };
        Assert.Same(original, AttributeTransformer.Apply("unknown", original));
        Assert.Same(original, AttributeTransformer.Apply(" ", original));
        Assert.Equal("ADA", AttributeTransformer.Apply("trim | upper", original));
        Assert.Equal("123", AttributeTransformer.Apply("trim", 123));
        Assert.Null(AttributeTransformer.Apply("upper", new ArrayList { null, "ignored" }));
        Assert.Throws<ArgumentException>(() => AttributeTransformer.Apply("replace::x", "ada"));
    }

    [Fact]
    public void ConduitCasingRemainsInvariantWhenHostCultureIsTurkish()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal("I", AttributeTransformer.Apply("upper", "i"));
            Assert.Equal("i", AttributeTransformer.Apply("lower", "I"));
            Assert.Equal("\u0130", TextTransformation.Apply("i", TextOperation.Upper, CultureInfo.CurrentCulture));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void SharedOperationsAreLoadedFromTheIndependentLibrary()
    {
        var shared = typeof(TextTransformation).Assembly;
        Assert.Equal("Conduit.Mapping", shared.GetName().Name);
        Assert.Equal(shared, typeof(AttributeTransformExpression).Assembly);
        Assert.Contains(typeof(AttributeTransformer).Assembly.GetReferencedAssemblies(), a => a.Name == shared.GetName().Name);
        Assert.DoesNotContain(shared.GetReferencedAssemblies(), a =>
            a.Name!.StartsWith("Conduit.") || a.Name == "DataAccessLibrary" || a.Name == "Dapper" || a.Name.Contains("SqlClient"));
    }
}
