using System;

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
    public string? BaseDN { get; set; }
    public string? LdapFilter { get; set; }
    public string? QueryExpression { get; set; }
    public int PageSize { get; set; } = 1000;
    public int? MaxObjects { get; set; }
    public bool IncludeDeleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
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
