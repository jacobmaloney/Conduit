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

    // V23.1: each class is its own workflow. Flatten back to a step list (workflow order,
    // then step order) so the behavioral assertions below read as before the IC-parity split.
    private static List<GeneratedSyncStep> StepsOf(GeneratedSyncProject p) =>
        p.Workflows.SelectMany(w => w.Steps).ToList();

    // A class's coverage is its MAPPING step. A workflow also carries a Lookup step for the
    // same class (structural, mirroring IC) and marker governance steps whose ObjectClass is
    // the governance subject rather than a synced class - "GroupMembership" among them. Taking
    // every step's ObjectClass therefore reports each class twice plus the governance
    // subjects, which is what made these read as failures the first time CI ever ran them.
    // The expectations below were right; this helper was measuring the wrong thing.
    private static string[] MappingClasses(GeneratedSyncProject p) =>
        StepsOf(p).Where(s => s.Step.StepType == WorkflowStepTypes.Mapping)
                  .Select(s => s.Step.ObjectClass!).ToArray();

    /// <summary>
    /// EVERY step's class, governance markers and Lookup steps included. Right for comparing
    /// two generation paths for shape equivalence, where an extra or missing step matters;
    /// wrong for asking which classes a blueprint covers.
    /// </summary>
    private static string[] AllStepClasses(GeneratedSyncProject p) =>
        StepsOf(p).Select(s => s.Step.ObjectClass!).ToArray();

    /// <summary>Mapping steps carry attribute mappings; governance markers deliberately do not.</summary>
    private static List<GeneratedSyncStep> MappingStepsOf(GeneratedSyncProject p) =>
        StepsOf(p).Where(s => s.Step.StepType == WorkflowStepTypes.Mapping).ToList();

    [Fact]
    public void Catalog_ShipsThe15CuratedBlueprints_WithUniqueIdsAndNames()
    {
        Assert.Equal(15, SyncProjectBlueprintCatalog.All.Count);
        foreach (var id in new[]
        {
            "entra-directory-governance", "m365-license-usage",
            "sharepoint-collaboration-governance", "azure-resource-inventory",
            "aws-iam-governance", "aws-identity-center-governance",
            "gws-directory-governance", "active-directory-governance",
            "active-directory-full-estate", "active-roles-governance",
            "okta-directory-governance", "generic-ldap-directory",
            "scim-directory-governance", "database-directory-governance",
            "sql-server-discovery"
        })
        {
            Assert.NotNull(SyncProjectBlueprintCatalog.GetById(id));
        }

        // No duplicate ids or display names — a duplicate renders as two identical
        // cards in the create flow (this caught a real double Google blueprint).
        Assert.Equal(
            SyncProjectBlueprintCatalog.All.Count,
            SyncProjectBlueprintCatalog.All.Select(b => b.Id.ToLowerInvariant()).Distinct().Count());
        Assert.Equal(
            SyncProjectBlueprintCatalog.All.Count,
            SyncProjectBlueprintCatalog.All.Select(b => b.Name.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void AwsIamGovernance_Expands_To_SixClasses_WithMappings()
    {
        var bp = SyncProjectBlueprintCatalog.GetById("aws-iam-governance")!;
        Assert.Equal("AWS", bp.SourceSystemType);
        var project = ExpandOne(bp);

        // The curated Full set for this connector, from SyncProjectGenerator's per-system
        // arrays. A *Governance* blueprint means "everything this connector supports", so a
        // class added there belongs here too - update both together.
        Assert.Equal(new[] { "user", "group", "role", "policy", "account", "computer" }, MappingClasses(project));
        Assert.All(MappingStepsOf(project), s => Assert.True(s.Mappings.Count > 0,
            $"class {s.Step.ObjectClass} should have > 0 mappings"));
    }

    [Fact]
    public void AwsIdentityCenterGovernance_Expands_To_FourClasses_WithMappings()
    {
        var bp = SyncProjectBlueprintCatalog.GetById("aws-identity-center-governance")!;
        Assert.Equal("AWSIdentityCenter", bp.SourceSystemType);
        var project = ExpandOne(bp);

        // The curated Full set for this connector, from SyncProjectGenerator's per-system
        // arrays. A *Governance* blueprint means "everything this connector supports", so a
        // class added there belongs here too - update both together.
        Assert.Equal(new[] { "user", "group", "permissionSet", "application" }, MappingClasses(project));
        Assert.All(MappingStepsOf(project), s => Assert.True(s.Mappings.Count > 0,
            $"class {s.Step.ObjectClass} should have > 0 mappings"));
    }

    [Fact]
    public void GwsDirectoryGovernance_Expands_To_NineClasses_WithMappings()
    {
        var bp = SyncProjectBlueprintCatalog.GetById("gws-directory-governance")!;
        Assert.Equal("GoogleWorkspace", bp.SourceSystemType);
        var project = ExpandOne(bp);

        // The curated Full set for this connector, from SyncProjectGenerator's per-system
        // arrays. A *Governance* blueprint means "everything this connector supports", so a
        // class added there belongs here too - update both together.
        Assert.Equal(new[]
        {
            "user", "group", "organizationalUnit", "role", "domain",
            "mobiledevice", "chromeosdevice", "roleAssignment", "calendarresource"
        }, MappingClasses(project));
        Assert.All(MappingStepsOf(project), s => Assert.True(s.Mappings.Count > 0,
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
            Assert.Equal(MappingClasses(project)[0], project.Project.ObjectClass);
            // IC parity: one workflow per class, each holding exactly one Mapping step,
            // named "<class> Upsert Sync". A workflow may ALSO carry a structural Lookup step
            // and marker governance steps (GroupMembership, LicenseSync and friends), so the
            // Mapping step is singled out rather than assuming it is the only one.
            Assert.Equal(MappingClasses(project).Length, project.Workflows.Count);
            Assert.All(project.Workflows, w =>
            {
                var step = Assert.Single(w.Steps, x => x.Step.StepType == WorkflowStepTypes.Mapping);
                Assert.Equal($"{step.Step.ObjectClass} Upsert Sync", w.Workflow.Name);
            });
        }
    }

    [Fact]
    public void EntraDirectoryGovernance_Expands_To_All14_DirectoryClasses_WithMappings()
    {
        var bp = SyncProjectBlueprintCatalog.GetById("entra-directory-governance")!;
        var project = ExpandOne(bp);

        var expected = new[]
        {
            "user", "group", "servicePrincipal", "directoryRole",
            "application", "device", "administrativeUnit", "conditionalAccessPolicy",
            "oAuth2PermissionGrant", "domain", "m365usage", "signinlog", "license",
            "approleassignment"
        };

        var actual = MappingClasses(project);
        Assert.Equal(14, actual.Length);
        Assert.Equal(expected, actual);
        Assert.Contains("m365usage", actual);

        // Every MAPPING step in this blueprint is a real (non-deferred) class, so each must
        // have at least one attribute mapping resolved from the catalog. Governance markers are
        // excluded deliberately: they carry no mappings by design, and several of them take the
        // governed class as their ObjectClass - the Entra sign-in-log and licence markers are
        // both ObjectClass "user" - so including them asserted that a marker was a sync step.
        foreach (var step in MappingStepsOf(project))
        {
            Assert.False(SyncProjectBlueprintCatalog.IsDeferredClass(step.Step.ObjectClass!));
            Assert.True(step.Mappings.Count > 0,
                $"class {step.Step.ObjectClass} should have > 0 mappings");
        }

        // Spot-check the called-out non-empty classes explicitly.
        foreach (var cls in new[] { "user", "group", "m365usage" })
        {
            // Single over ALL steps now finds several: "user" is also the ObjectClass of the
            // sign-in-log and licence governance markers. The mapping step is the one meant.
            var step = MappingStepsOf(project).Single(s => s.Step.ObjectClass == cls);
            Assert.True(step.Mappings.Count > 0, $"{cls} mappings empty");
        }
    }

    [Fact]
    public void AzureResourceInventory_Expands_To_ExactTwoClasses_WithMappings()
    {
        var bp = SyncProjectBlueprintCatalog.GetById("azure-resource-inventory")!;
        var project = ExpandOne(bp);

        var actual = MappingClasses(project);
        Assert.Equal(new[] { "azuresubscription", "azureresource" }, actual);
        Assert.All(MappingStepsOf(project), s => Assert.True(s.Mappings.Count > 0,
            $"class {s.Step.ObjectClass} should have > 0 mappings"));
    }

    [Fact]
    public void M365LicenseUsage_Expands_To_ExplicitThreeClasses()
    {
        var bp = SyncProjectBlueprintCatalog.GetById("m365-license-usage")!;
        var project = ExpandOne(bp);

        Assert.Equal(new[] { "user", "m365usage", "site" }, MappingClasses(project));
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
        Assert.Equal(AllStepClasses(viaMode), AllStepClasses(viaExplicit));
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

        Assert.Equal(new[] { "user", "group" }, MappingClasses(project));
    }
}
