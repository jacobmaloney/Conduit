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
    public bool Enabled { get; set; } = true;
    /// <summary>Free-form JSON for per-StepType config (e.g. PersonCreate template attrs).</summary>
    public string? Configuration { get; set; }
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

    public static readonly IReadOnlyList<string> All = new[]
    {
        Mapping, PersonMatch, PersonCreate, AssignManager, AssignGroupOwner, Custom
    };
}
