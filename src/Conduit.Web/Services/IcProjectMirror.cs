using Conduit.Core.Models;
using Conduit.Core.SyncModels;
using Conduit.DataAccess.Repositories;

namespace Conduit.Web.Services;

/// <summary>
/// The rows a local project needs to mirror one IdentityCenter definition (ZERO-TOUCH-01): IdentityCenter's
/// project → workflow → step (with its scope) → mapping graph, in Conduit's shapes. Pure: the same transform the
/// parity import proved column by column, now the one place both callers map through.
/// </summary>
public static class IcProjectGraph
{
    public sealed record StepRows(WorkflowStep Step, SyncProjectScope Scope, IReadOnlyList<AttributeMapping> Mappings);
    public sealed record WorkflowRows(Workflow Workflow, IReadOnlyList<StepRows> Steps);

    /// <summary>IC StepType → Conduit StepType. IC and Conduit use the SAME canonical names for the parity-relevant types (Upsert is IC's Mapping equivalent).</summary>
    public static string MapStepType(string? icStepType) => (icStepType ?? "Mapping") switch
    {
        "Upsert" => WorkflowStepTypes.Mapping,   // IC "Upsert" == Conduit "Mapping" (the source→sink loop)
        "Mapping" => WorkflowStepTypes.Mapping,
        "Lookup" => WorkflowStepTypes.Lookup,
        "GroupMembership" => WorkflowStepTypes.GroupMembership,
        "LicenseSync" => WorkflowStepTypes.LicenseSync,
        "SignInLogSync" => WorkflowStepTypes.SignInLogSync,
        "UsageReportSync" => WorkflowStepTypes.UsageReportSync,
        "AppRoleSync" => WorkflowStepTypes.AppRoleSync,
        var other => other   // preserve any IC type verbatim; the orchestrator run-skips unknowns
    };

    /// <summary>Conduit has ONE TransformExpr column: an explicit expression, else the named transform, else a plain passthrough (null).</summary>
    public static string? ComposeTransform(string? type, string? expression)
    {
        if (!string.IsNullOrWhiteSpace(expression)) return expression;
        if (string.IsNullOrWhiteSpace(type) || string.Equals(type, "Direct", StringComparison.OrdinalIgnoreCase))
            return null;
        return type;
    }

    /// <summary>The object class the project header carries: its first workflow's, lower case, else "user".</summary>
    public static string ProjectObjectClass(IcProjectDefinition definition) =>
        definition.Workflows.OrderBy(w => w.ExecutionOrder).Select(w => w.ObjectClass).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.ToLowerInvariant() ?? "user";

    /// <summary>The workflow tree for <paramref name="projectId"/>, fresh ids throughout.</summary>
    public static IReadOnlyList<WorkflowRows> Build(Guid projectId, IcProjectDefinition definition)
    {
        var result = new List<WorkflowRows>();
        var ordinal = 0;
        foreach (var icw in definition.Workflows.OrderBy(w => w.ExecutionOrder))
        {
            var workflow = new Workflow
            {
                Id = Guid.NewGuid(), SyncProjectId = projectId, Name = icw.Name, Description = icw.Description, Ordinal = ordinal++, Enabled = icw.IsEnabled,
            };
            var steps = new List<StepRows>();
            var stepOrdinal = 0;
            foreach (var ics in icw.Steps.OrderBy(s => s.ExecutionOrder))
            {
                var step = new WorkflowStep
                {
                    Id = Guid.NewGuid(), WorkflowId = workflow.Id, Name = ics.Name, StepType = MapStepType(ics.StepType),
                    ObjectClass = string.IsNullOrWhiteSpace(ics.ObjectClass) ? null : ics.ObjectClass, Ordinal = stepOrdinal++, Enabled = ics.IsEnabled,
                };
                // IC stores filter/bases/page size as columns on the STEP; Conduit stores them in a per-step scope row.
                var scope = new SyncProjectScope
                {
                    Id = Guid.NewGuid(), SyncProjectId = projectId, WorkflowStepId = step.Id, LdapFilter = ics.LdapFilter,
                    BaseDN = ics.SearchBase ?? ics.SearchBases.FirstOrDefault(), PageSize = ics.LdapPageSize > 0 ? ics.LdapPageSize : 1000,
                };
                scope.SetIncludedBaseList(ics.SearchBases);
                scope.SetExcludedBaseList(ics.ExcludedSearchBases);
                var mappings = ics.Mappings.OrderBy(m => m.ExecutionOrder).Select(m => new AttributeMapping
                {
                    Id = Guid.NewGuid(), SyncProjectId = projectId, WorkflowStepId = step.Id, SourceAttribute = m.SourceAttribute, SinkAttribute = m.TargetAttribute,
                    TransformExpr = ComposeTransform(m.TransformationType, m.TransformationExpression), IsRequired = m.IsRequired, SortOrder = m.ExecutionOrder,
                }).ToList();
                steps.Add(new StepRows(step, scope, mappings));
            }
            result.Add(new WorkflowRows(workflow, steps));
        }
        return result;
    }
}

/// <summary>What the mirror reads and writes; the repositories behind it are concrete, so the decisions are tested against this seam.</summary>
public interface IIcProjectMirrorStore
{
    /// <summary>The active connected system an IdentityCenter connection of this name is served from: by name, else by domain hint; never an IdentityCenter sink.</summary>
    Task<Tenant?> FindConnectedSystemAsync(string connectionName);
    /// <summary>The IdentityCenter connected system whose credential points at <paramref name="baseUrl"/>: the mirror's sink.</summary>
    Task<Tenant?> FindIdentityCenterSystemAsync(string baseUrl);
    Task<SyncProject> CreateProjectAsync(SyncProject project);
    Task<bool> UpdateProjectAsync(SyncProject project);
    /// <summary>Bind the project to its IdentityCenter project and record the fingerprint; false when another project holds the id.</summary>
    Task<bool> SetMirrorAsync(Guid projectId, Guid identityCenterProjectId, string fingerprint);
    /// <summary>Every workflow of the project goes (steps, scopes, mappings with it) and these take their place.</summary>
    Task ReplaceWorkflowsAsync(Guid projectId, IReadOnlyList<IcProjectGraph.WorkflowRows> workflows);
    /// <summary>MIRROR-OVERLAP-01: a project on this Conduit that <see cref="IcProjectMirror.Overlaps"/> the mirror, or null.</summary>
    Task<SyncProject?> FindOverlappingProjectAsync(Guid sourceTenantId, Guid sinkTenantId, string? objectClass, Guid? exceptProjectId);
}

public sealed class IcProjectMirrorStore : IIcProjectMirrorStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IcEndpointDirectory _endpoints;

    public IcProjectMirrorStore(IServiceScopeFactory scopeFactory, IcEndpointDirectory endpoints)
    {
        _scopeFactory = scopeFactory;
        _endpoints = endpoints;
    }

    public async Task<Tenant?> FindConnectedSystemAsync(string connectionName)
    {
        using var scope = _scopeFactory.CreateScope();
        var tenants = await scope.ServiceProvider.GetRequiredService<TenantRepository>().GetAllAsync();
        var candidates = tenants.Where(t => t.IsActive && !string.Equals(t.SystemType, IcEndpointDirectory.IdentityCenterSystemType, StringComparison.OrdinalIgnoreCase)).ToList();
        return candidates.FirstOrDefault(t => string.Equals(t.Name?.Trim(), connectionName, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(t => string.Equals(t.Domain?.Trim(), connectionName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Tenant?> FindIdentityCenterSystemAsync(string baseUrl)
    {
        // The directory is the one reader of which connected system is which IdentityCenter.
        var tenantId = await _endpoints.FindTenantIdAsync(baseUrl);
        if (tenantId is null) return null;
        using var scope = _scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TenantRepository>().GetByIdAsync(tenantId.Value);
    }

    public async Task<SyncProject?> FindOverlappingProjectAsync(Guid sourceTenantId, Guid sinkTenantId, string? objectClass, Guid? exceptProjectId)
    {
        using var scope = _scopeFactory.CreateScope();
        var projects = await scope.ServiceProvider.GetRequiredService<SyncProjectRepository>().GetAllAsync();
        return projects
            .Where(p => IcProjectMirror.Overlaps(p, sourceTenantId, sinkTenantId, objectClass, exceptProjectId))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public async Task<SyncProject> CreateProjectAsync(SyncProject project)
    {
        using var scope = _scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SyncProjectRepository>().CreateAsync(project);
    }

    public async Task<bool> UpdateProjectAsync(SyncProject project)
    {
        using var scope = _scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SyncProjectRepository>().UpdateAsync(project);
    }

    public async Task<bool> SetMirrorAsync(Guid projectId, Guid identityCenterProjectId, string fingerprint)
    {
        using var scope = _scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SyncProjectRepository>().SetIdentityCenterMirrorAsync(projectId, identityCenterProjectId, fingerprint);
    }

    public async Task ReplaceWorkflowsAsync(Guid projectId, IReadOnlyList<IcProjectGraph.WorkflowRows> workflows)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<WorkflowRepository>();
        foreach (var existing in await repo.GetByProjectAsync(projectId))
            await repo.DeleteAsync(existing.Id);
        foreach (var rows in workflows)
        {
            await repo.CreateAsync(rows.Workflow);
            foreach (var step in rows.Steps)
            {
                await repo.CreateStepAsync(step.Step);
                await repo.UpsertScopeForStepAsync(projectId, step.Step.Id, step.Scope);
                if (step.Mappings.Count > 0)
                    await repo.ReplaceMappingsForStepAsync(projectId, step.Step.Id, step.Mappings);
            }
        }
    }
}

/// <summary>What the mirror did for one claim: the project to run, or the reason nothing can.</summary>
public sealed record IcProjectMirrorOutcome(SyncProject? Project, string? Refusal, string Action)
{
    public const string Created = "Created";
    public const string Refreshed = "Refreshed";
    public const string Unchanged = "Unchanged";
    /// <summary>A project bound by hand (no fingerprint): run as it is, never rewritten.</summary>
    public const string HandBound = "HandBound";
    public static IcProjectMirrorOutcome Refused(string reason) => new(null, reason, "Refused");
}

/// <summary>
/// ZERO-TOUCH-01 (UC038-D01, Jacob 2026-11-09: "we make a key in identity center for conduit, make the connection,
/// and that's it"): the IdentityCenter project is the one definition. The installation that claims a run reads the
/// definition and keeps a local mirror bound to it: created on the first claim, rebuilt when IdentityCenter's
/// fingerprint changes, left alone otherwise. Its source is the connected system the IdentityCenter connection
/// is named after; its sink is the IdentityCenter the job came from. A project bound by hand is run as it is.
/// </summary>
public interface IIcProjectMirror
{
    Task<IcProjectMirrorOutcome> EnsureAsync(string baseUrl, IcProjectDefinition definition, SyncProject? existing, CancellationToken ct);
}

public sealed class IcProjectMirror : IIcProjectMirror
{
    private readonly IIcProjectMirrorStore _store;
    private readonly ILogger<IcProjectMirror> _logger;

    public IcProjectMirror(IIcProjectMirrorStore store, ILogger<IcProjectMirror> logger)
    {
        _store = store;
        _logger = logger;
    }

    public const string DefinedInIdentityCenter = "Defined in IdentityCenter";

    public static string NoSourceConnectionReason(IcProjectDefinition definition) =>
        $"IdentityCenter project '{definition.Name}' ({definition.ProjectId}) names no source connection; nothing can be read for it here.";
    public static string NoConnectedSystemReason(IcProjectDefinition definition, string connectionName) =>
        $"IdentityCenter project '{definition.Name}' reads connection '{connectionName}', and this Conduit has no active connected system of that name; add the connected system here (its directory and credentials) and the next run mirrors the project.";
    public static string NoIdentityCenterSystemReason(string baseUrl) =>
        $"This Conduit has no IdentityCenter connected system pointing at {baseUrl} to write to; the enrollment that created the connection was removed.";
    public static string BindingHeldReason(IcProjectDefinition definition) =>
        $"IdentityCenter project '{definition.Name}' ({definition.ProjectId}) is already bound to another project on this Conduit; unbind it there first.";
    /// <summary>MIRROR-OVERLAP-01 (UC038-F07): the refusal that names the other project and how to clear it.</summary>
    public static string OverlapReason(IcProjectDefinition definition, SyncProject other) =>
        $"IdentityCenter project '{definition.Name}' was not run: Conduit project '{other.Name}' ({other.Id}) is enabled and syncs the same source into the same IdentityCenter for the same objects, so the two would write over each other. Disable it in Schedules (or delete it) and the next run goes ahead.";

    /// <summary>
    /// MIRROR-OVERLAP-01 (UC038-F07): <paramref name="candidate"/> would write the mirror's objects too: it is not itself a
    /// mirror (IdentityCenter coordinates its own projects), it is enabled (a paused project is the operator's way out), and
    /// it reads the same connected system into the same IdentityCenter for an overlapping object class (none means any).
    /// </summary>
    public static bool Overlaps(SyncProject candidate, Guid sourceTenantId, Guid sinkTenantId, string? objectClass, Guid? exceptProjectId) =>
        candidate.Id != exceptProjectId
        && string.IsNullOrEmpty(candidate.IdentityCenterDefinitionFingerprint)
        && candidate.IsEnabled
        && candidate.SourceTenantId == sourceTenantId
        && candidate.SinkTenantId == sinkTenantId
        && (string.IsNullOrWhiteSpace(candidate.ObjectClass) || string.IsNullOrWhiteSpace(objectClass)
            || string.Equals(candidate.ObjectClass.Trim(), objectClass.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string RunningReason(SyncProject project) =>
        $"Conduit project '{project.Name}' ({project.Id}) is running, so its definition was not rebuilt; the next claim rebuilds it.";

    public async Task<IcProjectMirrorOutcome> EnsureAsync(string baseUrl, IcProjectDefinition definition, SyncProject? existing, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (existing is not null)
        {
            // Bound by hand (SYNC-SERVICE-05's API): the operator's project, never rewritten by a definition.
            if (string.IsNullOrEmpty(existing.IdentityCenterDefinitionFingerprint))
                return new IcProjectMirrorOutcome(existing, null, IcProjectMirrorOutcome.HandBound);
            var rival = await _store.FindOverlappingProjectAsync(existing.SourceTenantId, existing.SinkTenantId,
                IcProjectGraph.ProjectObjectClass(definition), existing.Id);
            if (rival is not null)
                return IcProjectMirrorOutcome.Refused(OverlapReason(definition, rival));
            if (string.Equals(existing.IdentityCenterDefinitionFingerprint, definition.Fingerprint, StringComparison.Ordinal))
                return new IcProjectMirrorOutcome(existing, null, IcProjectMirrorOutcome.Unchanged);
            if (existing.IsRunning)
            {
                // The pump refuses a running project by name anyway; the mirror only says why nothing changed.
                _logger.LogInformation("{Reason}", RunningReason(existing));
                return new IcProjectMirrorOutcome(existing, null, IcProjectMirrorOutcome.Unchanged);
            }
            ct.ThrowIfCancellationRequested();
            existing.Name = definition.Name;
            existing.Description = Describe(definition);
            existing.ObjectClass = IcProjectGraph.ProjectObjectClass(definition);
            await _store.UpdateProjectAsync(existing);
            await _store.ReplaceWorkflowsAsync(existing.Id, IcProjectGraph.Build(existing.Id, definition));
            if (!await _store.SetMirrorAsync(existing.Id, definition.ProjectId, definition.Fingerprint))
                return IcProjectMirrorOutcome.Refused(BindingHeldReason(definition));
            existing.IdentityCenterDefinitionFingerprint = definition.Fingerprint;
            _logger.LogInformation("Rebuilt Conduit project '{Name}' ({ProjectId}) from IdentityCenter definition {Fingerprint}", existing.Name, existing.Id, definition.Fingerprint);
            return new IcProjectMirrorOutcome(existing, null, IcProjectMirrorOutcome.Refreshed);
        }

        if (definition.SourceConnection is null || string.IsNullOrWhiteSpace(definition.SourceConnection.Name))
            return IcProjectMirrorOutcome.Refused(NoSourceConnectionReason(definition));
        var source = await _store.FindConnectedSystemAsync(definition.SourceConnection.Name.Trim());
        if (source is null)
            return IcProjectMirrorOutcome.Refused(NoConnectedSystemReason(definition, definition.SourceConnection.Name.Trim()));
        var sink = await _store.FindIdentityCenterSystemAsync(baseUrl);
        if (sink is null)
            return IcProjectMirrorOutcome.Refused(NoIdentityCenterSystemReason(baseUrl));
        var overlapping = await _store.FindOverlappingProjectAsync(source.Id, sink.Id, IcProjectGraph.ProjectObjectClass(definition), null);
        if (overlapping is not null)
            return IcProjectMirrorOutcome.Refused(OverlapReason(definition, overlapping));
        ct.ThrowIfCancellationRequested();

        var project = new SyncProject
        {
            Id = Guid.NewGuid(),
            Name = definition.Name,
            Description = Describe(definition),
            SourceTenantId = source.Id,
            SinkTenantId = sink.Id,
            ObjectClass = IcProjectGraph.ProjectObjectClass(definition),
            IsEnabled = true,
            SkipUnchanged = true,
            CronSchedule = null,   // IdentityCenter schedules it; the mirror never runs on its own clock
        };
        await _store.CreateProjectAsync(project);
        await _store.ReplaceWorkflowsAsync(project.Id, IcProjectGraph.Build(project.Id, definition));
        if (!await _store.SetMirrorAsync(project.Id, definition.ProjectId, definition.Fingerprint))
            return IcProjectMirrorOutcome.Refused(BindingHeldReason(definition));
        project.IdentityCenterProjectId = definition.ProjectId;
        project.IdentityCenterDefinitionFingerprint = definition.Fingerprint;
        _logger.LogInformation("Created Conduit project '{Name}' ({ProjectId}) as the mirror of IdentityCenter project {IcProjectId} (source '{Source}', sink '{Sink}')",
            project.Name, project.Id, definition.ProjectId, source.Name, sink.Name);
        return new IcProjectMirrorOutcome(project, null, IcProjectMirrorOutcome.Created);
    }

    /// <summary>The description every mirrored project carries, so the list says where it is edited.</summary>
    public static string Describe(IcProjectDefinition definition) =>
        string.IsNullOrWhiteSpace(definition.Description) ? DefinedInIdentityCenter : $"{DefinedInIdentityCenter}. {definition.Description.Trim()}";
}
