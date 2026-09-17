using Conduit.SourceBrowsing;
using Conduit.Web.Services;
using Xunit;

namespace Conduit.Web.Tests;

public class SourceMappingPreviewTests
{
    [Fact] public void PreviewUsesRawValueBeforeDisplayClippingAndTheRuntimeExpressionChain()
    {
        var source = new Dictionary<string, object?> { ["name"] = new string('x', 1500) + " ADA " };
        var result = Assert.Single(SourceMappingPreviewAdapter.Project(source, [new("name", "DisplayName", "substring:1500|trim|lower")]));
        Assert.Equal("ada", result.Output); Assert.Contains("shortened", result.Input); Assert.Equal("Projected", result.Outcome);
        Assert.Equal(1505, ((string)source["name"]!).Length);
    }
    [Fact] public void AbsentNullEmptyAndRequiredValuesPreserveRuntimePresence()
    {
        var source = new Dictionary<string, object?> { ["null"] = null, ["empty"] = "" };
        var result = SourceMappingPreviewAdapter.Project(source, [new("missing", "omit"), new("missing", "required", Required: true), new("null", "null"), new("empty", "empty"), new("missing", "fallback", "default:Fallback|upper")]);
        Assert.Equal("Omitted", result[0].Outcome); Assert.Equal("(missing)", result[0].Input);
        Assert.Equal("Null output", result[1].Outcome); Assert.Equal("Null output", result[2].Outcome);
        Assert.Equal("(empty)", result[3].Output); Assert.Equal("FALLBACK", result[4].Output);
    }
    [Fact] public void MultiValueTransformationUsesRuntimeFirstValue()
    {
        var row = Assert.Single(SourceMappingPreviewAdapter.Project(new Dictionary<string, object?> { ["aliases"] = new List<object?> { "first", "second" } }, [new("aliases", "alias", "upper")]));
        Assert.Equal("FIRST", row.Output); Assert.Equal("first; second", row.Input);
    }
    [Theory] [InlineData("password", "label")] [InlineData("name", "client-secret")] [InlineData("msLAPS-Password", "value")]
    public void SecretsCannotBeExposedByRenamingOrTransforming(string source, string target)
    {
        var calls = 0;
        var error = Assert.Throws<SourceBrowseException>(() => SourceMappingPreview.Render([new(source, target, "upper")], _ => { calls++; return new(true, "PRIVATE"); }, _ => { calls++; return default; }));
        Assert.Equal("SensitiveMapping", error.Code); Assert.Equal(0, calls); Assert.DoesNotContain("PRIVATE", error.Message);
        Assert.Single(SourceMappingPreviewAdapter.Project(new Dictionary<string, object?> { ["name"] = "Ada" }, [new("name", "label")]));
    }
    [Fact] public void DuplicateTargetsAndOversizedDraftsAreRefusedBeforeProjection()
    {
        Assert.Equal("DuplicateTarget", Assert.Throws<SourceBrowseException>(() => SourceMappingPreview.Snapshot([new("a", "same"), new("b", "SAME")])).Code);
        Assert.Equal("InvalidMappings", Assert.Throws<SourceBrowseException>(() => SourceMappingPreview.Snapshot(Enumerable.Range(0, 51).Select(i => new SourceMappingRule("a", "target" + i)).ToArray())).Code);
        Assert.Equal("InvalidMappings", Assert.Throws<SourceBrowseException>(() => SourceMappingPreview.Snapshot([new("a", "b", new string('x', 1025))])).Code);
    }
    [Fact] public void ContextDisclosesExcludedSavedMappingsAndPreservesTheirOrder()
    {
        var context = SourceMappingPreviewAdapter.Context([new() { SourceAttribute = "name", SinkAttribute = "label", SortOrder = 2 }, new() { SourceAttribute = "password", SinkAttribute = "copy", TransformExpr = "const:PRIVATE", SortOrder = 1 }]);
        Assert.Equal("name", Assert.Single(context.SavedRules).Source); Assert.Contains("1 of 2", context.Notice);
        Assert.DoesNotContain("PRIVATE", System.Text.Json.JsonSerializer.Serialize(context));
    }
    [Fact] public void BinaryFieldsStayHiddenEvenThroughExpressionsAndRenaming()
    {
        var row = Assert.Single(SourceMappingPreviewAdapter.Project(new Dictionary<string, object?> { ["photo"] = new byte[] { 1, 2, 3 } }, [new("photo", "label", "upper")]));
        Assert.Equal("Binary field hidden", row.Outcome); Assert.Equal("[not previewed]", row.Output);
    }
    [Fact] public void MappingFailuresUseSafeErrorWithoutLeakingRawValues()
    {
        var error = Assert.Throws<SourceBrowseException>(() => SourceMappingPreviewAdapter.Project(new Dictionary<string, object?> { ["name"] = "PRIVATE" }, [new("name", "label", "replace::x")]));
        Assert.Equal("MappingFailed", error.Code); Assert.DoesNotContain("PRIVATE", error.Message);
    }
    [Fact] public void MappingDraftAndResultSurviveTheRemoteWireContract()
    {
        var request = new SourceBrowseRequest(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), new(25, Mappings: [new("name", "label", "trim|upper")]), Guid.NewGuid());
        var copy = System.Text.Json.JsonSerializer.Deserialize<SourceBrowseRequest>(System.Text.Json.JsonSerializer.Serialize(request))!;
        Assert.Equal(request.ExpectedInstanceId, copy.ExpectedInstanceId); Assert.Equal(request.Selection, copy.Selection); Assert.Equal("trim|upper", Assert.Single(copy.Query.Mappings!).Transformation);
        var row = SourceBrowseSample.Row(1, new Dictionary<string, object?> { ["name"] = "Ada" }) with { MappedFields = SourceMappingPreviewAdapter.Project(new Dictionary<string, object?> { ["name"] = "Ada" }, copy.Query.Mappings!) };
        var returned = System.Text.Json.JsonSerializer.Deserialize<SourceBrowseRow>(System.Text.Json.JsonSerializer.Serialize(row))!;
        Assert.Equal("ADA", Assert.Single(returned.MappedFields!).Output);
    }
    [Fact] public void DraftSnapshotsCannotChangeDuringTheSourceRead()
    {
        var rules = new List<SourceMappingRule> { new("name", "label") }; var copy = SourceMappingPreview.Snapshot(rules);
        rules[0] = new("password", "label"); Assert.Equal("name", copy[0].Source);
    }
}
