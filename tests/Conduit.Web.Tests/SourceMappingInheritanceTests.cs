using Conduit.Core.SyncModels;
using Conduit.SourceBrowsing;
using Conduit.Sync.Connectors;
using Conduit.Sync.Orchestration;
using Xunit;

namespace Conduit.Web.Tests;

public partial class SourceBrowseTests
{
    private void InheritProjectMapping()
    {
        store.ProjectMappings = [new()
        {
            SyncProjectId = store.Project.Id, SourceAttribute = "name", SinkAttribute = "label", TransformExpr = "trim|lower"
        }];
        store.Mappings.Clear();
    }

    [Fact]
    public async Task Inherited_preview_uses_the_same_project_rule_as_execution_and_can_create_an_override()
    {
        InheritProjectMapping();
        var inheritedId = store.ProjectMappings[0].Id;
        var context = await Service(true).DescribeAsync(Admin, ProjectRequest().Selection, default);
        Assert.True(context.Mapping!.Edit!.CanSave);
        Assert.Contains("inherits the project mappings", context.Mapping.Help);
        Assert.Null(Assert.Single(context.Mapping.SavedRules).Id);
        var sample = await Service(true).ReadAsync(Admin, ProjectRequest() with
        {
            Query = new(2, Mappings: context.Mapping.SavedRules)
        }, default);
        var effective = EffectiveMappingResolver.Resolve([], store.ProjectMappings, "CSV", "ActiveDirectory", "Group");
        var runtime = new ConnectorMappingPlan(effective.Mappings).Apply(new ConnectorObject
        {
            SourceId = "0", ObjectClass = "Group", Attributes = new() { ["name"] = "Row 0" }
        });
        Assert.Equal(runtime.Attributes["label"], Assert.Single(sample.Rows[0].MappedFields!).Output);

        var draft = new SourceMappingSaveRequest(ProjectRequest().Selection,
            context.Mapping.SavedRules.Select(r => r with { Transformation = "upper" }).ToArray(), context.Mapping.Edit.Revision);
        var saved = await Service(true).SaveMappingsAsync(Admin, draft, default);
        Assert.Equal(1, store.Saves);
        Assert.Equal("trim|lower", Assert.Single(store.ProjectMappings).TransformExpr);
        Assert.Equal(inheritedId, store.ProjectMappings[0].Id);
        var owned = Assert.Single(store.Mappings);
        Assert.NotEqual(inheritedId, owned.Id);
        Assert.Equal(store.Step.Id, owned.WorkflowStepId);
        Assert.Equal("upper", owned.TransformExpr);
        Assert.Equal(owned.Id, Assert.Single(saved.Context.Mapping!.SavedRules).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_project_defaults_refuse_inherited_override_before_mutation(bool duringSave)
    {
        InheritProjectMapping();
        var context = await Service(true).DescribeAsync(Admin, ProjectRequest().Selection, default);
        var request = new SourceMappingSaveRequest(ProjectRequest().Selection, context.Mapping!.SavedRules, context.Mapping.Edit!.Revision);
        if (duringSave) store.BeforeSave = () => store.ProjectMappings[0].TransformExpr = "prefix:changed-";
        else store.ProjectMappings[0].TransformExpr = "prefix:changed-";
        var error = await Assert.ThrowsAsync<SourceBrowseException>(() => Service(true).SaveMappingsAsync(Admin, request, default));
        Assert.Equal("MappingsChanged", error.Code);
        Assert.Empty(store.Mappings); Assert.Equal(0, store.Saves);
        Assert.Equal("prefix:changed-", Assert.Single(store.ProjectMappings).TransformExpr);
        if (!duringSave) Assert.Equal(0, adapter.Source.Yielded);
    }

    [Fact]
    public async Task An_empty_step_without_project_defaults_can_still_save_an_explicit_override()
    {
        store.Mappings.Clear();
        var context = await Service(true).DescribeAsync(Admin, ProjectRequest().Selection, default);
        Assert.True(context.Mapping!.Edit!.CanSave);
        var request = new SourceMappingSaveRequest(ProjectRequest().Selection,
            [new("name", "label", "upper")], context.Mapping.Edit.Revision);
        await Service(true).SaveMappingsAsync(Admin, request, default);
        Assert.Equal(1, store.Saves);
        Assert.Equal("upper", Assert.Single(store.Mappings).TransformExpr);
        Assert.Empty(store.ProjectMappings);
    }

    [Fact]
    public void Project_defaults_only_participate_in_the_revision_while_the_step_inherits()
    {
        var projectRule = new AttributeMapping { SourceAttribute = "name", SinkAttribute = "label" };
        var before = MappingSaveCondition.Version(store.Project, store.Step, store.Mappings, store.Scope, [projectRule]);
        projectRule.TransformExpr = "upper";
        Assert.Equal(before, MappingSaveCondition.Version(store.Project, store.Step, store.Mappings, store.Scope, [projectRule]));
        store.Mappings.Clear();
        var inherited = MappingSaveCondition.Version(store.Project, store.Step, [], store.Scope, [projectRule]);
        projectRule.TransformExpr = "lower";
        Assert.NotEqual(inherited, MappingSaveCondition.Version(store.Project, store.Step, [], store.Scope, [projectRule]));
    }
}
