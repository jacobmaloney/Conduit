using System.Security.Claims;
using System.Text.Json;
using Conduit.Core.SyncModels;
using Conduit.SourceBrowsing;

namespace Conduit.Web.Services;

public sealed partial class SourceBrowseService
{
    public static void RequireMappingAdministrator(ClaimsPrincipal user)
    {
        RequireAuthenticated(user);
        if (user.Identity!.AuthenticationType == "ApiToken" && user.FindFirst("TokenScope")?.Value != "Admin")
            throw new SourceBrowseException("MappingAdministratorRequired", "An administrator is required to edit project mappings. Source-read access does not grant project-write access.");
    }

    public async Task<SourceMappingSaveResult> SaveMappingsAsync(ClaimsPrincipal user, SourceMappingSaveRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); RequireMappingAdministrator(user); RequireInstance(request.ExpectedInstanceId);
        var resolved = await ResolveAsync(user, request.Selection, ct);
        SourceMappingEdit.RequireSavable(resolved.Context.Mapping?.Edit, request.Revision);
        var rules = SourceMappingEdit.Validate(request.Rules, resolved.Context.Mapping!.Edit!.Schema, true);
        SourceMappingPreviewAdapter.Validate(rules);
        foreach (var rule in rules)
        {
            if (Conduit.Mapping.AttributeTransformExpression.ValidationError(rule.Transformation) is { } error)
                throw new SourceBrowseException("InvalidExpression", error);
            if (string.IsNullOrWhiteSpace(rule.Source) && !Conduit.Mapping.AttributeTransformExpression.ProducesValueWhenSourceMissing(rule.Transformation))
                throw new SourceBrowseException("SourceFieldRequired", "Choose a source field or a value-producing expression.");
        }
        var draft = new List<AttributeMapping>();
        foreach (var rule in rules)
        {
            if (rule.Id.HasValue && !resolved.Mappings.Any(m => m.Id == rule.Id))
                throw new SourceBrowseException("MappingUnavailable", "A saved mapping is no longer available. Reload the step.");
            draft.Add(new() { Id = rule.Id ?? Guid.NewGuid(), SyncProjectId = resolved.Project!.Id, WorkflowStepId = resolved.Step!.Id,
                SourceAttribute = rule.Source, SinkAttribute = rule.Target, TransformExpr = rule.Transformation, IsRequired = rule.Required, SortOrder = draft.Count + 1 });
        }
        var sample = await ReadAsync(user, new(request.Selection, new(50, Mappings: rules), request.ExpectedInstanceId), ct);
        SourceMappingEdit.RequireSavable(sample.Context.Mapping?.Edit, request.Revision);
        if (sample.Rows.Count == 0) throw new SourceBrowseException("EmptyValidationSample", "No source rows were available to validate. Adjust the source scope before saving here.");
        if (sample.Rows.Any(r => r.MappedFields!.Any(f => f.Outcome.StartsWith("Invalid:", StringComparison.Ordinal) || f.Outcome == "Binary field hidden")))
            throw new SourceBrowseException("InvalidSample", "The sample contains invalid or binary mapped values. Preview mappings and correct the indicated fields before saving.");
        ct.ThrowIfCancellationRequested();
        try { await store.SaveMappingsAsync(resolved.Project!.Id, resolved.Step!.Id, draft, new(request.Revision), ct); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("MappingsChanged:", StringComparison.Ordinal))
        { throw new SourceBrowseException("MappingsChanged", "The project or mappings changed. Reload and review them before saving."); }
        catch (SourceBrowseException) { throw; }
        catch (Exception ex) { logger.LogWarning("Mapping save not confirmed ({ErrorType})", ex.GetType().Name); throw new SourceBrowseException("SaveUnconfirmed", "Could not confirm the save. Reload the step before retrying."); }
        try
        {
            var reloaded = await ResolveAsync(user, request.Selection, ct);
            if (MappingSaveCondition.Version(resolved.Project!, resolved.Step!, draft) != MappingSaveCondition.Version(resolved.Project!, resolved.Step!, reloaded.Mappings))
                throw new InvalidOperationException("Saved mappings changed before reload.");
            return new("Mappings saved and reloaded for this import step. No sync was started.", reloaded.Context);
        }
        catch { return new("Mappings were saved, but could not be verified by reloading. Close and reopen this browser before making further changes.", resolved.Context with { Mapping = resolved.Context.Mapping with { Edit = null } }); }
    }
}
