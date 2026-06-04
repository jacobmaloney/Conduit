using System;
using System.Collections.Generic;
using System.Linq;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;

namespace Conduit.Sync.Templates;

/// <summary>
/// Pre-built workflow trees the operator can pick when creating a sync project.
/// The catalog is intentionally static + in-code (not a DB table) — adding a
/// template is a one-file edit and a rebuild.
///
/// CONTEXT-AWARE (ARS Synchronization-Service model). By the Object Class step
/// the operator has already chosen Source + Target connections + Object Class —
/// exactly the triple that determines which templates are LEGAL. Each template
/// self-declares its applicability:
///
///   • <see cref="WorkflowTemplate.AppliesToSourceFamilies"/> — connector families
///     the SOURCE must belong to (wildcard = any).
///   • <see cref="WorkflowTemplate.AppliesToTargetFamilies"/> — connector families
///     the TARGET must belong to (wildcard = any).
///   • <see cref="WorkflowTemplate.RequiresTargetCapability"/> — the write the
///     template needs the TARGET adapter to support (Create / Update / Delete /
///     ReadOnly). Gated against the live <see cref="ConnectorCapabilities"/>.
///   • <see cref="WorkflowTemplate.AppliesToObjectClasses"/> — object classes the
///     template fits (wildcard = any).
///   • <see cref="WorkflowTemplate.Specificity"/> — ranking. Exact family pair beats
///     a one-sided family match beats a wildcard. Drives "Recommended".
///
/// <see cref="Applicable"/> computes the legal set for a triple + the target
/// adapter's capabilities; the wizard HIDES everything else, auto-pre-selects
/// the highest-specificity match as "Recommended", and lists the rest as
/// alternatives. Empty / Custom is always offered as the fallback.
///
/// Content (the <see cref="TemplateWorkflow.Steps"/> tree + the
/// <see cref="WorkflowTemplate.Correlation"/> key pair) is lowered into Conduit's
/// V17 schema after the project is saved. Attribute mappings are NOT duplicated
/// here — the wizard's Auto-Map already pre-fills them from
/// <see cref="AttributeTemplateCatalog"/> in the pair's real attribute names, so a
/// template only declares its STEP shape + correlation and leaves the attribute
/// pre-fill to the existing AttributeMapService path.
/// </summary>
public static class WorkflowTemplateCatalog
{
    /// <summary>Stable template id. UI sends this string back when the operator picks one.</summary>
    public const string EmptyId = "empty";
    public const string AdUserProvisioningId = "ad-user-provisioning";
    public const string EntraIdUserSyncId = "entraid-user-sync";
    public const string ReadOnlySyncId = "readonly-sync";
    public const string GroupMembershipReconcileId = "group-membership-reconcile";
    public const string HrOnboardingId = "hr-onboarding";
    public const string DirectoryInventoryId = "directory-inventory";
    public const string DirectoryBidirectionalId = "directory-bidirectional";
    public const string DeprovisionLeaverId = "deprovision-leaver";

    public static IReadOnlyList<WorkflowTemplate> All { get; } = new[]
    {
        // ── Always-available fallback. Applies to any triple, lowest specificity,
        //    no capability requirement (it produces no tree). ──────────────────
        new WorkflowTemplate
        {
            Id = EmptyId,
            DisplayName = "Empty (custom)",
            Description = "Start blank. Build the workflow tree yourself in the Workflows tab.",
            SuggestedObjectClass = "User",
            AppliesToSourceFamilies = ConnectorFamilies.Any,
            AppliesToTargetFamilies = ConnectorFamilies.Any,
            AppliesToObjectClasses = ObjectClasses.Any,
            RequiresTargetCapability = TargetCapability.None,
            Specificity = 0,
            // Empty templates produce no tree — operator builds it manually.
            Workflows = Array.Empty<TemplateWorkflow>()
        },

        // ── AD → directory (AD / LDS / LDAP / IC) User Provisioning. The full
        //    IC-style lifecycle: pull, match, create-on-miss, stamp manager.
        //    Requires the target to support Create (person-create / provisioning). ─
        new WorkflowTemplate
        {
            Id = AdUserProvisioningId,
            DisplayName = "AD User Provisioning",
            Description = "Pull users from a directory source, match against existing target identities, create on miss, then stamp manager links. The full IC-style lifecycle pipeline.",
            SuggestedObjectClass = "User",
            AppliesToSourceFamilies = new[] { ConnectorFamily.Directory },
            AppliesToTargetFamilies = new[] { ConnectorFamily.Directory, ConnectorFamily.IdentityCenter },
            AppliesToObjectClasses = new[] { "User" },
            RequiresTargetCapability = TargetCapability.Create,
            Specificity = 30,
            Correlation = new TemplateCorrelation { SourceKeyAttr = "userPrincipalName", TargetKeyAttr = "UserPrincipalName" },
            Workflows = new[]
            {
                new TemplateWorkflow
                {
                    Name = "User Lifecycle",
                    Description = "Source → Mapping → PersonMatch → PersonCreate → AssignManager",
                    Steps = new[]
                    {
                        new TemplateStep { Name = "Pull users",     StepType = WorkflowStepTypes.Mapping },
                        new TemplateStep { Name = "Match person",   StepType = WorkflowStepTypes.PersonMatch },
                        new TemplateStep { Name = "Create missing", StepType = WorkflowStepTypes.PersonCreate },
                        new TemplateStep { Name = "Assign manager", StepType = WorkflowStepTypes.AssignManager }
                    }
                }
            }
        },

        // ── AD ↔ Entra ID User Sync. Update-centric — assumes the target already
        //    has matching identities; stamps manager links. Either direction
        //    (both sides directory). Requires Update. ───────────────────────────
        new WorkflowTemplate
        {
            Id = EntraIdUserSyncId,
            DisplayName = "AD ↔ Entra ID User Sync",
            Description = "Sync users between AD and Entra ID. Update-centric — assumes the target already holds matching identities (e.g. AD as authoritative). Skips create; stamps manager links.",
            SuggestedObjectClass = "User",
            AppliesToSourceFamilies = new[] { ConnectorFamily.Directory },
            AppliesToTargetFamilies = new[] { ConnectorFamily.Directory },
            AppliesToObjectClasses = new[] { "User" },
            RequiresTargetCapability = TargetCapability.Update,
            Specificity = 25,
            Correlation = new TemplateCorrelation { SourceKeyAttr = "userPrincipalName", TargetKeyAttr = "userPrincipalName" },
            Workflows = new[]
            {
                new TemplateWorkflow
                {
                    Name = "Directory User Sync",
                    Description = "Mapping → AssignManager",
                    Steps = new[]
                    {
                        new TemplateStep { Name = "Pull users",     StepType = WorkflowStepTypes.Mapping },
                        new TemplateStep { Name = "Assign manager", StepType = WorkflowStepTypes.AssignManager }
                    }
                }
            }
        },

        // ── HR / CSV / SAP-HCM → Identities Onboarding. Source-family = authoritative
        //    feed (CSV / Database / HR). Create + update, NO deprovision; correlation
        //    on employeeId. MUST NOT show for a directory (AD) source. Target = IC
        //    (people) typically, but any Create-capable target qualifies. ─────────
        new WorkflowTemplate
        {
            Id = HrOnboardingId,
            DisplayName = "HR / CSV Identities Onboarding",
            Description = "Onboard identities from an authoritative feed (HR export, CSV, SAP-HCM, a database view). Correlates on employeeId, creates on miss, updates on match. No deprovision — leavers are handled by a separate Leaver project.",
            SuggestedObjectClass = "User",
            AppliesToSourceFamilies = new[] { ConnectorFamily.AuthoritativeFeed },
            AppliesToTargetFamilies = new[] { ConnectorFamily.IdentityCenter, ConnectorFamily.Directory },
            AppliesToObjectClasses = new[] { "User" },
            RequiresTargetCapability = TargetCapability.Create,
            Specificity = 30,
            Correlation = new TemplateCorrelation { SourceKeyAttr = "employeeId", TargetKeyAttr = "EmployeeId" },
            Workflows = new[]
            {
                new TemplateWorkflow
                {
                    Name = "Identity Onboarding",
                    Description = "Mapping → PersonMatch (employeeId) → PersonCreate",
                    Steps = new[]
                    {
                        new TemplateStep { Name = "Read feed",        StepType = WorkflowStepTypes.Mapping },
                        new TemplateStep { Name = "Match on emp id",  StepType = WorkflowStepTypes.PersonMatch },
                        new TemplateStep { Name = "Create missing",   StepType = WorkflowStepTypes.PersonCreate }
                    }
                }
            }
        },

        // ── Directory → IC / Conduit Objects Inventory / Ingest. Pull a directory
        //    into the IC Objects/Identities lake for governance. Single Mapping step;
        //    requires the target to accept writes (Update). Target = IdentityCenter. ─
        new WorkflowTemplate
        {
            Id = DirectoryInventoryId,
            DisplayName = "Directory → IdentityCenter Inventory",
            Description = "Ingest a directory (AD / Entra / LDAP) into IdentityCenter's Objects or Identities table for governance. Single Mapping pass — no person-matching, no manager stamping.",
            SuggestedObjectClass = "User",
            AppliesToSourceFamilies = new[] { ConnectorFamily.Directory },
            AppliesToTargetFamilies = new[] { ConnectorFamily.IdentityCenter },
            AppliesToObjectClasses = ObjectClasses.Any,
            RequiresTargetCapability = TargetCapability.Update,
            Specificity = 28,
            Workflows = new[]
            {
                new TemplateWorkflow
                {
                    Name = "Inventory Ingest",
                    Description = "Mapping only — directory → IC table.",
                    Steps = new[]
                    {
                        new TemplateStep { Name = "Ingest objects", StepType = WorkflowStepTypes.Mapping }
                    }
                }
            }
        },

        // ── Directory ↔ Directory bidirectional attribute sync. Both sides
        //    directory; requires the target to support Update. ────────────────────
        new WorkflowTemplate
        {
            Id = DirectoryBidirectionalId,
            DisplayName = "Directory ↔ Directory Attribute Sync",
            Description = "Keep a set of attributes in sync between two directories (run a mirror project in each direction). Update-only — does not create or delete objects.",
            SuggestedObjectClass = "User",
            AppliesToSourceFamilies = new[] { ConnectorFamily.Directory },
            AppliesToTargetFamilies = new[] { ConnectorFamily.Directory },
            AppliesToObjectClasses = new[] { "User", "Group" },
            RequiresTargetCapability = TargetCapability.Update,
            Specificity = 20,
            Correlation = new TemplateCorrelation { SourceKeyAttr = "objectGUID", TargetKeyAttr = "objectGUID" },
            Workflows = new[]
            {
                new TemplateWorkflow
                {
                    Name = "Attribute Mirror",
                    Description = "Mapping only — update matched objects.",
                    Steps = new[]
                    {
                        new TemplateStep { Name = "Mirror attributes", StepType = WorkflowStepTypes.Mapping }
                    }
                }
            }
        },

        // ── Group Membership Reconcile. objectClass = Group; full-replace members.
        //    Requires Update on the target. ────────────────────────────────────────
        new WorkflowTemplate
        {
            Id = GroupMembershipReconcileId,
            DisplayName = "Group Membership Reconcile",
            Description = "Full-replace group memberships. Mapping step for the Group object class — targets that interpret the members attribute (AD, Entra, Emulator) diff and apply.",
            SuggestedObjectClass = "Group",
            AppliesToSourceFamilies = ConnectorFamilies.Any,
            AppliesToTargetFamilies = ConnectorFamilies.Any,
            AppliesToObjectClasses = new[] { "Group" },
            RequiresTargetCapability = TargetCapability.Update,
            Specificity = 22,
            Workflows = new[]
            {
                new TemplateWorkflow
                {
                    Name = "Group Reconcile",
                    Description = "Mapping step for Group object class.",
                    Steps = new[]
                    {
                        new TemplateStep { Name = "Reconcile members", StepType = WorkflowStepTypes.Mapping }
                    }
                }
            }
        },

        // ── Deprovision / Leaver. Requires the target to support Delete (soft-delete /
        //    tombstone). The Conduit orchestrator routes deletes via the sink's
        //    DeleteAsync / ITombstoneEmittingSink path on a proven-complete read —
        //    there is NO dedicated "deprovision" WorkflowStepType, so the tree is a
        //    Mapping step and the delete behaviour is the project's tombstone setting.
        //    (See AUDIT NOTE in the agent report — this is deliberate, not a gap.) ──
        new WorkflowTemplate
        {
            Id = DeprovisionLeaverId,
            DisplayName = "Deprovision / Leaver",
            Description = "Soft-delete (tombstone) target records that have disappeared from the source — the leaver path. Only targets that support reversible delete qualify. Pair with an onboarding project that owns create/update.",
            SuggestedObjectClass = "User",
            AppliesToSourceFamilies = ConnectorFamilies.Any,
            AppliesToTargetFamilies = new[] { ConnectorFamily.Directory, ConnectorFamily.IdentityCenter },
            AppliesToObjectClasses = new[] { "User", "Group" },
            RequiresTargetCapability = TargetCapability.Delete,
            Specificity = 24,
            Workflows = new[]
            {
                new TemplateWorkflow
                {
                    Name = "Leaver Reconcile",
                    Description = "Mapping pass — disappeared source ids are tombstoned on the target.",
                    Steps = new[]
                    {
                        new TemplateStep { Name = "Reconcile + tombstone", StepType = WorkflowStepTypes.Mapping }
                    }
                }
            }
        },

        // ── Read-only Sync / Report. No write — requires ReadOnly. Single Mapping
        //    pass to a target that only needs to mirror, never mutate the source. ──
        new WorkflowTemplate
        {
            Id = ReadOnlySyncId,
            DisplayName = "Read-only Sync / Report",
            Description = "Single Mapping step. Pull objects from the source and write to the target — no person-matching, no manager stamping, no delete. The 'I just want to see what's in this system' template.",
            SuggestedObjectClass = "User",
            AppliesToSourceFamilies = ConnectorFamilies.Any,
            AppliesToTargetFamilies = ConnectorFamilies.Any,
            AppliesToObjectClasses = ObjectClasses.Any,
            RequiresTargetCapability = TargetCapability.None,
            Specificity = 10,
            Workflows = new[]
            {
                new TemplateWorkflow
                {
                    Name = "Default",
                    Description = "Mapping only",
                    Steps = new[]
                    {
                        new TemplateStep { Name = "Pull and write", StepType = WorkflowStepTypes.Mapping }
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
    /// Compute the templates that are LEGAL for a (source, target, objectClass)
    /// triple, gated against the target adapter's capabilities, ranked
    /// highest-specificity first (the head of the list is the "Recommended"
    /// pick). Empty is always included (lowest rank) as the fallback.
    ///
    /// A template is applicable when:
    ///   • the source connector's family is in AppliesToSourceFamilies (or wildcard), AND
    ///   • the target connector's family is in AppliesToTargetFamilies (or wildcard), AND
    ///   • the object class is in AppliesToObjectClasses (or wildcard), AND
    ///   • the target adapter advertises the RequiresTargetCapability.
    ///
    /// <paramref name="targetCapabilities"/> is the live target adapter's
    /// <see cref="ConnectorCapabilities"/>; <paramref name="targetSupportsSink"/>
    /// is its SupportsSink flag. When the target adapter is unknown both may be
    /// null/false — we then fail OPEN on capability (don't hide a template just
    /// because an adapter isn't loaded) but still honour the family/class filter.
    /// </summary>
    public static IReadOnlyList<WorkflowTemplate> Applicable(
        string sourceSystemType,
        string targetSystemType,
        string objectClass,
        ConnectorCapabilities? targetCapabilities,
        bool targetSupportsSink)
    {
        var srcFamily = ConnectorFamilies.FamilyOf(sourceSystemType);
        var tgtFamily = ConnectorFamilies.FamilyOf(targetSystemType);

        return All
            .Where(t => t.Id == EmptyId || IsApplicable(t, srcFamily, tgtFamily, objectClass, targetCapabilities, targetSupportsSink))
            .OrderByDescending(t => t.Specificity)
            .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The single highest-specificity applicable template that is NOT Empty,
    /// or Empty when nothing else fits. This is the wizard's auto-pre-selection.</summary>
    public static WorkflowTemplate Recommended(
        string sourceSystemType,
        string targetSystemType,
        string objectClass,
        ConnectorCapabilities? targetCapabilities,
        bool targetSupportsSink)
    {
        var applicable = Applicable(sourceSystemType, targetSystemType, objectClass, targetCapabilities, targetSupportsSink);
        return applicable.FirstOrDefault(t => t.Id != EmptyId)
            ?? All.First(t => t.Id == EmptyId);
    }

    private static bool IsApplicable(
        WorkflowTemplate t,
        ConnectorFamily srcFamily,
        ConnectorFamily tgtFamily,
        string objectClass,
        ConnectorCapabilities? targetCapabilities,
        bool targetSupportsSink)
    {
        // Source family gate (wildcard passes everything).
        if (!t.AppliesToSourceFamilies.Contains(ConnectorFamily.Any)
            && !t.AppliesToSourceFamilies.Contains(srcFamily))
            return false;

        // Target family gate.
        if (!t.AppliesToTargetFamilies.Contains(ConnectorFamily.Any)
            && !t.AppliesToTargetFamilies.Contains(tgtFamily))
            return false;

        // Object-class gate (wildcard passes everything).
        if (!t.AppliesToObjectClasses.Contains(ObjectClasses.Wildcard)
            && !t.AppliesToObjectClasses.Any(c => string.Equals(c, objectClass, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Capability gate against the live target adapter.
        return SatisfiesCapability(t.RequiresTargetCapability, targetCapabilities, targetSupportsSink);
    }

    private static bool SatisfiesCapability(
        TargetCapability required,
        ConnectorCapabilities? caps,
        bool targetSupportsSink)
    {
        switch (required)
        {
            case TargetCapability.None:
                return true;

            case TargetCapability.ReadOnly:
                // A "report" template is fine anywhere; it never mutates the target.
                return true;

            case TargetCapability.Update:
                // Any sink can upsert (the base IConnectorSink contract). Fail open
                // when the adapter is unknown.
                return caps is null || targetSupportsSink;

            case TargetCapability.Create:
                // Needs an explicit create/person-create capability. Fail open when
                // the adapter is unknown (don't hide a template for a tenant whose
                // adapter isn't loaded in this build).
                return caps is null || caps.SupportsCreate || caps.SupportsPersonCreate;

            case TargetCapability.Delete:
                // Conduit expresses delete via the sink's DeleteAsync /
                // ITombstoneEmittingSink path, NOT a capability flag. There is no
                // SupportsDelete today, so we gate on "is a sink at all" and fail
                // open on unknown. FLAGGED in the agent report.
                return caps is null || targetSupportsSink;

            default:
                return true;
        }
    }

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

/// <summary>
/// Coarse connector grouping used by template applicability. A family captures
/// "what KIND of system is this" so a template can target e.g. "any directory"
/// without listing every directory connector. Keyed off the live
/// Tenants.SystemType strings the adapters register.
/// </summary>
public enum ConnectorFamily
{
    /// <summary>Wildcard — used inside a template's family set to mean "any".</summary>
    Any,
    /// <summary>Hierarchical directories: AD, Entra ID, generic LDAP, Okta, Google, SCIM endpoints.</summary>
    Directory,
    /// <summary>Authoritative feeds / systems of record: CSV, Database, (future) SAP-HCM / HR.</summary>
    AuthoritativeFeed,
    /// <summary>The IdentityCenter governance lake (Objects | Identities).</summary>
    IdentityCenter,
    /// <summary>Cloud entitlement / app-assignment systems: AWS IAM Identity Center, SharePoint.</summary>
    CloudEntitlement,
    /// <summary>The built-in test emulator (sink-only).</summary>
    Emulator,
    /// <summary>An unrecognised SystemType (no family mapping). Treated as wildcard-passing on the source side.</summary>
    Unknown
}

/// <summary>Maps the real registered SystemType strings to a <see cref="ConnectorFamily"/>.</summary>
public static class ConnectorFamilies
{
    public static readonly IReadOnlyList<ConnectorFamily> Any = new[] { ConnectorFamily.Any };

    // Keyed on the EXACT SystemType strings each adapter registers (verified
    // against the adapters: ActiveDirectory, GenericLdap, EntraID, Okta,
    // GoogleWorkspace, Scim, CSV, Database, IdentityCenter, AWS, AWSIdentityCenter,
    // SharePoint, Emulator). NOTE: AWS uses "AWS"/"AWSIdentityCenter" here but the
    // wizard's SinkIsAwsSso check looks for "AWSIdentityCenter" only — see the
    // agent report's FLAG on AWS naming drift.
    private static readonly Dictionary<string, ConnectorFamily> _map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ActiveDirectory"]   = ConnectorFamily.Directory,
            ["GenericLdap"]       = ConnectorFamily.Directory,
            ["EntraID"]           = ConnectorFamily.Directory,
            ["Okta"]              = ConnectorFamily.Directory,
            ["GoogleWorkspace"]   = ConnectorFamily.Directory,
            ["Scim"]              = ConnectorFamily.Directory,

            ["CSV"]               = ConnectorFamily.AuthoritativeFeed,
            ["Database"]          = ConnectorFamily.AuthoritativeFeed,

            ["IdentityCenter"]    = ConnectorFamily.IdentityCenter,

            ["AWS"]               = ConnectorFamily.CloudEntitlement,
            ["AWSIdentityCenter"] = ConnectorFamily.CloudEntitlement,
            ["SharePoint"]        = ConnectorFamily.CloudEntitlement,

            ["Emulator"]          = ConnectorFamily.Emulator,
        };

    public static ConnectorFamily FamilyOf(string? systemType) =>
        !string.IsNullOrWhiteSpace(systemType) && _map.TryGetValue(systemType, out var f)
            ? f
            : ConnectorFamily.Unknown;
}

/// <summary>The write a template needs the TARGET adapter to support.</summary>
public enum TargetCapability
{
    /// <summary>No requirement (e.g. the Empty template produces no tree).</summary>
    None,
    /// <summary>Target only needs to mirror — never mutates the source. Report-style.</summary>
    ReadOnly,
    /// <summary>Target must accept upserts (the base IConnectorSink contract).</summary>
    Update,
    /// <summary>Target must support object/person creation (ConnectorCapabilities.SupportsCreate / SupportsPersonCreate).</summary>
    Create,
    /// <summary>Target must support reversible delete (DeleteAsync / ITombstoneEmittingSink).</summary>
    Delete
}

/// <summary>Object-class wildcard helpers for template applicability.</summary>
public static class ObjectClasses
{
    public const string Wildcard = "*";
    public static readonly IReadOnlyList<string> Any = new[] { Wildcard };
}

/// <summary>A reusable workflow shape the operator picks at create time.</summary>
public sealed class WorkflowTemplate
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    /// <summary>Pre-fills the project's ObjectClass when the operator picks this template.</summary>
    public string SuggestedObjectClass { get; init; } = "User";

    // ─── Context-aware applicability ─────────────────────────────────────────
    /// <summary>Connector families the SOURCE must belong to. <see cref="ConnectorFamilies.Any"/> = any.</summary>
    public IReadOnlyList<ConnectorFamily> AppliesToSourceFamilies { get; init; } = ConnectorFamilies.Any;
    /// <summary>Connector families the TARGET must belong to. <see cref="ConnectorFamilies.Any"/> = any.</summary>
    public IReadOnlyList<ConnectorFamily> AppliesToTargetFamilies { get; init; } = ConnectorFamilies.Any;
    /// <summary>Object classes this template fits. <see cref="ObjectClasses.Any"/> = any.</summary>
    public IReadOnlyList<string> AppliesToObjectClasses { get; init; } = ObjectClasses.Any;
    /// <summary>The write the template needs the target adapter to support; gated against live capabilities.</summary>
    public TargetCapability RequiresTargetCapability { get; init; } = TargetCapability.None;
    /// <summary>Ranking. Exact family pair beats one-sided beats wildcard. Higher = more specific = "Recommended".</summary>
    public int Specificity { get; init; }

    // ─── Legacy string whitelists (kept for back-compat; superseded by families) ─
    /// <summary>Optional whitelist of source SystemTypes. Empty = applies anywhere. Superseded by <see cref="AppliesToSourceFamilies"/>.</summary>
    public IReadOnlyList<string> ApplicableSourceTypes { get; init; } = Array.Empty<string>();
    /// <summary>Optional whitelist of sink SystemTypes. Empty = applies anywhere. Superseded by <see cref="AppliesToTargetFamilies"/>.</summary>
    public IReadOnlyList<string> ApplicableSinkTypes { get; init; } = Array.Empty<string>();

    // ─── Content ─────────────────────────────────────────────────────────────
    /// <summary>Optional correlation key pair (source attr ↔ target attr) the template suggests.</summary>
    public TemplateCorrelation? Correlation { get; init; }
    public IReadOnlyList<TemplateWorkflow> Workflows { get; init; } = Array.Empty<TemplateWorkflow>();
}

/// <summary>The source ↔ target attribute pair a template correlates on (advisory; surfaced in the wizard).</summary>
public sealed class TemplateCorrelation
{
    public string SourceKeyAttr { get; init; } = string.Empty;
    public string TargetKeyAttr { get; init; } = string.Empty;
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
