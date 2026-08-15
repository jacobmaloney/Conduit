using Conduit.Core.SyncModels;
using Xunit;

namespace Conduit.Web.Tests;

public sealed class SyncRunStepMarkerTests
{
    [Fact]
    public void ParsesCanonicalOrchestratorMarker()
    {
        var parsed = SyncRunStepMarker.TryParse(
            "  Step 'Import users' [Mapping] starting (ordinal 2).",
            out var marker);

        Assert.True(parsed);
        Assert.Equal("Import users", marker.Name);
        Assert.Equal("Mapping", marker.StepType);
    }

    [Fact]
    public void DoesNotTreatCompletionAsAnotherStepStart()
    {
        var parsed = SyncRunStepMarker.TryParse(
            "  Step 'Import users' done. +Read=793 +Created=0 +Updated=195 +Skipped=285 +Failed=0.",
            out _);

        Assert.False(parsed);
    }

    [Theory]
    [InlineData("Step: Create identities [PersonCreate]", "Create identities", "PersonCreate")]
    [InlineData("Step: Legacy mapping", "Legacy mapping", null)]
    public void PreservesLegacyMarkerCompatibility(string message, string name, string? stepType)
    {
        Assert.True(SyncRunStepMarker.TryParse(message, out var marker));
        Assert.Equal(name, marker.Name);
        Assert.Equal(stepType, marker.StepType);
    }
}
