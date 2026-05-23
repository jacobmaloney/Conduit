namespace Conduit.Scheduling.Jobs;

/// <summary>
/// Placeholder: in a later phase will iterate registered connectors,
/// call TestConnectionAsync, and update health state.
/// </summary>
public class ConnectorHealthCheckJob : IScheduledJob
{
    public string JobName => "ConnectorHealthCheck";
    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
