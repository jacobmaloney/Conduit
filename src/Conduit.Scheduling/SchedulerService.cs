using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conduit.Scheduling;

/// <summary>
/// Lightweight in-process job scheduler for Conduit. Single-process,
/// non-persistent. See Conduit.Scheduling project README / docs for
/// the full constraint list before relying on it.
/// </summary>
public sealed class SchedulerService : BackgroundService
{
    private readonly IEnumerable<IScheduledJob> _jobs;
    private readonly ILogger<SchedulerService> _logger;

    public SchedulerService(IEnumerable<IScheduledJob> jobs, ILogger<SchedulerService> logger)
    {
        _jobs = jobs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Conduit Scheduler starting. {JobCount} job(s) registered.", _jobs.Count());

        var tasks = _jobs.Select(job => RunJobLoopAsync(job, stoppingToken));
        await Task.WhenAll(tasks);
    }

    private async Task RunJobLoopAsync(IScheduledJob job, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Scheduler: {JobName} registered with interval {Interval}.", job.JobName, job.Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(job.Interval, stoppingToken);

                if (stoppingToken.IsCancellationRequested)
                    break;

                _logger.LogDebug("Scheduler: Executing {JobName}.", job.JobName);
                await job.ExecuteAsync(stoppingToken);
                _logger.LogDebug("Scheduler: {JobName} completed.", job.JobName);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduler: {JobName} threw an unhandled exception. Will retry at next interval.", job.JobName);
            }
        }

        _logger.LogInformation("Scheduler: {JobName} loop stopped.", job.JobName);
    }
}
