using System;
using System.Threading;
using System.Threading.Tasks;
using Conduit.DataAccess;
using Conduit.Web.Middleware;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conduit.Web.Services
{
    /// <summary>
    /// Makes startup database initialization resilient and self-healing.
    ///
    /// <para>Two layers:</para>
    /// <list type="number">
    ///   <item><b>Inline bounded retry-with-backoff</b> (<see cref="TryInitializeWithRetryAsync"/>),
    ///     invoked synchronously at startup. Handles a brief blip — the DB coming up a few
    ///     seconds after the app — without any further machinery. Retries ONLY connectivity-class
    ///     failures (classified by <see cref="SqlConnectivity.IsTransient"/>); a fatal error
    ///     (auth 18456, permission, a genuine schema/migration exception) surfaces immediately —
    ///     waiting will not fix it.</item>
    ///   <item><b>Background self-heal</b> (<see cref="DatabaseSelfHealService"/>, an
    ///     <see cref="IHostedService"/>). If the inline retries are exhausted and it is STILL a
    ///     connectivity failure, the web host still STARTS (so /db-offline can serve) and this
    ///     service keeps retrying at a capped interval until the DB returns, then runs init once
    ///     and stops. No operator restart needed.</item>
    /// </list>
    ///
    /// <para>Idempotency: <see cref="DatabaseInitializer.InitializeAsync"/> is safe to run more
    /// than once — EnsureDatabaseExists is a no-op when the DB exists and the migrator records
    /// applied versions, so the background success path runs exactly the work still needed.</para>
    /// </summary>
    public static class DatabaseStartup
    {
        /// <summary>
        /// Runs the inline bounded retry-with-backoff. Returns:
        ///   <list type="bullet">
        ///     <item><c>Initialized</c> — init succeeded (possibly after a few retries).</item>
        ///     <item><c>StillUnreachable</c> — exhausted retries, still a connectivity failure;
        ///       the caller should start the web host anyway and let the background self-heal
        ///       service take over.</item>
        ///     <item><c>Fatal</c> — a non-transient failure (auth/permission/schema); the error
        ///       was logged and re-thrown is NOT done here — caller already routes to /setup or
        ///       /db-offline as appropriate. Surfaced fast, never retried.</item>
        ///   </list>
        /// </summary>
        public static async Task<StartupInitResult> TryInitializeWithRetryAsync(
            DatabaseInitializer initializer,
            StartupRetryOptions options,
            ILogger logger,
            CancellationToken cancellationToken = default)
        {
            var attempt = 0;
            var delay = options.BaseDelay;

            while (true)
            {
                attempt++;
                try
                {
                    await initializer.InitializeAsync();
                    if (attempt > 1)
                    {
                        logger.LogInformation(
                            "Database reachable — initialization complete (after {Attempt} attempts).",
                            attempt);
                    }
                    return StartupInitResult.Initialized;
                }
                catch (SqlException ex) when (SqlConnectivity.IsTransient(ex))
                {
                    // Connectivity-class: worth waiting for. Out of inline attempts → hand off
                    // to the background self-heal service rather than blocking boot.
                    if (attempt >= options.MaxAttempts)
                    {
                        logger.LogWarning(
                            "DB init attempt {Attempt}/{Max} failed (unreachable: {Reason}); " +
                            "inline retries exhausted — starting anyway and self-healing in the background.",
                            attempt, options.MaxAttempts, ex.Message);
                        return StartupInitResult.StillUnreachable;
                    }

                    logger.LogWarning(
                        "DB init attempt {Attempt}/{Max} failed (unreachable), retrying in {Delay}s. ({Reason})",
                        attempt, options.MaxAttempts, (int)delay.TotalSeconds, ex.Message);

                    await Task.Delay(delay, cancellationToken);
                    delay = NextDelay(delay, options.MaxDelay);
                }
                catch (Exception ex)
                {
                    // Fatal / non-transient (auth 18456, permission, schema/migration error).
                    // Do NOT loop — waiting will not fix a misconfiguration. Surface fast & clear.
                    logger.LogError(ex,
                        "Database initialization failed with a fatal (non-connectivity) error on attempt {Attempt}; " +
                        "this will not self-heal — check credentials/permissions/schema. Routing to setup.",
                        attempt);
                    return StartupInitResult.Fatal;
                }
            }
        }

        /// <summary>Doubles the delay, capped. Pure so it's trivially testable.</summary>
        internal static TimeSpan NextDelay(TimeSpan current, TimeSpan cap)
        {
            var doubled = TimeSpan.FromTicks(current.Ticks * 2);
            return doubled > cap ? cap : doubled;
        }
    }

    /// <summary>Outcome of the inline startup retry.</summary>
    public enum StartupInitResult
    {
        Initialized,
        StillUnreachable,
        Fatal
    }

    /// <summary>
    /// Tiny shared signal between the inline startup retry (which runs after the host is built,
    /// inside a scope) and the <see cref="DatabaseSelfHealService"/> hosted service (which starts
    /// when the host runs). The hosted service is registered unconditionally but only does work
    /// when the inline retry flips this on — so it's a no-op on the happy path and on a fatal
    /// failure, and only self-heals when the DB was left unreachable at startup.
    /// </summary>
    public sealed class DatabaseSelfHealSignal
    {
        private int _needed; // 0 = not needed, 1 = self-heal requested

        public void Request() => Interlocked.Exchange(ref _needed, 1);

        public bool IsRequested => Volatile.Read(ref _needed) == 1;
    }

    /// <summary>
    /// Backoff knobs, bound from config section <c>Database:StartupRetry</c> with sane defaults
    /// (5 attempts, 2s base, 30s cap → 2s, 4s, 8s, 16s, then capped at 30s; background interval 20s).
    /// </summary>
    public sealed class StartupRetryOptions
    {
        public int MaxAttempts { get; set; } = 5;
        public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(2);
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan BackgroundInterval { get; set; } = TimeSpan.FromSeconds(20);

        public static StartupRetryOptions FromConfiguration(IConfiguration configuration)
        {
            var opts = new StartupRetryOptions();
            var section = configuration.GetSection("Database:StartupRetry");
            if (section.Exists())
            {
                opts.MaxAttempts = section.GetValue("MaxAttempts", opts.MaxAttempts);
                opts.BaseDelay = TimeSpan.FromSeconds(
                    section.GetValue("BaseDelaySeconds", opts.BaseDelay.TotalSeconds));
                opts.MaxDelay = TimeSpan.FromSeconds(
                    section.GetValue("MaxDelaySeconds", opts.MaxDelay.TotalSeconds));
                opts.BackgroundInterval = TimeSpan.FromSeconds(
                    section.GetValue("BackgroundIntervalSeconds", opts.BackgroundInterval.TotalSeconds));
            }

            // Guardrails: never let bad config disable the retry or busy-loop.
            if (opts.MaxAttempts < 1) opts.MaxAttempts = 1;
            if (opts.BaseDelay < TimeSpan.FromSeconds(1)) opts.BaseDelay = TimeSpan.FromSeconds(1);
            if (opts.MaxDelay < opts.BaseDelay) opts.MaxDelay = opts.BaseDelay;
            if (opts.BackgroundInterval < TimeSpan.FromSeconds(5)) opts.BackgroundInterval = TimeSpan.FromSeconds(5);
            return opts;
        }
    }

    /// <summary>
    /// Self-heal: when startup left the DB unreachable, keep retrying init at a capped interval
    /// until the DB returns, then run init once and stop. Registered only when the inline retry
    /// reported <see cref="StartupInitResult.StillUnreachable"/>, so it does no work on the happy
    /// path and is never registered for a fatal (auth/schema) failure.
    /// </summary>
    public sealed class DatabaseSelfHealService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly StartupRetryOptions _options;
        private readonly DatabaseSelfHealSignal _signal;
        private readonly ILogger<DatabaseSelfHealService> _logger;

        public DatabaseSelfHealService(
            IServiceScopeFactory scopeFactory,
            StartupRetryOptions options,
            DatabaseSelfHealSignal signal,
            ILogger<DatabaseSelfHealService> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options;
            _signal = signal;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // No-op unless the inline startup retry asked us to self-heal. The signal is set
            // synchronously in the post-build init block, before the host starts running hosted
            // services, so it is reliably observed here.
            if (!_signal.IsRequested)
            {
                return;
            }

            _logger.LogWarning(
                "Database self-heal active: retrying initialization every {Interval}s until the host returns. " +
                "Requests will land on /db-offline until then.",
                (int)_options.BackgroundInterval.TotalSeconds);

            var attempt = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_options.BackgroundInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                attempt++;
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
                    await initializer.InitializeAsync();

                    // Success: the migrations/EnsureDatabaseExists ran (idempotently). Clear the
                    // 5s status cache so the very next request re-evaluates as Ready/NotConfigured
                    // and normal routing resumes — same hook the post-setup path uses.
                    SetupMiddleware.ClearCache();

                    // Best-effort: warm the OpenAccessState singleton now that the DB is up.
                    try
                    {
                        var openAccess = scope.ServiceProvider.GetRequiredService<OpenAccessState>();
                        await openAccess.InitializeAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Self-heal succeeded but OpenAccessState warm-load failed; defaulting to disabled.");
                    }

                    _logger.LogInformation(
                        "Database reachable — initialization complete (self-heal, after {Attempt} background attempts). " +
                        "Normal routing resumed.",
                        attempt);
                    return; // stop retrying
                }
                catch (SqlException ex) when (SqlConnectivity.IsTransient(ex))
                {
                    _logger.LogWarning(
                        "Self-heal attempt {Attempt} failed (still unreachable), retrying in {Interval}s. ({Reason})",
                        attempt, (int)_options.BackgroundInterval.TotalSeconds, ex.Message);
                    // loop; keep serving /db-offline
                }
                catch (Exception ex)
                {
                    // The host came back but init now fails for a NON-connectivity reason (auth
                    // changed, schema/migration broke). This won't self-heal — surface and stop,
                    // exactly like the inline fatal path. Routing falls through to setup/error.
                    _logger.LogError(ex,
                        "Self-heal stopped: database became reachable but initialization failed with a fatal " +
                        "(non-connectivity) error on attempt {Attempt}. Check credentials/permissions/schema.",
                        attempt);
                    SetupMiddleware.ClearCache();
                    return;
                }
            }
        }
    }
}
