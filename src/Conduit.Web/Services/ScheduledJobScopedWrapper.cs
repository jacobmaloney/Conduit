using Conduit.Scheduling;

namespace Conduit.Web;

/// <summary>
/// Adapts a scope-dependent IScheduledJob (one that needs Scoped DI deps like
/// repositories per tick) to the singleton IScheduledJob contract the
/// SchedulerService expects. On each ExecuteAsync, opens a fresh scope, builds
/// the inner job from it, runs it, disposes the scope.
/// </summary>
public sealed class ScheduledJobScopedWrapper : IScheduledJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<IServiceProvider, IScheduledJob> _builder;
    private readonly string _name;
    private readonly TimeSpan _interval;

    public ScheduledJobScopedWrapper(
        IServiceScopeFactory scopeFactory,
        Func<IServiceProvider, IScheduledJob> builder)
    {
        _scopeFactory = scopeFactory;
        _builder = builder;

        // Probe a temporary instance to grab the JobName + Interval. The probe
        // scope is disposed immediately; the real instance is built fresh per tick.
        using var scope = _scopeFactory.CreateScope();
        var probe = _builder(scope.ServiceProvider);
        _name = probe.JobName;
        _interval = probe.Interval;
    }

    public string JobName => _name;
    public TimeSpan Interval => _interval;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var inner = _builder(scope.ServiceProvider);
        await inner.ExecuteAsync(cancellationToken);
    }
}
