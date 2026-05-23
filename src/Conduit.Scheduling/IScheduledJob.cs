namespace Conduit.Scheduling;

public interface IScheduledJob
{
    string JobName { get; }
    TimeSpan Interval { get; }
    Task ExecuteAsync(CancellationToken cancellationToken);
}
