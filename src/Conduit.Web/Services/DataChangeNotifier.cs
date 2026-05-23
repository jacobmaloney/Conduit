namespace Conduit.Web.Services;

/// <summary>
/// Process-wide event source signaling that user/group data has changed. UI components
/// (NavMenu count badges, future dashboards) subscribe to refresh themselves without
/// having to poll. Registered as a singleton so background tasks (generation, cleanup)
/// can publish even when their DI scope no longer matches the UI's.
/// </summary>
public class DataChangeNotifier
{
    public event Func<DataChangeKind, Task>? OnChangedAsync;

    public Task NotifyAsync(DataChangeKind kind)
    {
        var handler = OnChangedAsync;
        return handler is null ? Task.CompletedTask : handler.Invoke(kind);
    }
}

public enum DataChangeKind
{
    Users,
    Groups,
    Both,
    /// <summary>
    /// Connected System (tenant) list itself changed — added, edited, deleted,
    /// seeded, or deactivated. NavMenu listens so the switcher refreshes its
    /// tenant list without a page reload.
    /// </summary>
    Tenants
}
