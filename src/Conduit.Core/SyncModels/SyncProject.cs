using System;
using System.Collections.Generic;
using System.Linq;

namespace Conduit.Core.SyncModels;

/// <summary>
/// A Sync Project pumps objects from one Connected System (source) to another
/// (sink). Per the conduit-symmetric-router-architecture decision, every
/// Connected System is potentially a source AND a sink — direction is wired by
/// the project. Conduit owns ONLY the sync metadata; it never persists object
/// rows from connector tenants.
/// </summary>
public class SyncProject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Workspace scope. Null in single-workspace installs.</summary>
    public Guid? WorkspaceId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Source Connected System (Tenant) — data is pulled FROM here.</summary>
    public Guid SourceTenantId { get; set; }

    /// <summary>Sink Connected System (Tenant) — data is pushed TO here.</summary>
    public Guid SinkTenantId { get; set; }

    /// <summary>Object class to sync. Phase 1A supports "User" and "Group".</summary>
    public string ObjectClass { get; set; } = "User";

    /// <summary>
    /// Phase 2 multi-credential UX: which credential name on the source tenant
    /// this project should authenticate with. Null = adapter's default (matches
    /// pre-Phase-2 behavior).
    /// </summary>
    public string? SourceCredentialName { get; set; }

    /// <summary>Phase 2 multi-credential UX: which credential name on the sink tenant.</summary>
    public string? SinkCredentialName { get; set; }

    /// <summary>
    /// Per-endpoint IdentityCenter table selection (V22). When the SOURCE endpoint's
    /// connector is IdentityCenter, this picks which IC table the project reads FROM:
    /// "Objects" (directory accounts, /api/objects/*) or "Identities" (people golden
    /// records, /api/identities/*). NULL/empty = "Objects" (back-compat default).
    /// Meaningless and ignored when the source connector is not IdentityCenter.
    /// </summary>
    public string? SourceTable { get; set; }

    /// <summary>
    /// Per-endpoint IdentityCenter table selection (V22). When the SINK endpoint's
    /// connector is IdentityCenter, this picks which IC table the project writes TO:
    /// "Objects" or "Identities". NULL/empty = "Objects" (back-compat default).
    /// Tombstone soft-delete only applies to an Objects sink; an Identities sink is a
    /// safe no-op. Meaningless and ignored when the sink connector is not IdentityCenter.
    /// </summary>
    public string? SinkTable { get; set; }

    /// <summary>Cron expression for scheduled execution. Null = manual run only.</summary>
    public string? CronSchedule { get; set; }

    public bool IsEnabled { get; set; } = true;
    public bool IsRunning { get; set; }

    /// <summary>
    /// Opt-in sink-side skip-unchanged. When true, the orchestrator load-once
    /// caches the project's sink content hashes and skips re-pushing records whose
    /// mapped payload is unchanged since the last successful write. Off by default
    /// so connectors that can't support it safely just do the normal upsert.
    /// </summary>
    public bool SkipUnchanged { get; set; }

    public DateTime? LastRunAt { get; set; }
    public string? LastRunStatus { get; set; }
    public Guid? LastRunId { get; set; }
    public DateTime? NextScheduledRunAt { get; set; }

    public int TotalRuns { get; set; }
    public int SuccessfulRuns { get; set; }
    public int FailedRuns { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Per-project source-side scope filter. For LDAP/AD: BaseDN + LdapFilter.
/// For HTTP/SCIM connectors: QueryExpression. PageSize bounds the read window.
/// </summary>
public class SyncProjectScope
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SyncProjectId { get; set; }
    /// <summary>
    /// Phase 7. When non-null, this scope attaches to a specific WorkflowStep
    /// inside the project's workflow tree. When null (legacy / Phase 6 and
    /// earlier), the scope applies to the project as a whole.
    /// </summary>
    public Guid? WorkflowStepId { get; set; }
    /// <summary>
    /// Legacy single-container Base DN. Retained for back-compat and as the
    /// "first included" fallback. When <see cref="IncludedBaseDNs"/> is populated
    /// (the IC-style multi-select scope), this holds the first included DN so any
    /// caller that still reads a single BaseDN keeps working.
    /// </summary>
    public string? BaseDN { get; set; }
    /// <summary>
    /// IC-parity multi-select scope. JSON array of explicitly-Included container
    /// DNs. Each becomes a Subtree LDAP base. NULL/empty = fall back to
    /// <see cref="BaseDN"/> (single-container) or the source default naming context.
    /// Mirrors IC's <c>SyncStep.SearchBases</c>.
    /// </summary>
    public string? IncludedBaseDNs { get; set; }
    /// <summary>
    /// IC-parity multi-select scope. JSON array of explicitly-Blocked container
    /// DNs. Any object whose DN is at or under one of these is dropped from the
    /// read, even when it sits under an Included base. Mirrors IC's
    /// <c>SyncStep.ExcludedSearchBases</c>.
    /// </summary>
    public string? ExcludedBaseDNs { get; set; }
    public string? LdapFilter { get; set; }
    public string? QueryExpression { get; set; }
    public int PageSize { get; set; } = 1000;
    public int? MaxObjects { get; set; }
    public bool IncludeDeleted { get; set; }
    /// <summary>
    /// In-memory attribute projection hint (NOT persisted). When the orchestrator
    /// knows which SOURCE attributes a step actually maps, it stamps them here so
    /// the source connector can request ONLY those (plus its own structural floor)
    /// instead of pulling every attribute. NULL = no hint → read all attributes
    /// (back-compat). A source connector that honors this MUST still union in the
    /// structural attributes it depends on (id / cursor / class), so an under- or
    /// over-specified hint can never starve correctness. Set by the orchestrator
    /// per run; never round-tripped to the DB.
    /// </summary>
    public List<string>? RequestedAttributes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastModified { get; set; } = DateTime.UtcNow;

    // ─── IC-parity multi-select scope helpers ────────────────────────────
    // The UI works in two HashSet<string> (Included / Blocked) DN sets exactly
    // like IC's MultiScopeBrowser. These helpers (de)serialize those sets to the
    // JSON columns and keep BaseDN coherent as the single-value fallback. The
    // 4-state model is: Included (in the included set), Blocked (in the blocked
    // set), Inherited (an ancestor DN is Included — computed in the UI, never
    // stored), NotSelected (in neither set).

    /// <summary>
    /// Returns the explicitly-Included container DNs. Priority: IncludedBaseDNs
    /// JSON array, else the single BaseDN, else empty. Mirrors IC
    /// <c>SyncStep.GetSearchBaseList()</c>.
    /// </summary>
    public List<string> GetIncludedBaseList()
    {
        if (!string.IsNullOrWhiteSpace(IncludedBaseDNs))
        {
            try
            {
                var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(IncludedBaseDNs);
                if (list is { Count: > 0 })
                    return list.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            }
            catch (System.Text.Json.JsonException) { /* fall through to BaseDN */ }
        }
        if (!string.IsNullOrWhiteSpace(BaseDN))
            return new List<string> { BaseDN };
        return new List<string>();
    }

    /// <summary>
    /// Stores the explicitly-Included container DNs as JSON. Also stamps
    /// <see cref="BaseDN"/> with the first entry so single-value callers keep
    /// working. Empty list clears both. Mirrors IC <c>SyncStep.SetSearchBaseList()</c>.
    /// </summary>
    public void SetIncludedBaseList(IEnumerable<string>? included)
    {
        var valid = (included ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (valid.Count == 0)
        {
            IncludedBaseDNs = null;
            // Leave BaseDN as-is only if it was the sole source of truth; here we
            // null it so an empty multi-select genuinely means "no included base".
            BaseDN = null;
            return;
        }

        BaseDN = valid[0]; // single-value fallback
        IncludedBaseDNs = valid.Count == 1 ? null : System.Text.Json.JsonSerializer.Serialize(valid);
    }

    /// <summary>Returns the explicitly-Blocked container DNs. Mirrors IC <c>GetExcludedSearchBaseList()</c>.</summary>
    public List<string> GetExcludedBaseList()
    {
        if (string.IsNullOrWhiteSpace(ExcludedBaseDNs))
            return new List<string>();
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(ExcludedBaseDNs);
            return list?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
        }
        catch (System.Text.Json.JsonException)
        {
            return new List<string>();
        }
    }

    /// <summary>Stores the explicitly-Blocked container DNs as JSON. Mirrors IC <c>SetExcludedSearchBaseList()</c>.</summary>
    public void SetExcludedBaseList(IEnumerable<string>? excluded)
    {
        var valid = (excluded ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        ExcludedBaseDNs = valid.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(valid);
    }
}

/// <summary>One source attribute → one sink attribute. Optional transform.</summary>
public class AttributeMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SyncProjectId { get; set; }
    /// <summary>
    /// Phase 7. When non-null, this mapping attaches to a specific WorkflowStep
    /// (StepType=Mapping). When null, the mapping applies project-wide (legacy
    /// pre-Phase-7 semantics).
    /// </summary>
    public Guid? WorkflowStepId { get; set; }
    public string SourceAttribute { get; set; } = string.Empty;
    public string SinkAttribute { get; set; } = string.Empty;
    /// <summary>Optional transform expression. Phase 1A is passthrough only.</summary>
    public string? TransformExpr { get; set; }
    public bool IsRequired { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>
/// Phase 7. A Workflow groups ordered WorkflowSteps inside a SyncProject. Mirrors
/// IC's SyncProjectWizard model so an operator can compose a multi-step pipeline
/// (e.g. Mapping → PersonMatch → PersonCreate → AssignManager) without abandoning
/// the symmetric-router project shape.
/// </summary>
public class Workflow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SyncProjectId { get; set; }
    public string Name { get; set; } = "Default";
    public string? Description { get; set; }
    /// <summary>Workflows execute in ascending Ordinal order within a project.</summary>
    public int Ordinal { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Phase 7. One step inside a Workflow. <see cref="StepType"/> is the discriminator
/// the orchestrator routes on: Mapping runs the classic source→mapping→sink loop
/// (with the step's own AttributeMappings + optional SyncProjectScope), the four
/// person-aware step types call the matching <see cref="Conduit.Sync.Connectors.IConnectorSink"/>
/// methods, Custom is reserved.
/// </summary>
public class WorkflowStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkflowId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>One of: Mapping, PersonMatch, PersonCreate, AssignManager, AssignGroupOwner, Custom.</summary>
    public string StepType { get; set; } = "Mapping";
    /// <summary>Steps execute in ascending Ordinal order within a workflow.</summary>
    public int Ordinal { get; set; }
    /// <summary>
    /// V23: the object class this step reads (e.g. "user", "group", "computer").
    /// One project's workflow holds N Mapping steps — one per object class. The
    /// orchestrator resolves <c>step.ObjectClass ?? project.ObjectClass</c> at read
    /// time, so a NULL value transparently falls back to the parent project's class
    /// (the pre-V23 single-class shape). Only meaningful for Mapping steps.
    /// </summary>
    public string? ObjectClass { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Free-form JSON for per-StepType config (e.g. PersonCreate template attrs).</summary>
    public string? Configuration { get; set; }

    /// <summary>
    /// V25: per-STEP incremental cursor. Each Mapping step is its own complete
    /// per-class read with its own high-water mark (e.g. AD highestCommittedUSN /
    /// uSNChanged for THAT class's enumeration), so the cursor is keyed at the
    /// step — NOT the project. Sharing one project-level cursor across N per-class
    /// steps would let one class's high-water mark clobber another's. NULL = no
    /// prior cursor → full enumeration next run (always SAFE). Only meaningful for
    /// Mapping steps on a source connector whose Capabilities.SupportsIncremental.
    /// </summary>
    public string? IncrementalCursor { get; set; }
    /// <summary>V25: when <see cref="IncrementalCursor"/> was last advanced (UTC). NULL until first set.</summary>
    public DateTime? CursorUpdatedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Phase 7. Canonical StepType values. Source-of-truth for the UI dropdown +
/// orchestrator router. Kept as strings (not enum) to avoid migration churn
/// when future step types are added.
/// </summary>
public static class WorkflowStepTypes
{
    public const string Mapping = "Mapping";
    public const string PersonMatch = "PersonMatch";
    public const string PersonCreate = "PersonCreate";
    public const string AssignManager = "AssignManager";
    public const string AssignGroupOwner = "AssignGroupOwner";
    public const string Custom = "Custom";

    /// <summary>
    /// IC-parity relationship-resolution step (e.g. "Resolve Manager Relationships" /
    /// "Resolve ManagedBy" / "Resolve Group Owner"). LICENSED IdentityCenter feature:
    /// only meaningful when a project's SINK is an IdentityCenter connection. It mirrors
    /// IC's per-class Lookup card (manager / managedBy / owner resolution).
    ///
    /// FUNCTIONAL (as of the governance-step build): the orchestrator's step router has
    /// a Lookup arm (<c>ExecuteLookupStepAsync</c>). It reads the manager/managedBy/owner
    /// reference the upstream Mapping step carried on each object and hands the pair to
    /// IC via the sink's <c>AssignManagerAsync</c> (user/contact class) or
    /// <c>AssignGroupOwnerAsync</c> (group/team class) — IC owns the object store and the
    /// resolution. A non-IC sink (no capability) clean-skips; AD manager DNs that IC
    /// cannot resolve against PrimaryEmail are counted as SKIPPED, never a fake success.
    /// </summary>
    public const string Lookup = "Lookup";

    // ── IC-parity "deeper governance" step types ────────────────────────────
    // These mirror IdentityCenter's AutoSyncProjectGenerator step types beyond
    // manager/owner resolution. Like <see cref="Lookup"/>, they are LICENSED IC
    // features: emitted ONLY when a project's SINK is an IdentityCenter connection
    // (and the source-gated ones — License/SignInLog/UsageReport/AppRole — only
    // when the SOURCE is Entra ID, matching IC's connectionType gate).
    //
    // HONEST STATUS (markers, not functional): the orchestrator's step router has
    // NO arm for any of these, so at run time they fall through to the safe
    // `_ => StepResult.Skipped(...)` default — they persist, open, and render 1:1
    // with IC's dashed step cards, but the actual governance (M365 license sync,
    // sign-in/usage/app-role ingest, group-membership ingest) runs IC-side and is
    // NOT implemented in the free pump. They must never claim to do governance.

    /// <summary>IC "Sync M365 Licenses" — Entra→IC license-assignment ingest. MARKER (run-skipped).</summary>
    public const string LicenseSync = "LicenseSync";

    /// <summary>IC "Sync Sign-In Logs" — Entra→IC sign-in-log ingest. MARKER (run-skipped).</summary>
    public const string SignInLogSync = "SignInLogSync";

    /// <summary>IC "Sync M365 Usage Reports" — Entra→IC usage-activity ingest. MARKER (run-skipped).</summary>
    public const string UsageReportSync = "UsageReportSync";

    /// <summary>IC "Sync App Role Assignments" — Entra→IC enterprise-app role ingest. MARKER (run-skipped).</summary>
    public const string AppRoleSync = "AppRoleSync";

    /// <summary>
    /// IC "Sync Group Memberships" — memberOf → membership ingest, attached to the
    /// GROUP class (after groups + owners exist). MARKER (run-skipped): Conduit has
    /// no membership-ingest capability today, so this is structural only.
    /// </summary>
    public const string GroupMembership = "GroupMembership";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Mapping, PersonMatch, PersonCreate, AssignManager, AssignGroupOwner, Custom, Lookup,
        LicenseSync, SignInLogSync, UsageReportSync, AppRoleSync, GroupMembership
    };
}
