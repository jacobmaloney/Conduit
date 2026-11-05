using System.Runtime.CompilerServices;
using Conduit.Core.SyncModels;
using Conduit.Sync.Orchestration;
using Conduit.Sync.Templates;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// INHERIT-HONEST-01 (the Granite walk, 2026-11-05): the Domain.local "user upsert Contractor" step showed "0 rules",
/// warned "No mappings configured" and sent the admin to a project Mappings tab the edit dialog no longer has, while
/// every run logged "uses 26 template mapping(s)". The step mapping dialog now describes an inheriting step with the
/// same resolver the run uses, and names neither a missing tab nor an empty map that is not empty.
/// </summary>
public class InheritedMappingsHonestTests
{
    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));

    [Fact]
    public void An_inheriting_step_with_no_project_rules_runs_the_default_template()
    {
        var effective = EffectiveMappingResolver.Resolve(Array.Empty<AttributeMapping>(), Array.Empty<AttributeMapping>(),
            AttributeTemplateCatalog.Systems.ActiveDirectory, AttributeTemplateCatalog.Systems.IdentityCenter, "user");

        Assert.Equal(EffectiveMappingOrigin.Template, effective.Origin);
        Assert.Equal(26, effective.Mappings.Count);
    }

    [Fact]
    public void The_step_mapping_dialog_describes_what_the_run_maps()
    {
        var page = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Pages", "Sync", "SyncProjects.razor")));

        Assert.Contains("EffectiveMappingResolver.Resolve(Array.Empty<AttributeMapping>(), mappingForm,", page);
        Assert.Contains("\"Using the default mapping\"", page);
        Assert.Contains("@if (mmRows.Count == 0 && mmEffective is not null)", page);
        Assert.DoesNotContain("Inherit from project Mappings tab", page);
        // The edit dialog has Setup / Workflows / Schedule only; neither inherit dialog may send the admin to a tab it lacks.
        Assert.DoesNotContain("Scope tab", page);
        Assert.DoesNotContain("Inherit from project Scope", page);
        Assert.DoesNotContain("This step is running the rules from the project <strong>Mappings</strong> tab", page);
    }
}
