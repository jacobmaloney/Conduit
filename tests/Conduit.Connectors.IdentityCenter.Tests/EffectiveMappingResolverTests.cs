using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Sync.Orchestration;
using Xunit;

namespace Conduit.Connectors.IdentityCenter.Tests;

public class EffectiveMappingResolverTests
{
    [Fact]
    public void Empty_step_uses_project_transform_and_default_instead_of_stock_template()
    {
        AttributeMapping[] project = [
            new() { SourceAttribute = "customName", SinkAttribute = "DisplayName", TransformExpr = "trim|upper", SortOrder = 1 },
            new() { SourceAttribute = "missing", SinkAttribute = "Department", TransformExpr = "default:Operations", SortOrder = 2 }
        ];
        var resolved = EffectiveMappingResolver.Resolve([], project, "ActiveDirectory", "IdentityCenter", "user");
        Assert.Equal(EffectiveMappingOrigin.Project, resolved.Origin);
        var result = new ConnectorMappingPlan(resolved.Mappings).Apply(new ConnectorObject
        {
            SourceId = "1", ObjectClass = "user", Attributes = new() { ["customName"] = "  Ada  " }
        });
        Assert.Equal("ADA", result.Attributes["DisplayName"]);
        Assert.Equal("Operations", result.Attributes["Department"]);
        Assert.Equal(2, resolved.Mappings.Count);
        project[0].TransformExpr = "lower";
        Assert.Equal("trim|upper", resolved.Mappings[0].TransformExpr); // the effective pass is a snapshot
    }

    [Fact]
    public void Step_override_wins_and_templates_remain_the_last_fallback()
    {
        var step = new AttributeMapping { SourceAttribute = "step", SinkAttribute = "DisplayName" };
        var project = new AttributeMapping { SourceAttribute = "project", SinkAttribute = "DisplayName" };
        var owned = EffectiveMappingResolver.Resolve([step], [project], "ActiveDirectory", "IdentityCenter", "user");
        Assert.Equal(EffectiveMappingOrigin.Step, owned.Origin);
        Assert.Equal("step", Assert.Single(owned.Mappings).SourceAttribute);
        var defaults = EffectiveMappingResolver.Resolve([], [], "ActiveDirectory", "IdentityCenter", "user");
        Assert.Equal(EffectiveMappingOrigin.Template, defaults.Origin);
        Assert.NotEmpty(defaults.Mappings);
    }
}
