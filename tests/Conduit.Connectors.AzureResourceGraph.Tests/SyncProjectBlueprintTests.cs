using Conduit.Core.Models;
using Conduit.Core.SyncModels;
using Conduit.Sync.Templates;
using Xunit;

namespace Conduit.Connectors.AzureResourceGraph.Tests;

/// <summary>
/// Proves the blueprint catalog expands — through the real ISyncProjectGenerator
/// and AttributeMapService (catalog-backed, no DB, no live fetch) — into a fully
/// configured multi-class project: ONE WORKFLOW PER CLASS (IC parity), each holding
/// one Mapping step with mappings.
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

    // V23.1: each class is now its own workflow holding a single Mapping step.
    // Flatten back to the per-class step list (workflow order, then step order)
    // so the behavioral assertions below read the same as before the IC-parity split.
    private static List<GeneratedSyncStep> StepsOf(GeneratedSyncProject p) =>
        p.Workflows.SelectMany(w => w.Steps).ToList();

    private static string[] StepClasses(GeneratedSyncProject p) =>
        StepsOf(p).Select(s => s.Step.ObjectClass!).ToArray();

    [Fact]
    public void Catalog_ShipsExactlySixBlueprints()
    {
        Assert.Equal(6, SyncProjectBlueprintCatalog.All.Count);
        Assert.NotNull(SyncProjectBlueprintCatalog.GetById("entra-directory-governance"));
        Assert.NotNull(SyncProjectBlueprintCatalog.GetById("m365-license-usage"));
        Assert.NotNull(SyncProjectBlueprintCatalog.GetById("azure-resource-inventory"));
        Assert.NotNull(SyncProjectBlueprintCatalog.GetById("aws-iam-governance"));
        Assert.NotNull(SyncProjectBlueprintCatalog.GetById("aws-identity-center-governance"));
        Assert.NotNull(SyncProjectBlueprintCatalog.GetById("gws-directory-governance"));
    }

    [Fact]
    public void AwsIamGovernance_Expands_To_FiveClasses_WithMappings()
    {
        var bp = SyncProjectBlueprintCatalog.GetById("aws-iam-governance")!;
        Assert.Equal("AWS", bp.SourceSystemType);
        var project = ExpandOne(bp);

        Assert.Equal(new[] { "user", "group", "role", "policy", "account" }, StepClasses(project));
        Assert.All(StepsOf(project), s => Assert.True(s.Mappings.Count > 0,
            $"class {s.Step.ObjectClass} should have > 0 mappings"));
    }

    [Fact]
    public void AwsIdentityCenterGovernance_Expands_To_ThreeClasses_WithMappings()
    {
        var bp = SyncProjectBlueprintCatalog.GetById("aws-identity-center-governance")!;
        Assert.Equal("AWSIdentityCenter", bp.SourceSystemType);
        var project = ExpandOne(bp);

        Assert.Equal(new[] { "user", "group", "permissionSet" }, StepClasses(project));
        Assert.All(StepsOf(project), s => Assert.True(s.Mappings.Count > 0,
            $"class {s.Step.ObjectClass} should have > 0 mappings"));
    }

    [Fact]
    public void GwsDirectoryGovernance_Expands_To_FiveClasses_WithMappings()
    {
        var bp = SyncProjectBlueprintCatalog.GetById("gws-directory-governance")!;
        Assert.Equal("GoogleWorkspace", bp.SourceSystemType);
        var project = ExpandOne(bp);

        Assert.Equal(new[] { "user", "group", "organizationalUnit", "role", "domain" }, StepClasses(project));
        Assert.All(StepsOf(project), s => Assert.True(s.Mappings.Count > 0,
            $"class {s.Step.ObjectClass} should have > 0 mappings"));
    }

    [Fact]
    public void Generator_AwsFull_advertises_role_policy_account_after_casing_fix()
    {
        var gen = NewGenerator();
        // The bug: case "Aws" never matched the live adapter SystemType "AWS",
        // so a real AWS source silently got the default {user, group}.
        var full = gen.GetObjectClasses("AWS", GenerationMode.Full);
        Assert.Contains("role", full);
        Assert.Contains("policy", full);
        Assert.Contains("account", full);

        var core = gen.GetObjectClasses("AWS", GenerationMode.Core);
        Assert.Equal(new[] { "user", "group" }, core.ToArray());
    }

    [Fact]
    public void Generator_AwsIdentityCenterFull_advertises_permissionSet()
    {
        var gen = NewGenerator();
        var full = gen.GetObjectClasses("AWSIdentityCenter", GenerationMode.Full);
        Assert.Contains("permissionSet", full);
        Assert.Contains("user", full);
        Assert.Contains("group", full);
    }

    [Fact]
    public void Generator_GoogleFull_advertises_orgUnit_role_domain()
    {
        var gen = NewGenerator();
        var full = gen.GetObjectClasses("GoogleWorkspace", GenerationMode.Full);
        Assert.Contains("organizationalUnit", full);
        Assert.Contains("role", full);
        Assert.Contains("domain", full);
    }

    [Theory]
    [InlineData("AWS", "Role")]
    [InlineData("AWS", "Policy")]
    [InlineData("AWS", "Account")]
    [InlineData("AWSIdentityCenter", "User")]
    [InlineData("AWSIdentityCenter", "Group")]
    [InlineData("AWSIdentityCenter", "PermissionSet")]
    [InlineData("GoogleWorkspace", "Role")]
    [InlineData("GoogleWorkspace", "Domain")]
    public void AttributeCatalog_HasRequiredSourceUniqueId_For_NewClasses(string systemType, string objectClass)
    {
        var entries = AttributeTemplateCatalog.Get(systemType, objectClass);
        Assert.NotNull(entries);
        Assert.NotEmpty(entries!);
        Assert.Contains(entries!, e => e.Canonical == "SourceUniqueId" && e.IsRequired);
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
            Assert.All(StepsOf(project), s =>
                Assert.Equal(WorkflowStepTypes.Mapping, s.Step.StepType));

            // IC parity: one workflow per class, each holding exactly one Mapping step,
            // named "<class> Upsert Sync".
            Assert.Equal(StepClasses(project).Length, project.Workflows.Count);
            Assert.All(project.Workflows, w =>
            {
                var step = Assert.Single(w.Steps);
                Assert.Equal($"{step.Step.ObjectClass} Upsert Sync", w.Workflow.Name);
            });
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
        foreach (var step in StepsOf(project))
        {
            Assert.False(SyncProjectBlueprintCatalog.IsDeferredClass(step.Step.ObjectClass!));
            Assert.True(step.Mappings.Count > 0,
                $"class {step.Step.ObjectClass} should have > 0 mappings");
        }

        // Spot-check the called-out non-empty classes explicitly.
        foreach (var cls in new[] { "user", "group", "m365usage" })
        {
            var step = StepsOf(project).Single(s => s.Step.ObjectClass == cls);
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
        Assert.All(StepsOf(project), s => Assert.True(s.Mappings.Count > 0,
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

        var viaModeSteps = StepsOf(viaMode);
        var viaExplicitSteps = StepsOf(viaExplicit);
        Assert.Equal(StepClasses(viaMode), StepClasses(viaExplicit));
        Assert.Equal(viaModeSteps.Count, viaExplicitSteps.Count);

        for (var i = 0; i < viaModeSteps.Count; i++)
        {
            var m = viaModeSteps[i];
            var e = viaExplicitSteps[i];
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
