namespace Conduit.Sync.Orchestration;

/// <summary>Counts and completion policy shared by normal and exceptional step exits.</summary>
public readonly record struct SyncStepDelta(int Read, int Created, int Updated, int Skipped, int Failed, int Other)
{
    public static SyncStepDelta Exception => new(0, 0, 0, 0, 1, 0);
}

public sealed record SyncRunCompletion(string Status, string? ErrorMessage);

public sealed class SyncRunProgress
{
    public int Read { get; private set; }
    public int Created { get; private set; }
    public int Updated { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public int FailedSteps { get; private set; }
    public int SucceededSteps { get; private set; }
    private bool _anyMapping, _anyStep, _incremental;
    private string? _firstError;

    public void Add(SyncStepDelta delta, bool isMapping, bool wasIncremental, string? failureReason)
    {
        Read += delta.Read;
        Created += delta.Created;
        Updated += delta.Updated;
        Skipped += delta.Skipped;
        Failed += delta.Failed;
        _anyStep = true;
        _anyMapping |= isMapping;
        _incremental |= wasIncremental;
        if (delta.Failed > 0)
        {
            FailedSteps++;
            _firstError ??= failureReason;
        }
        else if (delta.Read > 0 || delta.Created > 0 || delta.Updated > 0)
            SucceededSteps++;
    }

    public SyncRunCompletion Complete()
    {
        if (FailedSteps > 0)
            return new(SucceededSteps > 0 ? "PartialSuccess" : "Failed", _firstError);
        if (_anyMapping && Read == 0 && !_incremental)
            return new("Failed", "Source enumeration returned 0 objects. Check the connection credential, BaseDN, and LDAP filter — a truthful 0-record run is treated as a failure to avoid a silent green success.");
        if (!_anyStep)
            return new("Failed", "No workflow steps executed — the project has no enabled Mapping step. Nothing was synced.");
        return new("Succeeded", null);
    }
}
