using System.Security.Claims;
using Conduit.Core.Models;
using Conduit.Core.SyncModels;
using Conduit.SourceBrowsing;
using Conduit.Web.Services;
using Xunit;

namespace Conduit.Web.Tests;

public partial class SourceBrowseTests
{
    private sealed class Targets : ISourceMappingTargetService
    {
        public Task<SourceMappingSchema> ReadAsync(Tenant target, SyncProject project, WorkflowStep step, CancellationToken ct) =>
            Task.FromResult(new SourceMappingSchema("Fixture destination", [new("label", Required: true, MaxLength: 20), new("count", DataType: "Integer")], "Fixture"));
    }
    private async Task<SourceMappingSaveRequest> SaveRequest()
    {
        var selection = ProjectRequest().Selection;
        var context = await Service(true).DescribeAsync(Admin, selection, default);
        return new(selection, context.Mapping!.SavedRules.Select(r => r with { Transformation = "trim|lower" }).ToArray(), context.Mapping.Edit!.Revision);
    }
    [Fact] public async Task SaveValidatesFreshRowsPreservesIdsAndReloadsPersistedStep()
    {
        var request = await SaveRequest(); var id = Assert.Single(store.Mappings).Id;
        var result = await Service(true).SaveMappingsAsync(Admin, request, default);
        Assert.Equal(1, store.Saves); Assert.True(adapter.Source.Yielded > 0);
        var mapping = Assert.Single(store.Mappings); Assert.Equal(id, mapping.Id); Assert.Equal("trim|lower", mapping.TransformExpr);
        Assert.Equal(store.Project.Id, mapping.SyncProjectId); Assert.Equal(store.Step.Id, mapping.WorkflowStepId);
        Assert.Equal(id, Assert.Single(result.Context.Mapping!.SavedRules).Id);
        Assert.NotEqual(request.Revision, result.Context.Mapping.Edit!.Revision); Assert.Contains("saved and reloaded", result.Message);
    }
    [Theory] [InlineData("Tenant")] [InlineData("SourceRead")]
    public async Task ReadTokenCannotSaveMappings(string scope)
    {
        var request = new SourceMappingSaveRequest(ProjectRequest().Selection, [new("name", "label")], "anything");
        await SaveRefuses("MappingAdministratorRequired", request, Token(store.Connection.Id, scope));
        Assert.Equal(0, store.Reads);
    }
    [Fact] public async Task CookieNonAdministratorCannotSaveMappings()
    {
        await SaveRefuses("AdministratorRequired", new(ProjectRequest().Selection, [], "anything"), new(new ClaimsIdentity([], "Cookies")));
        Assert.Equal(0, store.Reads);
    }
    [Fact] public async Task WrongHostRefusesBeforeReadsOrWrites()
    {
        await SaveRefuses("WrongExecutionHost", new(ProjectRequest().Selection, [new("name", "label")], "anything", Guid.NewGuid()));
        Assert.Equal(0, store.Reads);
    }
    [Fact] public async Task StaleDraftCannotOverwriteUpdatedMapping()
    {
        var request = await SaveRequest(); store.Mappings[0].TransformExpr = "lower";
        await SaveRefuses("MappingsChanged", request); Assert.Equal("lower", store.Mappings[0].TransformExpr);
    }
    [Fact] public async Task ChangedSavedScopeRequiresAnotherReview()
    {
        var request = await SaveRequest(); store.Scope.LdapFilter = "(name=Other*)";
        await SaveRefuses("MappingsChanged", request);
    }
    [Theory] [InlineData("unknown", "", "UnknownTarget")] [InlineData("label", "typo", "InvalidExpression")]
    public async Task InvalidDraftRefusesBeforeEnumeration(string target, string expression, string reason)
    {
        var request = await SaveRequest(); await SaveRefuses(reason, request with { Rules = [new("name", target, expression)] });
    }
    [Fact] public async Task RequiredTargetAndForeignMappingIdAreNotSilentlyAccepted()
    {
        var request = await SaveRequest();
        await SaveRefuses("RequiredTarget", request with { Rules = [new("name", "count")] });
        await SaveRefuses("MappingUnavailable", request with { Rules = [new("name", "label", Id: Guid.NewGuid())] });
    }
    [Fact] public async Task InvalidProjectedTypeCannotBeSaved()
    {
        var request = await SaveRequest();
        await SaveRefuses("InvalidSample", request with { Rules = [new("name", "label"), new("name", "count")] }, readsAllowed: true);
    }
    [Fact] public async Task EmptySampleCannotMasqueradeAsValidatedMapping()
    {
        var request = await SaveRequest(); adapter.Source.Count = 0;
        await SaveRefuses("EmptyValidationSample", request);
    }
    [Fact] public async Task ConflictDuringSaveLeavesPersistedMappingUntouched()
    {
        var request = await SaveRequest(); store.Conflict = true;
        await SaveRefuses("MappingsChanged", request, readsAllowed: true); Assert.Equal("upper", store.Mappings[0].TransformExpr);
    }
    [Fact] public async Task SavedButReloadFailedDoesNotOfferAnotherSaveFromOldState()
    {
        var request = await SaveRequest(); store.FailReload = true;
        var result = await Service(true).SaveMappingsAsync(Admin, request, default);
        Assert.Equal(1, store.Saves); Assert.Contains("could not be verified", result.Message); Assert.Null(result.Context.Mapping!.Edit);
        Assert.DoesNotContain("PRIVATE", result.Message);
    }
    [Fact] public async Task PartialSecretFilteredMappingSetCannotReplaceTheFullSet()
    {
        store.Mappings.Add(new() { SourceAttribute = "password", SinkAttribute = "copy" });
        var request = await SaveRequest(); await SaveRefuses("MappingSaveUnavailable", request); Assert.Equal(2, store.Mappings.Count);
    }
    private async Task SaveRefuses(string code, SourceMappingSaveRequest request, ClaimsPrincipal? user = null, bool readsAllowed = false)
    {
        var before = MappingSaveCondition.Version(store.Project, store.Step, store.Mappings);
        var error = await Assert.ThrowsAsync<SourceBrowseException>(() => Service(true).SaveMappingsAsync(user ?? Admin, request, default));
        Assert.Equal(code, error.Code); Assert.Equal(0, store.Saves);
        Assert.Equal(before, MappingSaveCondition.Version(store.Project, store.Step, store.Mappings));
        if (!readsAllowed) Assert.Equal(0, adapter.Source.Yielded);
    }
}
