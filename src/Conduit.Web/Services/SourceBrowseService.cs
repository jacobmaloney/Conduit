using System.Security.Claims;
using System.Text.Json;
using Conduit.Core.Models;
using Conduit.Core.SyncModels;
using Conduit.DataAccess.Repositories;
using Conduit.SourceBrowsing;
using Conduit.Sync.Connectors;
using Conduit.Sync.Orchestration;
using Conduit.Sync.Security;

namespace Conduit.Web.Services;

public sealed record SourceBrowseHost(Guid InstanceId, string Name);

public interface ISourceBrowseStore
{
    Task<Tenant?> ConnectionAsync(Guid id);
    Task<SyncProject?> ProjectAsync(Guid id);
    Task<IReadOnlyList<WorkflowStep>> ImportStepsAsync(Guid projectId);
    Task<IReadOnlyList<AttributeMapping>> MappingsAsync(Guid stepId) => Task.FromResult<IReadOnlyList<AttributeMapping>>([]);
    Task<IReadOnlyList<AttributeMapping>> ProjectMappingsAsync(Guid projectId) => Task.FromResult<IReadOnlyList<AttributeMapping>>([]);
    Task<SyncProjectScope?> ScopeAsync(Guid projectId, Guid? stepId);
    Task SaveMappingsAsync(Guid projectId, Guid stepId, IReadOnlyList<AttributeMapping> mappings, MappingSaveCondition condition, CancellationToken ct) =>
        throw new SourceBrowseException("MappingSaveUnavailable", "Mapping saves are unavailable on this host.");
}

public sealed class SourceBrowseStore(TenantRepository tenants, SyncProjectRepository projects,
    WorkflowRepository workflows) : ISourceBrowseStore
{
    public Task<Tenant?> ConnectionAsync(Guid id) => tenants.GetByIdAsync(id);
    public Task<SyncProject?> ProjectAsync(Guid id) => projects.GetByIdAsync(id);
    public async Task<IReadOnlyList<WorkflowStep>> ImportStepsAsync(Guid projectId)
    {
        var steps = new List<WorkflowStep>();
        foreach (var workflow in await workflows.GetByProjectAsync(projectId))
            if (workflow.Enabled) steps.AddRange((await workflows.GetStepsAsync(workflow.Id))
                .Where(s => s.Enabled && s.StepType == WorkflowStepTypes.Mapping));
        return steps;
    }
    public async Task<IReadOnlyList<AttributeMapping>> MappingsAsync(Guid stepId) => await workflows.GetMappingsByStepAsync(stepId);
    public async Task<IReadOnlyList<AttributeMapping>> ProjectMappingsAsync(Guid projectId) => await projects.GetMappingsAsync(projectId);
    public Task SaveMappingsAsync(Guid projectId, Guid stepId, IReadOnlyList<AttributeMapping> mappings, MappingSaveCondition condition, CancellationToken ct) =>
        workflows.ReplaceMappingsForStepAsync(projectId, stepId, mappings, condition, ct);
    public async Task<SyncProjectScope?> ScopeAsync(Guid projectId, Guid? stepId) =>
        (stepId.HasValue ? await workflows.GetScopeByStepAsync(stepId.Value) : null)
        ?? await projects.GetProjectScopeAsync(projectId);
}

/// <summary>Shared UI/API owner for source samples and reviewed mapping configuration. Never starts a sync or creates a sink.</summary>
public sealed partial class SourceBrowseService(ISourceBrowseStore store, ConnectorRegistry connectors,
    SourceBrowseHost host, ILogger<SourceBrowseService> logger, ISourceMappingTargetService? targets = null)
{
    public static readonly string[] AdClasses = ["User", "Group", "Computer", "Contact", "organizationalUnit"];
    public static bool Supports(string type) => type.Equals("CSV", StringComparison.OrdinalIgnoreCase) || type.Equals("ActiveDirectory", StringComparison.OrdinalIgnoreCase);
    public static string? UnavailableReason(string type, bool active = true) => !active
        ? "This source connection is inactive. Enable it in Connections before browsing."
        : Supports(type) ? null : $"Source browsing is not available for {type} yet. Supported sources: Active Directory and CSV.";

    public async Task<SourceBrowseContext> DescribeAsync(ClaimsPrincipal user, SourceBrowseSelection selection, CancellationToken ct, Guid? expectedInstanceId = null)
    {
        RequireInstance(expectedInstanceId);
        return (await ResolveAsync(user, selection, ct)).Context;
    }

    private void RequireInstance(Guid? expected)
    {
        if (expected.HasValue && expected != host.InstanceId)
            throw new SourceBrowseException("WrongExecutionHost", "The selected source belongs to a different Conduit instance. Refresh the source catalog; no source was read.");
    }

    public async Task<SourceBrowsePage> ReadAsync(ClaimsPrincipal user, SourceBrowseRequest request, CancellationToken ct)
    {
        SourceBrowseLimits.Validate(request.Query);
        RequireInstance(request.ExpectedInstanceId);
        var mappings = request.Query.Mappings == null ? null : SourceMappingPreview.Snapshot(request.Query.Mappings);
        SourceMappingPreviewAdapter.Validate(mappings);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SourceBrowseLimits.Timeout);
        try
        {
            var resolved = await ResolveAsync(user, request.Selection, timeout.Token);
            if (!resolved.Context.CanBrowse) throw new SourceBrowseException("SelectImportStep", resolved.Context.Reason ?? "Choose a supported import step.");
            var scope = resolved.Scope;
            var schema = resolved.Context.Mapping?.Edit?.CanSave == true ? resolved.Context.Mapping.Edit.Schema : null;
            if (mappings != null && schema != null) SourceMappingEdit.Validate(mappings, schema, true);
            var objectClass = resolved.Context.ObjectClass;
            if (!request.Selection.ProjectId.HasValue && resolved.Context.CanEditScope)
            {
                objectClass = string.IsNullOrWhiteSpace(request.Query.ObjectClass) ? "User" : request.Query.ObjectClass;
                if (!AdClasses.Contains(objectClass, StringComparer.OrdinalIgnoreCase))
                    throw new SourceBrowseException("InvalidClass", "Choose one of the available object classes.");
                scope.BaseDN = string.IsNullOrWhiteSpace(request.Query.BaseDn) ? null : request.Query.BaseDn.Trim();
                // A custom filter narrows the selected class, rather than relabeling unrelated objects.
                scope.LdapFilter = SourceBrowseScope.ClassFilter(objectClass, request.Query.Filter);
            }
            else if (request.Selection.ProjectId.HasValue && (!string.IsNullOrWhiteSpace(request.Query.BaseDn) || !string.IsNullOrWhiteSpace(request.Query.Filter)))
                throw new SourceBrowseException("SavedScopeRequired", "Project browsing uses the saved import scope. Edit and save the project to change it.");
            if (scope.MaxObjects is <= 0) throw new SourceBrowseException("InvalidScope", "The saved maximum object count must be positive.");
            var sampleLimit = Math.Min(request.Query.Limit, scope.MaxObjects ?? request.Query.Limit);
            var savedCap = scope.MaxObjects;
            scope.MaxObjects = Math.Min(sampleLimit + 1, savedCap ?? int.MaxValue);
            scope.PageSize = Math.Min(scope.PageSize > 0 ? scope.PageSize : 100, 200);
            using var credentials = CredentialNameContext.Push(resolved.CredentialName, null);
            var source = connectors.Require(resolved.Connection.SystemType).CreateSource(resolved.Connection.Id)
                ?? throw new SourceBrowseException("UnsupportedReader", "This connection has no source reader.");
            var enumeration = await source.EnumerateAsync(objectClass, scope, null, timeout.Token);
            var rows = new List<SourceBrowseRow>();
            var hasMore = false;
            await foreach (var item in enumeration.Objects.WithCancellation(timeout.Token))
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (rows.Count == sampleLimit) { hasMore = true; break; }
                var row = SourceBrowseSample.Row(rows.Count + 1, item.Attributes);
                rows.Add(mappings == null ? row : row with { MappedFields = SourceMappingPreviewAdapter.Project(item.Attributes, mappings, schema) });
            }
            timeout.Token.ThrowIfCancellationRequested();
            if (resolved.Connection.SystemType.Equals("ActiveDirectory", StringComparison.OrdinalIgnoreCase) &&
                !hasMore && !(savedCap.HasValue && rows.Count == savedCap.Value) && !enumeration.WasCompleteRead())
                throw new SourceBrowseException("IncompleteRead", "The directory did not confirm a complete sample read. Retry; partial results were discarded.");
            var context = resolved.Context with { ObjectClass = objectClass, Scope = DescribeScope(scope, objectClass, resolved.CredentialName, savedCap, resolved.Connection.SystemType.Equals("CSV", StringComparison.OrdinalIgnoreCase)) };
            return new(context, rows, hasMore, DateTimeOffset.UtcNow,
                mappings == null ? "Source sample before mappings; no sync, scripts or writes were run. Saved project record limits are respected." : schema != null ? "Draft mappings checked against supported target fields and this sample. No sync or scripts were run; changes are not saved yet. This is not full-population or write-permission validation." : "Trial field mappings evaluated on a fresh source sample. Target validation is unavailable. No sync, scripts or writes were run; changes are not saved.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new SourceBrowseException("TimedOut", "The source read timed out. Narrow the scope and retry."); }
        catch (OperationCanceledException) { throw; }
        catch (SourceBrowseException) { throw; }
        catch (Conduit.Readers.ActiveDirectory.AdReadException ex) { throw new SourceBrowseException(ex.Code, ex.Message); }
        catch (Exception ex)
        {
            logger.LogWarning("Source browse failed ({ErrorType}); no partial sample returned", ex.GetType().Name);
            throw new SourceBrowseException("SourceReadFailed", "Could not read source data. Check the saved connection, credentials and scope on this Conduit host.");
        }
    }

    private async Task<Resolved> ResolveAsync(ClaimsPrincipal user, SourceBrowseSelection selection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireAuthenticated(user);
        var project = selection.ProjectId.HasValue ? await store.ProjectAsync(selection.ProjectId.Value)
            ?? throw new SourceBrowseException("ProjectUnavailable", "The selected project is unavailable.") : null;
        var sourceId = project?.SourceTenantId ?? selection.ConnectionId;
        if (sourceId == Guid.Empty) throw new SourceBrowseException("ConnectionRequired", "Choose a source connection to browse.");
        AuthorizeSource(user, sourceId);
        if (project != null && selection.ConnectionId != Guid.Empty && selection.ConnectionId != sourceId)
            throw new SourceBrowseException("SourceMismatch", "The selected connection is not this project's source.");
        if (project == null && selection.StepId.HasValue)
            throw new SourceBrowseException("ProjectRequired", "Choose a project before selecting an import step.");
        var connection = await store.ConnectionAsync(sourceId)
            ?? throw new SourceBrowseException("ConnectionUnavailable", "The selected source connection is unavailable.");
        ct.ThrowIfCancellationRequested();
        if (UnavailableReason(connection.SystemType, connection.IsActive) is { } unavailable)
            throw new SourceBrowseException(connection.IsActive ? "UnsupportedReader" : "InactiveConnection", $"{connection.Name}: {unavailable}");
        var steps = project == null ? [] : await store.ImportStepsAsync(project.Id);
        var step = selection.StepId.HasValue ? steps.SingleOrDefault(s => s.Id == selection.StepId.Value)
            ?? throw new SourceBrowseException("StepUnavailable", "This step is not an enabled import step in the selected project.") : null;
        // Never quietly use project defaults when a workflow exists. Let the operator choose the import step.
        var canBrowse = project == null || step != null;
        var scope = project == null ? new SyncProjectScope() : await store.ScopeAsync(project.Id, step?.Id) ?? new SyncProjectScope();
        SourceBrowseScope.ValidateLists(scope.IncludedBaseDNs, scope.ExcludedBaseDNs);
        if (scope.IncludeDeleted) throw new SourceBrowseException("UnsupportedScope", "Deleted-object scope is not supported by the source browser.");
        if (!string.IsNullOrWhiteSpace(scope.QueryExpression)) throw new SourceBrowseException("UnsupportedScope", "This project has a query expression that this source browser cannot apply.");
        var objectClass = !string.IsNullOrWhiteSpace(step?.ObjectClass) ? step.ObjectClass : project?.ObjectClass ?? "User";
        var savedMappings = step == null ? [] : await store.MappingsAsync(step.Id);
        var inheritedMappings = step != null && savedMappings.Count == 0
            ? await store.ProjectMappingsAsync(project!.Id) : Array.Empty<AttributeMapping>();
        var targetConnection = step == null ? null : await store.ConnectionAsync(project!.SinkTenantId);
        var effective = step == null ? new EffectiveMappingSet([], EffectiveMappingOrigin.Step) :
            EffectiveMappingResolver.Resolve(savedMappings, inheritedMappings, connection.SystemType,
                targetConnection?.SystemType ?? "", objectClass!);
        var mapping = SourceMappingPreviewAdapter.Context(effective.Mappings);
        if (effective.DroppedAttributes is { Count: > 0 } dropped)
            mapping = mapping with { Help = mapping.Help + $" Template defaults omit {dropped.Count} source fields with no matching target field." };
        if (effective.Origin != EffectiveMappingOrigin.Step)
            mapping = mapping with
            {
                // Inherited/default rules become NEW step rows on Save. Project-row
                // primary keys must never be reused or treated as editable step IDs.
                SavedRules = mapping.SavedRules.Select(r => r with { Id = null }).ToArray(),
                Help = mapping.Help + (effective.Origin == EffectiveMappingOrigin.Project
                    ? " This step inherits the project mappings. Saving here creates a step override and leaves project mappings unchanged."
                    : " This step uses template defaults because neither step nor project mappings are saved. Saving here creates a step override.")
            };
        if (step != null)
        {
            SourceMappingSchema schema = new("Target fields unavailable", [], "");
            string? reason = null;
            try
            {
                RequireMappingAdministrator(user);
                if (targets == null) throw new SourceBrowseException("TargetSchemaUnavailable", "Mapping target discovery is unavailable on this host.");
                var target = targetConnection ?? throw new SourceBrowseException("TargetUnavailable", "The target connection is unavailable.");
                schema = await targets.ReadAsync(target, project, step, ct);
                if (mapping.Notice != null) reason = "The preview omits saved fields. Use the project editor to preserve the complete mapping set.";
            }
            catch (SourceBrowseException ex) { reason = ex.Message; }
            mapping = mapping with { Edit = new(MappingSaveCondition.Version(project!, step, savedMappings, scope, inheritedMappings), schema, reason == null, reason) };
        }
        var context = new SourceBrowseContext
        {
            Mapping = mapping,
            ConnectionName = connection.Name, ExecutionLocation = $"Conduit · {host.Name}", ExecutionInstanceId = host.InstanceId,
            ProjectName = project?.Name, StepId = step?.Id, ObjectClass = objectClass!, ObjectClasses = AdClasses,
            CanEditScope = project == null && connection.SystemType.Equals("ActiveDirectory", StringComparison.OrdinalIgnoreCase),
            CanBrowse = canBrowse, Reason = canBrowse ? null : steps.Count == 0 ? "This project has no enabled import steps to browse." : "Choose the saved import step to browse its source scope.",
            Steps = steps.Select(s => new SourceBrowseStep(s.Id,
                $"{(string.IsNullOrWhiteSpace(s.ObjectClass) ? project?.ObjectClass ?? "User" : s.ObjectClass)} - {s.Name}")).ToArray(),
            Scope = DescribeScope(scope, objectClass!, project?.SourceCredentialName, scope.MaxObjects, connection.SystemType.Equals("CSV", StringComparison.OrdinalIgnoreCase))
        };
        // The store returns detached values; clone scope so neither runtime configuration nor test stores are mutated.
        return new(connection, context, JsonSerializer.Deserialize<SyncProjectScope>(JsonSerializer.Serialize(scope))!, project?.SourceCredentialName, project, step, savedMappings);
    }

    public static void RequireAuthenticated(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) throw new SourceBrowseException("AuthenticationRequired", "Sign in before browsing source data.");
        if (user.Identity.AuthenticationType != "ApiToken" && !user.IsInRole("Admin"))
            throw new SourceBrowseException("AdministratorRequired", "Administrator access is required to browse source data.");
    }
    public static void AuthorizeSource(ClaimsPrincipal user, Guid sourceId)
    {
        RequireAuthenticated(user);
        if (user.Identity!.AuthenticationType != "ApiToken") return;
        var scope = user.FindFirst("TokenScope")?.Value;
        if (scope == "Admin") return;
        if (scope != "Tenant" || !Guid.TryParse(user.FindFirst("TenantId")?.Value, out var tenantId) || tenantId != sourceId)
            throw new SourceBrowseException("SourceAccessDenied", "This token cannot browse the project's source connection. Sink access does not grant source access.");
    }
    private static IReadOnlyList<SourceBrowseField> DescribeScope(SyncProjectScope scope, string objectClass, string? credential, int? cap, bool csv) => csv
        ? [new("File", "Saved CSV file on this Conduit execution host"), new("Rows", "Before mappings and scripts"),
           new("Source credential", credential ?? "Connection default"), new("Project maximum", cap?.ToString() ?? "No project cap")]
        :
    [new("Object class", objectClass), new("Included containers", string.Join("; ", scope.GetIncludedBaseList()) is { Length: > 0 } includes ? includes : "Connection default"),
     new("Excluded containers", string.Join("; ", scope.GetExcludedBaseList())), new("LDAP filter", scope.LdapFilter ?? "Object class default"),
     new("Source credential", credential ?? "Connection default"), new("Project maximum", cap?.ToString() ?? "No project cap")];
    private sealed record Resolved(Tenant Connection, SourceBrowseContext Context, SyncProjectScope Scope, string? CredentialName, SyncProject? Project, WorkflowStep? Step, IReadOnlyList<AttributeMapping> Mappings);
}
