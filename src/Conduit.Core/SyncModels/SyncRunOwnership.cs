namespace Conduit.Core.SyncModels;

/// <summary>LastRunId is the admission token; an absent local task is never proof of owner death.</summary>
public static class SyncRunOwnership
{
    public static IReadOnlyList<string> TerminalStatuses { get; } = Array.AsReadOnly(new[]
        { "Succeeded", "PartialSuccess", "Failed", "Cancelled", "Interrupted", "Skipped" });

    public static void RequireOwner(Guid runId)
    {
        if (runId == Guid.Empty)
            throw new SyncRunOwnershipException("RunOwnerRequired", "A nonempty run owner is required before changing execution state.");
    }

    /// <summary>Called with the just-read persisted project before execution creates a run or touches connectors.</summary>
    public static void RequireCurrentOwner(SyncProject project, Guid expectedRunId)
    {
        RequireOwner(expectedRunId);
        if (!project.IsRunning || project.LastRunId != expectedRunId)
            throw new SyncRunOwnershipException("RunOwnershipLost", "The supplied admission owner no longer owns this project.");
    }

    public static void RequireRecoverable(SyncProject project, SyncRun? owner, Guid expectedRunId)
    {
        if (expectedRunId == Guid.Empty || !project.IsRunning || project.LastRunId != expectedRunId
            || owner is null || owner.Id != expectedRunId || owner.SyncProjectId != project.Id
            || !TerminalStatuses.Contains(owner.Status, StringComparer.Ordinal))
            throw new SyncRunOwnershipException("RunOwnershipUnverified",
                "Recovery requires the same persisted owner with a confirmed terminal run. A running, missing, or remote owner cannot be force-released here.");
    }
}

public sealed class SyncRunOwnershipException(string code, string message) : InvalidOperationException(code + ": " + message)
{
    public string Code { get; } = code;
}
