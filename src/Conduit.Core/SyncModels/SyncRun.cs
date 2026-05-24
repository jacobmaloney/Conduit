using System;

namespace Conduit.Core.SyncModels;

/// <summary>One execution attempt of a SyncProject. Lives forever (history).</summary>
public class SyncRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? WorkspaceId { get; set; }
    public Guid SyncProjectId { get; set; }

    /// <summary>Running | Succeeded | Failed | Cancelled.</summary>
    public string Status { get; set; } = "Running";

    /// <summary>Manual | Scheduled | System.</summary>
    public string TriggeredBy { get; set; } = "Manual";

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public long? DurationMs { get; set; }

    public int ObjectsRead { get; set; }
    public int ObjectsCreated { get; set; }
    public int ObjectsUpdated { get; set; }
    public int ObjectsSkipped { get; set; }
    public int ObjectsFailed { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Phase 2 incremental sync: opaque cursor token persisted at the end of a
    /// successful run. The next run reads it back and passes to the source's
    /// EnumerateAsync. Null when the connector doesn't support incremental.
    /// </summary>
    public string? Cursor { get; set; }

    /// <summary>True if this run was an incremental (delta) enumeration.</summary>
    public bool IsIncremental { get; set; }
}

/// <summary>One log line attached to a SyncRun. Cheap append-only buffer.</summary>
public class SyncRunLog
{
    public long Id { get; set; }
    public Guid SyncRunId { get; set; }
    /// <summary>Info | Warning | Error | Debug.</summary>
    public string Level { get; set; } = "Info";
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Phase 4. One row per adapter-submitted async job (e.g. AWS SSO
/// CreateAccountAssignment RequestId). The background AsyncJobPollerService
/// advances State Pending → Succeeded / Failed by calling the adapter's
/// <c>IConnectorAsyncJobResolver</c>.
/// </summary>
public class SyncRunAsyncJob
{
    public long Id { get; set; }
    public Guid SyncRunId { get; set; }
    public Guid SyncProjectId { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>Matches IConnectorAdapter.SystemType — the registry key.</summary>
    public string SystemType { get; set; } = string.Empty;
    /// <summary>Adapter-defined kind (e.g. "AwsSsoCreateAccountAssignment").</summary>
    public string JobType { get; set; } = string.Empty;
    /// <summary>Opaque handle from target system (RequestId, OperationId, etc.).</summary>
    public string JobId { get; set; } = string.Empty;
    /// <summary>Source object identifier (ConnectorObject.SourceId at submission).</summary>
    public string? ObjectExternalId { get; set; }
    /// <summary>Pending | Succeeded | Failed.</summary>
    public string State { get; set; } = "Pending";
    public string? ErrorMessage { get; set; }
    /// <summary>Opaque JSON payload kept for the resolver — Conduit doesn't read it.</summary>
    public string? PayloadJson { get; set; }
    public string? ResultJson { get; set; }
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastPolledAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int PollAttempts { get; set; }
}
