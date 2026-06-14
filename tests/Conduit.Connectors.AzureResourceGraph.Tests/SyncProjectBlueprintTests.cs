using Conduit.Core.Models;
using Conduit.Core.SyncModels;
using Conduit.Sync.Templates;
using Xunit;

namespace Conduit.Connectors.AzureResourceGraph.Tests;

/// <summary>
/// Proves the blueprint catalog expands — through the real ISyncProjectGenerator
/// and AttributeMapService (catalog-backed, no DB, no live fetch) — into a fully
/// configured multi-class project: one Mapping step per class, with mappings.
/// </summary>
public class SyncProjectBlueprintTests
{
    private static SyncProjectGenerator NewGenerator() => new(new AttributeMapService());

    private static Tenant Source(string systemType) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Source System",
        SystemType = systemType
    };

    private static Tenant Sink() => new()
    {
        Id = Guid.NewGuid(),
        Name = "IdentityCenter",
        SystemType = "IdentityCenter"
    };

    private static GeneratedSyncProject ExpandOne(SyncProjectBlueprint bp)
    {
        var gen = NewGenerator();
        var projects = bp.Expand(gen, Source(bp.SourceSystemType), Sink(), null, Array.Empty<string>());
        return Assert.Single(projects);
    }

    private static string[] StepClasses(GeneratedSyncProject p) =>
        p.Steps.Select(s => s.Step.ObjectClass!).ToArray();

    [Fact]
    public void Catalog_ShipsExactlyThreeBlueprints()
    {
        Assert.Equal(3, SyncProjectBlueprintCatalog.All.Count);
        Assert.NotNull(SyncProjectBlueprintCatalog.GetById("entra-directory-governance"));
        Assert.NotNull(SyncProjectBlueprintCatalog.GetById("m365-license-usage"));
        Assert.NotNull(SyncProjectBlueprintCatalog.GetById("azure-resource-inventory"));
    }

    [Fact]
    public void AllBlueprints_ProduceDisabledSkipUnchangedProjects()
    {
        foreach (var bp in SyncProjectBlueprintCatalog.All)
        {
            var project = ExpandOne(bp);
            Assert.False(project.Project.IsEnabled);
            Assert.True(project.Project.SkipUnchanged);
            Assert.Equal(StepClasses(project)[0], project.Project.ObjectClass);
            Assert.All(project.Steps, s =>
                Assert.Equal(WorkflowStepTypes.Mapping, s.Step.StepType));
        }
    }

    [Fact]
    public void EntraDirectoryGovernance_Expands_To_All11_DirectoryClasses_WithMappings()
    {
        var bp = SyncProjectBlueprintCatalog.GetById("entra-directory-governance")!;
        var project = ExpandOne(bp);

        var expected = new[]
        {
            "user", "group", "servicePrincipal", "directoryRole",
            "application", "device", "administrativeUnit", "conditionalAccessPolicy",
            "oAuth2PermissionGrant", "domain", "m365usage"
        };

        var actual = StepClasses(project);
        Assert.Equal(11, actual.Length);
        Assert.Equal(expected, actual);
        Assert.Contains("m365usage", actual);

        // Every step in this blueprint is a real (non-deferred) class, so each must
        // have at least one attribute mapping resolved from the catalog.
        foreach (var step in project.Steps)
        {
            Assert.False(SyncProjectBlueprintCatalog.IsDeferredClass(step.Step.ObjectClass!));
            Assert.True(step.Mappings.Count > 0,
                $"class {step.Step.ObjectClass} should have > 0 mappings");
        }

        // Spot-check the called-out non-empty classes explicitly.
        foreach (var cls in new[] { "user", "group", "m365usage" })
        {
            var step = project.Steps.Single(s => s.Step.ObjectClass == cls);
            Assert.True(step.Mappings.Count > 0, $"{cls} mappings empty");
        }
    }

    [Fact]
    public void AzureResourceInventory_Expands_To_ExactTwoClasses_WithMappings()
    {
        var bp = SyncProjectBlueprintCatalog.GetById("azure-resource-inventory")!;
        var project = ExpandOne(bp);

        var actual = StepClasses(project);
        Assert.Equal(new[] { "azuresubscription", "azureresource" }, actual);
        Assert.All(project.Steps, s => Assert.True(s.Mappings.Count > 0,
            $"class {s.Step.ObjectClass} should have > 0 mappings"));
    }

    [Fact]
    public void M365LicenseUsage_Expands_To_ExplicitThreeClasses()
    {
        var bp = SyncProjectBlueprintCatalog.GetById("m365-license-usage")!;
        var project = ExpandOne(bp);

        Assert.Equal(new[] { "user", "m365usage", "site" }, StepClasses(project));
    }

    [Fact]
    public void ExplicitClassOverload_MatchesModePath_ForEquivalentClassSet()
    {
        var gen = NewGenerator();
        var src = Source("EntraID");
        var sink = Sink();

        // The mode path for EntraID Core yields {user, group}.
        var viaMode = Assert.Single(
            gen.Generate(src, sink, GenerationMode.Core, null, Array.Empty<string>()));

        // The explicit path with the same set must yield the same shape.
        var viaExplicit = Assert.Single(
            gen.Generate(src, sink, new[] { "user", "group" }, null, Array.Empty<string>()));

        Assert.Equal(StepClasses(viaMode), StepClasses(viaExplicit));
        Assert.Equal(viaMode.Steps.Count, viaExplicit.Steps.Count);

        for (var i = 0; i < viaMode.Steps.Count; i++)
        {
            var m = viaMode.Steps[i];
            var e = viaExplicit.Steps[i];
            Assert.Equal(m.Step.ObjectClass, e.Step.ObjectClass);
            Assert.Equal(m.Step.StepType, e.Step.StepType);
            Assert.Equal(m.Scope.LdapFilter, e.Scope.LdapFilter);
            Assert.Equal(m.Scope.PageSize, e.Scope.PageSize);
            Assert.Equal(m.Mappings.Count, e.Mappings.Count);
        }

        Assert.Equal(viaMode.Project.IsEnabled, viaExplicit.Project.IsEnabled);
        Assert.Equal(viaMode.Project.SkipUnchanged, viaExplicit.Project.SkipUnchanged);
        Assert.Equal(viaMode.Project.ObjectClass, viaExplicit.Project.ObjectClass);
    }

    [Fact]
    public void ExplicitOverload_DedupesAndDropsBlanks_PreservingOrder()
    {
        var gen = NewGenerator();
        var project = Assert.Single(gen.Generate(
            Source("EntraID"), Sink(),
            new[] { "user", "", "group", "user", "  " },
            null, Array.Empty<string>()));

        Assert.Equal(new[] { "user", "group" }, StepClasses(project));
    }
}
