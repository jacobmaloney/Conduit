using System;
using System.Collections.Generic;
using System.Linq;
using Conduit.Core.SyncModels;

namespace Conduit.Sync.Templates;

/// <summary>
/// Phase 8. Pre-built workflow trees the operator can pick when creating a
/// sync project. The catalog is intentionally static + in-code (not a DB
/// table) — adding a template is a one-file edit and a rebuild. Each template
/// declares its applicable shape (source / sink SystemType hints) for the UI
/// to grey-out incompatible options, plus the workflow + step tree to lower
/// into Conduit's V17 schema after the project is saved.
///
/// Templates are advisory. The operator can swap source / sink / object class
/// AFTER picking a template; we don't lock anything down. The Mapping step in
/// each tree leaves AttributeMappings empty by default — the orchestrator's
/// passthrough behavior handles that. Operators can fill in mappings later
/// via the Workflows tab's per-step Mapping editor.
/// </summary>
public static class WorkflowTemplateCatalog
{
    /// <summary>Stable template id. UI sends this string back when the operator picks one.</summary>
    public const string EmptyId = "empty";
    public const string AdUserProvisioningId = "ad-user-provisioning";
    public const string EntraIdUserSyncId = "entraid-user-sync";
    public const string ReadOnlySyncId = "readonly-sync";
    public const string GroupMembershipReconcileId = "group-membership-reconcile";

    public static IReadOnlyList<WorkflowTemplate> All { get; } = new[]
    {
        new WorkflowTemplate
        {
            Id = EmptyId,
            DisplayName = "Empty (custom)",
            Description = "Start blank. Build the workflow tree yourself in the Workflows tab.",
            SuggestedObjectClass = "User",
            // Empty templates produce no tree — operator builds it manually.
            Workflows = Array.Empty<TemplateWorkflow>()
        },

        new WorkflowTemplate
        {
            Id = AdUserProvisioningId,
            DisplayName = "AD User Provisioning",
            Description = "Pull users from a source, match against existing IC identities, create on miss, then stamp manager links. The full IC-style lifecycle pipeline.",
            SuggestedObjectClass = "User",
            // Applies wherever the sink supports person-aware ops. IC is the
            // canonical target today; AD/Entra also support AssignManager.
            ApplicableSinkTypes = new[] { "IdentityCenter", "ActiveDirectory", "EntraID" },
            Workflows = new[]
            {
                new TemplateWorkflow
                {
                    Name = "User Lifecycle",
                    Description = "Source → Mapping → PersonMatch → PersonCreate → AssignManager",
                    Steps = new[]
                    {
                        new TemplateStep { Name = "Pull users",          StepType = WorkflowStepTypes.Mapping },
                        new TemplateStep { Name = "Match person",        StepType = WorkflowStepTypes.PersonMatch },
                        new TemplateStep { Name = "Create missing",      StepType = WorkflowStepTypes.PersonCreate },
                        new TemplateStep { Name = "Assign manager",      StepType = WorkflowStepTypes.AssignManager }
                    }
                }
            }
        },

        new WorkflowTemplate
        {
            Id = EntraIdUserSyncId,
            DisplayName = "EntraID User Sync",
            Description = "Pull users from EntraID and stamp manager links on the sink. Skips PersonMatch/Create — assumes the sink already has matching identities (e.g. AD as authoritative).",
            SuggestedObjectClass = "User",
            ApplicableSourceTypes = new[] { "EntraID" },
            Workflows = new[]
            {
                new TemplateWorkflow
                {
                    Name = "Entra Pull",
                    Description = "Mapping → AssignManager",
                    Steps = new[]
                    {
                        new TemplateStep { Name = "Pull users",          StepType = WorkflowStepTypes.Mapping },
                        new TemplateStep { Name = "Assign manager",      StepType = WorkflowStepTypes.AssignManager }
                    }
                }
            }
        },

        new WorkflowTemplate
        {
            Id = ReadOnlySyncId,
            DisplayName = "Read-only Sync",
            Description = "Single Mapping step. Pull objects from the source and write to the sink — no person-matching, no manager stamping. The canonical 'I just want to see what's in this system' template.",
            SuggestedObjectClass = "User",
            Workflows = new[]
            {
                new TemplateWorkflow
                {
                    Name = "Default",
                    Description = "Mapping only",
                    Steps = new[]
                    {
                        new TemplateStep { Name = "Pull and write",      StepType = WorkflowStepTypes.Mapping }
                    }
                }
            }
        },

        new WorkflowTemplate
        {
            Id = GroupMembershipReconcileId,
            DisplayName = "Group Membership Reconcile",
            Description = "Full-replace group memberships. Mapping step for the Group object class — sinks that interpret 'members' attribute (AD, Entra, Emulator) will diff and apply.",
            SuggestedObjectClass = "Group",
            Workflows = new[]
            {
                new TemplateWorkflow
                {
                    Name = "Group Reconcile",
                    Description = "Mapping step for Group object class.",
                    Steps = new[]
                    {
                        new TemplateStep { Name = "Reconcile members",   StepType = WorkflowStepTypes.Mapping }
                    }
                }
            }
        }
    };

    /// <summary>Lookup by id. Falls back to Empty when unknown.</summary>
    public static WorkflowTemplate GetById(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? All.First(t => t.Id == EmptyId);

    /// <summary>
    /// Lowers a template into Workflow + WorkflowStep entities for the given
    /// project. Ordinals are assigned 0..N in the template's declared order.
    /// Caller persists via <see cref="DataAccess.Repositories.WorkflowRepository"/>.
    /// </summary>
    public static (List<Workflow> Workflows, Dictionary<Guid, List<WorkflowStep>> StepsByWorkflow)
        Materialize(WorkflowTemplate template, Guid syncProjectId)
    {
        var workflows = new List<Workflow>();
        var steps = new Dictionary<Guid, List<WorkflowStep>>();
        for (int i = 0; i < template.Workflows.Count; i++)
        {
            var tw = template.Workflows[i];
            var wf = new Workflow
            {
                Id = Guid.NewGuid(),
                SyncProjectId = syncProjectId,
                Name = tw.Name,
                Description = tw.Description,
                Ordinal = i,
                Enabled = true
            };
            workflows.Add(wf);
            var list = new List<WorkflowStep>();
            for (int j = 0; j < tw.Steps.Count; j++)
            {
                var ts = tw.Steps[j];
                list.Add(new WorkflowStep
                {
                    Id = Guid.NewGuid(),
                    WorkflowId = wf.Id,
                    Name = ts.Name,
                    StepType = ts.StepType,
                    Ordinal = j,
                    Enabled = true
                });
            }
            steps[wf.Id] = list;
        }
        return (workflows, steps);
    }
}

/// <summary>Phase 8. A reusable workflow shape the operator picks at create time.</summary>
public sealed class WorkflowTemplate
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    /// <summary>Pre-fills the project's ObjectClass when the operator picks this template.</summary>
    public string SuggestedObjectClass { get; init; } = "User";
    /// <summary>Optional whitelist of source SystemTypes this template targets. Empty = applies anywhere.</summary>
    public IReadOnlyList<string> ApplicableSourceTypes { get; init; } = Array.Empty<string>();
    /// <summary>Optional whitelist of sink SystemTypes this template targets. Empty = applies anywhere.</summary>
    public IReadOnlyList<string> ApplicableSinkTypes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<TemplateWorkflow> Workflows { get; init; } = Array.Empty<TemplateWorkflow>();
}

public sealed class TemplateWorkflow
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public IReadOnlyList<TemplateStep> Steps { get; init; } = Array.Empty<TemplateStep>();
}

public sealed class TemplateStep
{
    public string Name { get; init; } = string.Empty;
    public string StepType { get; init; } = WorkflowStepTypes.Mapping;
}
