using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;

namespace Conduit.Web.Services;

/// <summary>
/// Brute-force protection for the portal login, backed by SQL Server so failure
/// counters survive process restarts. Tracks attempts per (username, IP) tuple
/// with sliding-window decay and applies progressive backoff once the threshold
/// is breached.
///
/// Tables (migration v11):
///   LoginAttempts  — append-only log of every attempt with Success flag
///   LoginLockouts  — current active hard-lockout per (user, IP)
///
/// Old rows are pruned by <see cref="LoginThrottlePruner"/> on a timer; nothing
/// here depends on the prune running, but without it the tables grow unbounded.
/// </summary>
public class LoginThrottle
{
    // Conservative thresholds — same shape as the in-memory predecessor.
    private static readonly TimeSpan Window         = TimeSpan.FromMinutes(15);
    private const int SoftThreshold = 5;       // start backoff after 5 failures in window
    private const int HardThreshold = 10;      // hard lockout after 10 failures in window
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly LoginThrottleRepository _repository;

    public LoginThrottle(LoginThrottleRepository repository)
    {
        _repository = repository;
    }

    public sealed record CheckResult(bool Allowed, TimeSpan RetryAfter, int FailuresInWindow);

    /// <summary>
    /// Call BEFORE attempting to verify the password. If allowed=false, refuse the
    /// login outright with a 429 and the supplied Retry-After.
    /// </summary>
    public async Task<CheckResult> CheckAllowedAsync(string usernameLower, string ipAddress)
    {
        var now = DateTime.UtcNow;
        var windowStart = now - Window;

        try
        {
            // Active hard lockout?
            var lockout = await _repository.GetActiveLockoutUntilAsync(usernameLower, ipAddress, now);

            if (lockout is { } until && until > now)
            {
                var failuresAtLockout = await _repository.GetLockoutFailureCountAsync(usernameLower, ipAddress);
                return new CheckResult(false, until - now, failuresAtLockout);
            }

            // Soft-delay path: count failures in window.
            var failures = await _repository.CountFailuresInWindowAsync(usernameLower, ipAddress, windowStart);

            if (failures >= SoftThreshold)
            {
                var soft = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, failures - SoftThreshold)));
                return new CheckResult(true, soft, failures);
            }

            return new CheckResult(true, TimeSpan.Zero, failures);
        }
        catch
        {
            // If the DB is unreachable we explicitly fail OPEN — better than locking
            // every operator out because of a connectivity blip. The LoginService
            // still logs the auth attempt via the rest of its path.
            return new CheckResult(true, TimeSpan.Zero, 0);
        }
    }

    /// <summary>
    /// Call after a successful login — clears the bucket so the user starts fresh.
    /// </summary>
    public async Task RegisterSuccessAsync(string usernameLower, string ipAddress)
    {
        try
        {
            await _repository.RecordSuccessAsync(usernameLower, ipAddress);
        }
        catch
        {
            // Recording a success is not load-bearing for auth — swallow.
        }
    }

    /// <summary>
    /// Call after a failed login. Returns the new failure count within the window.
    /// </summary>
    public async Task<int> RegisterFailureAsync(string usernameLower, string ipAddress)
    {
        try
        {
            var now = DateTime.UtcNow;
            var windowStart = now - Window;

            await _repository.RecordFailureAsync(usernameLower, ipAddress, now);

            var failures = await _repository.CountFailuresInWindowAsync(usernameLower, ipAddress, windowStart);

            if (failures >= HardThreshold)
            {
                // Upsert lockout row — SQL Server MERGE keeps it single round-trip
                // and idempotent. LockedUntil extends on every subsequent failure.
                await _repository.UpsertLockoutAsync(usernameLower, ipAddress, now.Add(LockoutDuration), failures);
            }

            return failures;
        }
        catch
        {
            // Failing to record a failure is regrettable but mustn't block the
            // caller from seeing the BadCredentials outcome.
            return 0;
        }
    }
}

/// <summary>
/// Background pruner — keeps LoginAttempts bounded by deleting rows older than 1 day
/// and clearing expired LoginLockouts. Runs every 30 minutes; safe to skip a tick.
/// </summary>
public class LoginThrottlePruner : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AttemptRetention = TimeSpan.FromDays(1);

    private readonly IServiceProvider _services;
    private readonly ILogger<LoginThrottlePruner> _logger;

    public LoginThrottlePruner(IServiceProvider services, ILogger<LoginThrottlePruner> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DatabaseConfig>();
                if (!string.IsNullOrEmpty(db.ConnectionString))
                {
                    var repository = scope.ServiceProvider.GetRequiredService<LoginThrottleRepository>();
                    var cutoff = DateTime.UtcNow - AttemptRetention;
                    var rows = await repository.PruneAsync(cutoff);
                    if (rows > 0)
                    {
                        _logger.LogInformation("LoginThrottlePruner: removed {Rows} old rows.", rows);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LoginThrottlePruner tick failed; will retry.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
