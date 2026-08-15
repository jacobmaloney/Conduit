using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;

namespace Conduit.Web.Services
{
    /// <summary>
    /// Service to handle initial setup and configuration
    /// </summary>
    public class SetupService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<SetupService> _logger;
        private readonly DatabaseConfig _databaseConfig;
        private readonly SetupRepository _repository;
        private readonly IHostEnvironment _env;

        // setup.complete is anchored to ContentRootPath so launching from `dotnet run`
        // (cwd = src/Conduit.Web) and launching from Visual Studio (cwd = bin/Debug/...)
        // both resolve to the same file. Without this, F5 in VS bounces the operator
        // back into /setup every restart because the marker file "disappears."
        private readonly string _setupCompleteFile;

        // Connect timeout (seconds) for the status probe. Must NOT undercut a healthy-but-slow
        // server: a 4s probe falsely reported "unreachable" when .56's pre-login handshake took
        // ~4.5s, routing every request to /db-offline even though the real init connection (15s)
        // connected fine and applied migrations. Match the default connection timeout so a slow
        // handshake is never mistaken for a dead host. A truly-down host (no TCP / "network path
        // not found") still fails in <1s at the transport layer, and the 5s status cache means at
        // most one probe per window pays the full wait.
        private const int ProbeConnectTimeoutSeconds = 15;

        // Brief status cache so a flood of requests/pollers against a dead host doesn't
        // re-hammer it (and spam logs). Short enough that recovery is detected promptly.
        private static readonly TimeSpan StatusCacheTtl = TimeSpan.FromSeconds(5);
        private static readonly object _statusLock = new();
        private static DatabaseStatus? _cachedStatus;
        private static DateTime _cachedStatusAtUtc = DateTime.MinValue;

        // RECOVERY ATTEMPT BUDGET.
        //
        // RecoverAdminAsync is invoked from a Blazor component, i.e. over the SignalR hub.
        // Hub invocations after the websocket upgrade are NOT HTTP requests, so neither
        // GlobalLimiter nor the "anon" policy in Program.cs — both of which partition on
        // HttpContext — ever sees one. Unbounded, an anonymous visitor gets unlimited submits
        // per circuit, each doing a blocking File.ReadAllText of the token file on a
        // threadpool thread and emitting a log line.
        //
        // The token's 256 bits already make brute force irrelevant; entropy says nothing
        // about the cost of an attempt, which is what this bounds.
        //
        // BE PRECISE ABOUT WHICH CONTROL DOES WHAT — this counter is the weaker half:
        //   - LOG volume is bounded by LogRecoveryRefusal, which is PROCESS-WIDE. That is the
        //     control actually doing the work, and it is the one that matters, because a log
        //     flood buries the single line an operator needs — the one naming the token file.
        //   - DISK reads are NOT meaningfully bounded by this counter. Resetting it costs one
        //     new circuit (a GET, a /_blazor/negotiate, a websocket), and the global limiter
        //     in Program.cs permits enough of those per minute to make the per-circuit divisor
        //     small. Treat this as a courtesy that stops a trivial single-socket loop, not as
        //     a rate limit. If the token read ever needs a real bound, it has to be
        //     process-wide like the logging is.
        //
        // Per CIRCUIT, not per component instance: SetupService is registered scoped and
        // Blazor Server scopes a DI container to the circuit, so navigating away from
        // /admin-recovery and back does not hand the caller a fresh budget. Reloading the
        // page does — deliberately, so a genuine operator who typo'd the token ten times is
        // never locked out. The increment is not interlocked, so parallel hub invocations on
        // one circuit can slip a few past the cap; that is acceptable precisely because this
        // is not the control the security argument rests on.
        public const int MaxRecoveryAttemptsPerCircuit = 10;
        private int _recoveryAttempts;

        // Refusal logging is aggregated to one line per window, process-wide — log volume is
        // a process resource, not a per-circuit one, so a caller cannot multiply it by opening
        // circuits.
        private static readonly TimeSpan RefusalLogWindow = TimeSpan.FromMinutes(1);
        private static readonly object _refusalLogLock = new();
        private static DateTime _refusalWindowStartUtc = DateTime.MinValue;
        private static int _refusalsSinceLastLine;

        // At most ONE status probe in flight for the whole process. The offline page polls
        // on a timer per circuit, so N open tabs would otherwise mean N simultaneous probes,
        // each parking a socket and a threadpool slot for the full connect timeout against a
        // host that is already known to be down. Static, not per-instance: SetupService is
        // resolved per scope, so an instance field gates nothing across circuits.
        private static readonly SemaphoreSlim _probeGate = new(1, 1);

        public SetupService(IConfiguration configuration, ILogger<SetupService> logger,
            DatabaseConfig databaseConfig, SetupRepository repository, IHostEnvironment env)
        {
            _configuration = configuration;
            _logger = logger;
            _databaseConfig = databaseConfig;
            _repository = repository;
            _env = env;
            _setupCompleteFile = Path.Combine(env.ContentRootPath, "setup.complete");
        }

        /// <summary>
        /// Checks if the application needs initial setup. The authoritative signal is
        /// "does an active portal admin exist?" — if at least one row is present in
        /// PortalAdmins we've been set up before regardless of where the marker file
        /// lives on disk. The marker file is still honored as a secondary positive
        /// signal so behavior is unchanged on legacy installs.
        /// </summary>
        public async Task<bool> IsSetupRequiredAsync()
        {
            // Setup is "required" only when the DB is reachable but not configured. An
            // unreachable DB is NOT a setup signal — see GetDatabaseStatusAsync.
            return await GetDatabaseStatusAsync() == DatabaseStatus.NotConfigured;
        }

        /// <summary>
        /// Classifies the database into one of four states so callers can tell a transient
        /// outage from a genuine first-run from a locked-out install:
        ///   <list type="bullet">
        ///     <item><see cref="DatabaseStatus.Ready"/> — reachable, schema present, an
        ///       active portal admin exists.</item>
        ///     <item><see cref="DatabaseStatus.NotConfigured"/> — reachable, but NO SCHEMA.
        ///       This is the legitimate first-run; route to /setup.</item>
        ///     <item><see cref="DatabaseStatus.NeedsAdminRecovery"/> — reachable, schema
        ///       present, zero ACTIVE portal admins. Route to /admin-recovery, which demands
        ///       proof of host access; NEVER the anonymous wizard.</item>
        ///     <item><see cref="DatabaseStatus.Unreachable"/> — the host is down / the
        ///       connection cannot be opened (network class). Route to the offline page and
        ///       retry; NEVER expose the setup wizard for this.</item>
        ///   </list>
        /// The result is cached for a few seconds so a dead host isn't re-probed on every
        /// request. The cache never latches: once the host returns, the next probe after
        /// TTL detects Ready/NotConfigured and the app proceeds normally.
        /// </summary>
        public async Task<DatabaseStatus> GetDatabaseStatusAsync()
        {
            if (TryReadCachedStatus(freshOnly: true, out var status))
            {
                return status;
            }

            // Skip rather than queue: a caller who arrives while a probe is running reuses
            // the last known status instead of opening a second connection.
            if (!await _probeGate.WaitAsync(0))
            {
                if (TryReadCachedStatus(freshOnly: false, out status))
                {
                    return status;
                }

                // Cold start: a probe is running but has never yet produced a status, so
                // there is nothing to reuse. Wait for the winner rather than guessing —
                // guessing "Unreachable" here would bounce concurrent first-hit requests on
                // a perfectly healthy install straight to /db-offline. This path still never
                // probes; it only reads what the winner wrote.
                await _probeGate.WaitAsync();
                _probeGate.Release();
                return TryReadCachedStatus(freshOnly: false, out status) ? status : DatabaseStatus.Unreachable;
            }

            try
            {
                // The previous holder may have just refreshed it while we were waiting.
                if (TryReadCachedStatus(freshOnly: true, out status))
                {
                    return status;
                }

                status = await ProbeDatabaseStatusAsync();

                lock (_statusLock)
                {
                    _cachedStatus = status;
                    _cachedStatusAtUtc = DateTime.UtcNow;
                }
                return status;
            }
            finally
            {
                _probeGate.Release();
            }
        }

        private static bool TryReadCachedStatus(bool freshOnly, out DatabaseStatus status)
        {
            lock (_statusLock)
            {
                if (_cachedStatus.HasValue &&
                    (!freshOnly || DateTime.UtcNow - _cachedStatusAtUtc < StatusCacheTtl))
                {
                    status = _cachedStatus.Value;
                    return true;
                }
            }

            status = DatabaseStatus.Unreachable;
            return false;
        }

        /// <summary>
        /// Invalidates the cached database status so the next probe runs immediately.
        /// Called after setup completes so the freshly-configured DB is recognized at once.
        /// </summary>
        /// <summary>
        /// Returns the SQL server/host portion of the EFFECTIVE connection string for
        /// display on the offline page. Never returns credentials. Empty if unknown.
        /// </summary>
        public string GetConfiguredServerName()
        {
            try
            {
                var cs = EffectiveConnectionString;
                if (string.IsNullOrWhiteSpace(cs)) return string.Empty;
                return new SqlConnectionStringBuilder(cs).DataSource ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// The connection string the application is ACTUALLY using, which is the
        /// <see cref="DatabaseConfig"/> singleton — not <see cref="IConfiguration"/>.
        ///
        /// The two are identical on every normal path: Program.cs seeds the singleton from
        /// IConfiguration at boot. They diverge only after a runtime
        /// <see cref="DatabaseConfig.SetConnectionString"/>, and reading IConfiguration
        /// there produced a genuine dead-end. On a true first run appsettings ships
        /// DefaultConnection as "", ApplySetupAsync repoints the singleton and writes
        /// secrets.json, but IConfiguration is frozen (reloadOnChange: false) and still
        /// holds "". The next probe short-circuited on the empty string and reported
        /// NotConfigured WITHOUT EVER ASKING THE DATABASE, dropping the operator back onto a
        /// blank wizard after a setup that had in fact succeeded — and the re-submit threw
        /// SetupAlreadyCompletedException, surfaced as "Setup failed. Check the logs."
        ///
        /// It also split a single probe against itself: the reachability half read
        /// IConfiguration while the admin-count half went through the repository, i.e. the
        /// singleton. One source of truth removes both problems.
        ///
        /// THE INVARIANT THIS DEPENDS ON, stated so it is preserved deliberately: the
        /// connection string is no longer immutable config — it is a MUTABLE SINGLETON WITH
        /// EXACTLY ONE GATED WRITER. That writer is
        /// <see cref="ApplySetupAsync"/>'s call to <see cref="DatabaseConfig.SetConnectionString"/>,
        /// reached only after the first-run gate has established that PortalAdmins holds no
        /// rows at all. Adding a second writer — a settings page, a repoint endpoint, a
        /// background job — silently widens every decision made from this property, including
        /// the setup gate itself. Don't add one without a gate of its own.
        ///
        /// CAVEAT on where the first-run value can come from:
        /// <see cref="ProvisioningService"/> feeds ApplySetupAsync from the <c>Provision:*</c>
        /// configuration section. secrets.json shadows that section only when it actually
        /// contains one, so an environment variable can reach it. That is bounded to the FIRST
        /// RUN — the gate below requires a virgin PortalAdmins table — but it means the initial
        /// value is host-environment-trusted, not file-trusted.
        /// </summary>
        private string? EffectiveConnectionString => _databaseConfig.ConnectionString;

        public static void ClearStatusCache()
        {
            lock (_statusLock)
            {
                _cachedStatus = null;
                _cachedStatusAtUtc = DateTime.MinValue;
            }
        }

        private async Task<DatabaseStatus> ProbeDatabaseStatusAsync()
        {
            var connectionString = EffectiveConnectionString;

            // No usable connection string at all → genuine first run.
            if (string.IsNullOrWhiteSpace(connectionString) || IsPlaceholderConnectionString(connectionString))
            {
                return DatabaseStatus.NotConfigured;
            }

            try
            {
                // 1. Schema present? (Throws DatabaseUnreachableException on connect failure.)
                var configured = await IsDatabaseConfiguredAsync(connectionString);
                if (!configured)
                {
                    return DatabaseStatus.NotConfigured;
                }

                // 2. Active portal admin present? This is the real signal — the operator
                //    can sign in only if there's a row here. If the table doesn't exist
                //    yet (pre-v10 schema) we treat that as "not configured" since the
                //    next migration will create it.
                var hasTable = await _repository.PortalAdminsTableExistsAsync();
                if (!hasTable)
                {
                    return DatabaseStatus.NotConfigured;
                }
                var activeAdmins = await _repository.CountActiveAdminsAsync();
                var status = ClassifyReachableDatabase(schemaPresent: true, portalAdminsTableExists: true, activeAdmins);

                if (status == DatabaseStatus.Ready)
                {
                    // An outstanding recovery token is a STANDING CREDENTIAL for the next
                    // zero-admin event, whenever that happens. The only other deletion is on
                    // a successful recovery, so an operator who repaired the install some
                    // other way — a restore, a fresh PrepareDatabaseAsync, a repoint — used
                    // to leave a live token on disk indefinitely, and in every backup of the
                    // data directory taken since. An admin exists again; the token has no job
                    // left. Destroy it the moment we observe that.
                    RetireRecoveryToken();

                    // Setup is effectively complete — backfill the marker file so any
                    // downstream code that still reads it agrees. This is a WRITE only; the
                    // marker file never decides a returned status. See
                    // ClassifyReachableDatabase, which cannot see the file at all.
                    try { if (!File.Exists(_setupCompleteFile)) File.WriteAllText(_setupCompleteFile, DateTime.UtcNow.ToString("O")); }
                    catch { /* best-effort; harmless if it fails */ }
                    return DatabaseStatus.Ready;
                }

                // Reachable, schema present, but NO active portal admin. This is never
                // "Ready" (nobody can sign in) and it is never "NotConfigured" either.
                //
                // Reporting NotConfigured here is the mistake this status exists to prevent.
                // NotConfigured routes to the ANONYMOUS /setup wizard, so before that change
                // the wizard was unreachable on any install that had ever finished setup,
                // and after it the wizard was reachable. That is a widening, and it was
                // worse than "an anonymous visitor can create an admin": the wizard's admin
                // step used to resolve an existing row by username with no Active filter and
                // UPDATE it with Active = 1, so typing a DEACTIVATED admin's name
                // reactivated that named identity and set its password — a takeover of a
                // real account, audit history and all. Worse still, the startup safety net
                // MANUFACTURES this state by design: it migrates a fresh database, which by
                // definition has zero admins.
                //
                // Creating the first admin therefore requires proof of HOST access, not
                // network reach. Issue the one-time token now (idempotent — a token already
                // outstanding is left alone) and route to the /admin-recovery dead end.
                TryIssueRecoveryToken();
                return status;
            }
            catch (DatabaseUnreachableException ex)
            {
                // ONE clean WARN line — no cascading stack trace storm. The status cache
                // suppresses repeats for the TTL window.
                _logger.LogWarning("Database unreachable: {Server} — retrying. ({Reason})",
                    ex.Server, ex.InnerException?.Message ?? ex.Message);
                return DatabaseStatus.Unreachable;
            }
            catch (Exception ex)
            {
                // Unexpected non-connect failure: don't expose setup on a mystery error and
                // don't latch. Treat as Unreachable so we retry rather than wiping into the
                // wizard or throwing into the request pipeline.
                _logger.LogWarning(ex, "Unexpected error probing database status — treating as unreachable and retrying.");
                return DatabaseStatus.Unreachable;
            }
        }

        /// <summary>
        /// THE STATUS DECISION, isolated from all I/O so it can be tested directly.
        /// <see cref="ProbeDatabaseStatusAsync"/> calls exactly this once it has connected.
        ///
        /// Note what is NOT a parameter: the setup.complete marker file. It is anchored to
        /// ContentRootPath, so it survives a database repoint and exists on every install
        /// that ever finished setup — letting it influence this decision reported "schema
        /// present + zero admins" as Ready, which locked operators out of an install nobody
        /// could sign in to. The file is written as a breadcrumb and never read back here.
        /// </summary>
        public static DatabaseStatus ClassifyReachableDatabase(
            bool schemaPresent, bool portalAdminsTableExists, int activeAdmins)
        {
            // No schema, or a schema too old to have PortalAdmins, is a genuine first run:
            // there is nothing here to take over and nothing to lock anyone out of.
            if (!schemaPresent || !portalAdminsTableExists)
            {
                return DatabaseStatus.NotConfigured;
            }

            // Schema present and somebody can sign in.
            if (activeAdmins > 0)
            {
                return DatabaseStatus.Ready;
            }

            // Schema present, nobody can sign in. NOT NotConfigured — that routes to the
            // anonymous wizard, which is the widening this whole status exists to close.
            return DatabaseStatus.NeedsAdminRecovery;
        }

        /// <summary>
        /// THE FIRST-RUN GATE, isolated from all I/O. Any PortalAdmins row at all — active
        /// or deactivated — means this install has a history and setup does not run again.
        /// </summary>
        public static SetupGateDecision DecideSetupGate(bool portalAdminsTableExists, int totalAdminRows)
        {
            if (!portalAdminsTableExists)
            {
                return SetupGateDecision.Allow;
            }

            // Deliberately NOT "active rows". A table full of deactivated admins is a
            // populated table; treating it as a first run let an anonymous caller rewrite
            // the connection string, the JWT signing key, and the admin credentials of a
            // live install.
            return totalAdminRows > 0
                ? SetupGateDecision.RefuseTablePopulated
                : SetupGateDecision.Allow;
        }

        /// <summary>
        /// Whether the effective connection string could name a database that holds an admin
        /// history AT ALL. No server named means no database has been named yet — which is
        /// precisely the genuine first run the wizard exists to serve.
        ///
        /// This is deliberately NOT "the database is unreachable ⇒ allow". Unreachable is a
        /// live install whose SQL host happens to be down, and treating that as a first run
        /// would let anyone who can knock the SQL host offline re-open the anonymous wizard
        /// against a real install. <see cref="ProbeDatabaseStatusAsync"/> refuses Unreachable
        /// for exactly that reason; this must not undo it. The distinction is "no database
        /// was ever configured" versus "the configured database did not answer."
        ///
        /// IT ALSO MUST NOT USE <see cref="IsPlaceholderConnectionString"/>, which is a
        /// substring match over the WHOLE RAW STRING and counts "**" as template text. That
        /// is fine for the display/routing decisions it was written for and catastrophic
        /// here: a SQL password containing two adjacent asterisks would make a real, working,
        /// POPULATED install look unconfigured, and the caller skips the query that refuses
        /// setup on a populated database — handing an anonymous visitor the connection
        /// string, the JWT signing key and the admin credentials of a live install. A
        /// password can never appear in DataSource or InitialCatalog, so parse first and
        /// judge only those two fields. Do not "simplify" this back to the raw-string check.
        /// </summary>
        public static bool CouldHoldAdminHistory(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return false;
            }

            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);

                // No server, or a template server, means nothing can be connected to and so
                // nothing can hold a history.
                if (string.IsNullOrWhiteSpace(builder.DataSource) || IsTemplateValue(builder.DataSource))
                {
                    return false;
                }

                // A template DATABASE name is equally unusable. An ABSENT one is not: the
                // connection then lands on the login's default database, which can very much
                // hold an admin history.
                return !IsTemplateValue(builder.InitialCatalog);
            }
            catch
            {
                // Unparseable. That is a broken configuration, not a first run. Fail CLOSED:
                // claim it could hold a history so the caller queries, and let the query's own
                // failure surface rather than converting it into a pass.
                return true;
            }
        }

        /// <summary>Template text in a parsed server or database name. Never sees a password.</summary>
        private static bool IsTemplateValue(string? value) =>
            !string.IsNullOrEmpty(value) &&
            value.Contains("YOUR_", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// THE FIRST-RUN GATE INCLUDING ITS I/O PRECONDITION.
        ///
        /// <paramref name="readAdminHistory"/> is a delegate rather than a direct repository
        /// call so the fresh-install case can be proven to reach <see cref="SetupGateDecision.Allow"/>
        /// WITHOUT the reader ever running — i.e. without a connection being opened. That is
        /// not a testing nicety: on a true first run there is no connection to open, and a
        /// gate that queried unconditionally threw out of both the wizard submit and the
        /// installer's startup provisioning.
        /// </summary>
        public static async Task<SetupGateDecision> EvaluateSetupGateAsync(
            string? effectiveConnectionString,
            Func<Task<(bool TableExists, int TotalAdminRows)>> readAdminHistory)
        {
            if (!CouldHoldAdminHistory(effectiveConnectionString))
            {
                return SetupGateDecision.Allow;
            }

            var (tableExists, totalAdminRows) = await readAdminHistory();
            return DecideSetupGate(tableExists, totalAdminRows);
        }

        /// <summary>
        /// THE RECOVERY ADMISSION DECISION, isolated from all I/O so every refusal REASON
        /// can be asserted directly. <see cref="RecoverAdminAsync"/> calls exactly this and
        /// then only performs the I/O the decision authorizes.
        ///
        /// Order matters and is part of the contract: the token is checked FIRST, so a
        /// caller without host access learns nothing about the install's state or about
        /// which usernames are taken.
        /// </summary>
        public static RecoveryRefusal DecideRecovery(
            bool tokenValid, DatabaseStatus status, string? userName, string? password, bool userNameExists)
        {
            if (!tokenValid)
            {
                return RecoveryRefusal.InvalidToken;
            }

            if (status != DatabaseStatus.NeedsAdminRecovery)
            {
                return RecoveryRefusal.NotInRecoveryState;
            }

            if (string.IsNullOrWhiteSpace(userName))
            {
                return RecoveryRefusal.UserNameRequired;
            }

            if (string.IsNullOrEmpty(password) || password.Length < 8)
            {
                return RecoveryRefusal.PasswordTooShort;
            }

            // INSERT-ONLY. A collision is a hard refusal, never a branch into an UPDATE:
            // the colliding row may be a deactivated account belonging to someone else, and
            // reactivating it would hand an anonymous caller a real identity complete with
            // its audit history. That is exactly the takeover this path was built to close.
            return userNameExists
                ? RecoveryRefusal.UserNameTaken
                : RecoveryRefusal.None;
        }

        /// <summary>
        /// THE ATTEMPT-BUDGET DECISION, isolated from all state.
        /// See <see cref="MaxRecoveryAttemptsPerCircuit"/> for why the budget exists at all —
        /// the entropy of the token is not in question, the cost of an unmetered hub
        /// invocation is.
        /// </summary>
        public static bool IsRecoveryAttemptAllowed(int priorAttempts) =>
            priorAttempts < MaxRecoveryAttemptsPerCircuit;

        /// <summary>
        /// One refusal line per <see cref="RefusalLogWindow"/>, carrying the count of the
        /// attempts it stands in for. A line per attempt let an anonymous caller flood the log
        /// over the hub and bury the line the operator actually needs — the one naming the
        /// token file. The first refusal after a quiet window is always logged immediately.
        /// </summary>
        private void LogRecoveryRefusal(RecoveryRefusal refusal)
        {
            int suppressed;
            lock (_refusalLogLock)
            {
                var now = DateTime.UtcNow;
                if (now - _refusalWindowStartUtc < RefusalLogWindow)
                {
                    _refusalsSinceLastLine++;
                    return;
                }

                suppressed = _refusalsSinceLastLine;
                _refusalsSinceLastLine = 0;
                _refusalWindowStartUtc = now;
            }

            if (suppressed > 0)
            {
                _logger.LogWarning(
                    "Rejected admin recovery: {Refusal}. A further {Suppressed} attempt(s) were refused and not logged individually.",
                    refusal, suppressed);
            }
            else
            {
                _logger.LogWarning("Rejected admin recovery: {Refusal}.", refusal);
            }
        }

        /// <summary>
        /// Writes the one-time recovery token if none is outstanding. Best-effort: an
        /// install whose data directory cannot be written is already broken in ways this
        /// probe cannot fix, and throwing here would turn a status check into an outage.
        /// The token's ABSENCE is fail-closed — <see cref="RecoverAdminAsync"/> refuses when
        /// there is nothing to validate against, so a failed write blocks recovery rather
        /// than opening it.
        /// </summary>
        private void TryIssueRecoveryToken()
        {
            try
            {
                if (AdminRecoveryToken.IsOutstanding(RecoveryTokenPathOverride))
                {
                    return;
                }

                AdminRecoveryToken.EnsureIssued(RecoveryTokenPathOverride);
                _logger.LogWarning(
                    "The configured database has a schema but ZERO active portal admins. Portal sign-in is impossible until an " +
                    "admin is recreated, and that is not an anonymous action: a one-time recovery token has been written to '{Path}' " +
                    "(ACL-restricted, same protection as secrets.json). Open that file on this host and paste the token at /admin-recovery. " +
                    "It is valid for {Hours} hour(s); a fresh one is issued automatically while this state persists.",
                    AdminRecoveryToken.DefaultPath, AdminRecoveryToken.Lifetime.TotalHours);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Could not write the admin-recovery token. Recovery stays BLOCKED until this is resolved — " +
                    "creating a portal admin requires presenting a token that only a host-local reader can obtain.");
            }
        }

        /// <summary>
        /// Destroys an outstanding recovery token because the install no longer needs one.
        /// Called when the probe observes <see cref="DatabaseStatus.Ready"/> — an active admin
        /// exists, so nothing the token authorizes can succeed, and leaving it on disk only
        /// widens the window in which a copy of the data directory carries a live credential.
        /// </summary>
        private void RetireRecoveryToken()
        {
            // Consume, not IsOutstanding-then-Consume: an EXPIRED token file is already inert
            // but there is no reason to leave it on disk either, and IsOutstanding is false
            // for it. The return value keeps the log line honest on the common path, where
            // there is no file and this is a single File.Exists per probe.
            if (!AdminRecoveryToken.Consume(RecoveryTokenPathOverride))
            {
                return;
            }

            _logger.LogInformation(
                "An active portal admin is present again — the outstanding admin-recovery token has been destroyed. " +
                "A new one is issued only if this install loses all of its administrators again.");
        }

        /// <summary>Test seam: point the recovery token at somewhere other than the real data dir.</summary>
        public string? RecoveryTokenPathOverride { get; set; }

        /// <summary>
        /// Tests a connection string and reports whether the server is reachable,
        /// whether the target database exists, and whether the schema is present.
        /// </summary>
        public async Task<ConnectionTestResult> TestConnectionAsync(string connectionString)
        {
            var result = new ConnectionTestResult();
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                result.ErrorMessage = "Connection string is empty.";
                return result;
            }

            SqlConnectionStringBuilder builder;
            try
            {
                builder = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 5 };
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Invalid connection string: {ex.Message}";
                return result;
            }

            result.DatabaseName = builder.InitialCatalog;
            var targetBuilder = new SqlConnectionStringBuilder(builder.ConnectionString);
            builder.InitialCatalog = "master";

            try
            {
                using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                result.ServerReachable = true;

                if (string.IsNullOrWhiteSpace(result.DatabaseName))
                {
                    result.ErrorMessage = "No database name specified in connection string.";
                    return result;
                }

                var count = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM sys.databases WHERE name = @name",
                    new { name = result.DatabaseName });
                result.DatabaseExists = count > 0;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                return result;
            }

            if (!result.DatabaseExists)
            {
                return result;
            }

            try
            {
                using var dbConnection = new SqlConnection(targetBuilder.ConnectionString);
                await dbConnection.OpenAsync();
                var tableCount = await dbConnection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Users'");
                result.SchemaExists = tableCount > 0;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Database '{result.DatabaseName}' exists but could not be opened: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// PERSISTS the database connection string to the ACL-restricted secrets.json
        /// (read-merge-rewrite; never the world-readable appsettings files). It does NOT
        /// repoint the running application — the operator restarts to adopt it.
        ///
        /// There used to be an <c>applyToRunningApp</c> flag here that also called
        /// <see cref="DatabaseConfig.SetConnectionString"/>. It had no callers left, and its
        /// documentation described wiring that no longer existed: the /database-settings
        /// caller was replaced by <see cref="PrepareDatabaseAsync"/> (which deliberately
        /// never repoints the live singleton), and first-run setup calls SetConnectionString
        /// directly in <see cref="ApplySetupAsync"/>. A dead branch carrying a comment that
        /// misdescribes the system is the next two-hour bug, so it is gone.
        /// </summary>
        public Task<(bool Success, string Message)> UpdateConnectionStringAsync(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return Task.FromResult((false, "Connection string is empty."));
            }

            try
            {
                SecretsFile.Update(root =>
                {
                    if (root["ConnectionStrings"] is not System.Text.Json.Nodes.JsonObject conn)
                    {
                        conn = new System.Text.Json.Nodes.JsonObject();
                        root["ConnectionStrings"] = conn;
                    }
                    conn["DefaultConnection"] = connectionString;
                });

                return Task.FromResult((true, "Connection string saved to the restricted secrets store (secrets.json)."));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update the connection string in secrets.json");
                return Task.FromResult((false, $"Failed to save: {ex.Message}"));
            }
        }

        /// <summary>
        /// The one authenticated action behind "type a new database name, create, restart,
        /// and it's set up." Creates the database, builds the full schema, seeds exactly one
        /// portal admin, and only then persists the connection string + provisioning marker.
        ///
        /// Everything happens NOW, while the operator is authenticated and can see errors —
        /// nothing is deferred to a post-restart anonymous wizard. Save is an inseparable
        /// final step, so there is no longer any way to be told "restart" without having
        /// saved, and a failed prepare never writes the connection string, so a broken
        /// database can never be waiting for the operator after a restart.
        ///
        /// CRITICAL: this deliberately does NOT repoint the live <see cref="DatabaseConfig"/>
        /// and does NOT call <see cref="ApplySetupAsync"/>. The running application keeps
        /// serving every active user from its CURRENT database until the operator restarts.
        /// The schema is built through a DETACHED DatabaseConfig aimed at the new connection.
        ///
        /// <paramref name="onProgress"/> is invoked before and after each step so the caller
        /// can render per-step progress; it is never passed credential material.
        /// </summary>
        public async Task<PrepareDatabaseResult> PrepareDatabaseAsync(
            PrepareDatabaseRequest request, System.Security.Claims.ClaimsPrincipal user,
            Func<PrepareDatabaseResult, Task>? onProgress = null)
        {
            // SECURITY: the account whose credential may be copied is derived HERE, from the
            // authenticated principal — it is never accepted from the caller. Taking a
            // username as input would mean any future caller could name an arbitrary admin
            // and have their password hash copied into a database of the caller's choosing.
            // The only account this method will ever copy is the one the request is
            // authenticated as.
            var currentAdminUserName = user.Identity?.IsAuthenticated == true
                ? user.Identity.Name
                : null;

            var result = new PrepareDatabaseResult();
            var createStep = result.AddStep(PrepareStep.CreateDatabase, "Create the database");
            var schemaStep = result.AddStep(PrepareStep.BuildSchema, "Build the schema");
            var adminStep = result.AddStep(PrepareStep.SeedAdmin, "Set up the sign-in");
            var saveStep = result.AddStep(PrepareStep.SaveConnection, "Save the connection");

            SqlConnectionStringBuilder builder;
            try
            {
                builder = new SqlConnectionStringBuilder(request.ConnectionString);
            }
            catch (Exception ex)
            {
                return result.FailUpFront(createStep, $"Invalid connection string: {ex.Message}");
            }

            result.ServerName = builder.DataSource;
            result.DatabaseName = builder.InitialCatalog;

            if (string.IsNullOrWhiteSpace(result.DatabaseName))
            {
                return result.FailUpFront(createStep, "No database name specified in the connection string.");
            }

            // Validate the sign-in choice BEFORE creating anything. A database created and
            // migrated but left without an admin is a database nobody can sign in to.
            if (request.CopyCurrentAdmin)
            {
                if (string.IsNullOrWhiteSpace(currentAdminUserName))
                {
                    return result.FailUpFront(adminStep,
                        "Could not determine which portal admin you are signed in as. Choose 'A different admin' and supply credentials.");
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(request.NewAdminUserName))
                {
                    return result.FailUpFront(adminStep, "Admin username is required.");
                }
                if (string.IsNullOrWhiteSpace(request.NewAdminPassword) || request.NewAdminPassword.Length < 8)
                {
                    return result.FailUpFront(adminStep, "Password must be at least 8 characters long.");
                }
            }

            async Task<bool> RunStep(PrepareStepResult step, Func<Task<(PrepareStepState State, string Message)>> action)
            {
                step.State = PrepareStepState.Running;
                if (onProgress is not null) await onProgress(result);

                try
                {
                    var (state, message) = await action();
                    step.State = state;
                    step.Message = message;
                }
                catch (Exception ex)
                {
                    step.State = PrepareStepState.Failed;
                    step.Message = ex.Message;
                    _logger.LogError(ex, "Prepare-database step '{Step}' failed for {Server}/{Database}.",
                        step.Step, result.ServerName, result.DatabaseName);
                }

                if (onProgress is not null) await onProgress(result);
                return step.State != PrepareStepState.Failed;
            }

            if (!await RunStep(createStep, async () =>
            {
                var (ok, message) = await CreateDatabaseAsync(request.ConnectionString);
                return (ok ? PrepareStepState.Succeeded : PrepareStepState.Failed, message);
            }))
            {
                return result;
            }

            if (!await RunStep(schemaStep, async () =>
            {
                // DETACHED config — the live singleton is untouched, so the running app
                // stays on its current database while this builds the new one.
                var detached = new DatabaseConfig
                {
                    ConnectionString = request.ConnectionString,
                    AutoCreateDatabase = true,
                    AutoMigrate = true,
                    CommandTimeout = _databaseConfig.CommandTimeout
                };

                using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
                await new DatabaseInitializer(detached, loggerFactory.CreateLogger<DatabaseInitializer>())
                    .InitializeAsync();

                return (PrepareStepState.Succeeded, $"Schema built in '{result.DatabaseName}'.");
            }))
            {
                return result;
            }

            if (!await RunStep(adminStep, () => SeedPreparedAdminAsync(request, currentAdminUserName, result)))
            {
                return result;
            }

            if (!await RunStep(saveStep, async () =>
            {
                // Marker first: if it cannot be written the connection string is NOT
                // persisted, so a failure here leaves the operator on their working
                // database rather than pointed at one that will not self-migrate. A
                // marker whose database is never adopted authorizes nothing — startup
                // compares it against the configured connection and finds a mismatch.
                ProvisionedConnectionMarker.Write(request.ConnectionString);

                var (ok, message) = await UpdateConnectionStringAsync(request.ConnectionString);
                if (!ok)
                {
                    // Roll the marker back so it cannot outlive the connection string it
                    // describes. An orphan marker would be pinned against whatever the
                    // NEXT-highest config source names — environment or appsettings —
                    // which is lower integrity than the secret store the marker lives in.
                    ProvisionedConnectionMarker.Remove();
                }

                return (ok ? PrepareStepState.Succeeded : PrepareStepState.Failed, message);
            }))
            {
                return result;
            }

            result.Success = true;
            ClearStatusCache();
            _logger.LogInformation(
                "Prepared database {Database} on {Server}: schema built, portal admin seeded, connection saved. Restart required to adopt it.",
                result.DatabaseName, result.ServerName);
            return result;
        }

        /// <summary>
        /// Seeds exactly ONE portal admin into the freshly prepared database.
        ///
        /// SECURITY: the copy path reads only the single account the caller is
        /// authenticated as — never every row in PortalAdmins, which would leak accounts
        /// across a boundary the operator may not intend. Copying that one row is
        /// authorized precisely because the caller is authenticated in the SOURCE database
        /// at this moment. If the target already has an active admin this is a no-op:
        /// preparing a database never overwrites an existing credential.
        /// </summary>
        private async Task<(PrepareStepState State, string Message)> SeedPreparedAdminAsync(
            PrepareDatabaseRequest request, string? currentAdminUserName, PrepareDatabaseResult result)
        {
            using var target = new SqlConnection(request.ConnectionString);
            await target.OpenAsync();

            var existingAdmins = await target.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM PortalAdmins WHERE Active = 1");
            if (existingAdmins > 0)
            {
                // The target's own admins carry over, so the success banner must not claim
                // the operator signs in as an account this run created. Nothing was created.
                result.AdminUserName = null;
                result.ExistingAdminsRetained = existingAdmins;
                return (PrepareStepState.Skipped,
                    $"'{result.DatabaseName}' already has {existingAdmins} active portal admin(s) — existing sign-ins were left untouched.");
            }

            string userName, displayName, hash, salt;
            if (request.CopyCurrentAdmin)
            {
                var source = await _repository.GetActiveAdminCredentialAsync(currentAdminUserName!);
                if (source is null)
                {
                    return (PrepareStepState.Failed,
                        $"No active portal admin named '{currentAdminUserName}' exists in the current database, so there is nothing to copy. Choose 'A different admin' instead.");
                }

                userName = source.UserName;
                displayName = source.DisplayName ?? source.UserName;
                hash = source.PasswordHash;
                salt = source.PasswordSalt;
            }
            else
            {
                userName = request.NewAdminUserName!.Trim();
                displayName = userName;
                (hash, salt) = PasswordHasher.Hash(request.NewAdminPassword!);
            }

            try
            {
                await target.ExecuteAsync(@"
                    INSERT INTO [PortalAdmins] ([Id], [UserName], [DisplayName], [PasswordHash], [PasswordSalt], [Active])
                    VALUES (NEWID(), @UserName, @DisplayName, @Hash, @Salt, 1)",
                    new { UserName = userName, DisplayName = displayName, Hash = hash, Salt = salt });
            }
            catch (SqlException ex) when (IsUniqueUserNameViolation(ex))
            {
                // The target already holds this username on a DEACTIVATED row, which the
                // Active = 1 count above cannot see. Raw, this surfaced to the operator as
                // "Violation of UNIQUE KEY constraint 'UQ_PortalAdmins_UserName'" — true,
                // unactionable, and alarming. Say what to do instead. Never reactivate the
                // existing row: that is someone's real account.
                return (PrepareStepState.Failed,
                    $"'{result.DatabaseName}' already has an account named '{userName}' (it may be deactivated). " +
                    "Existing accounts are never reused or reactivated by this action — choose 'A different admin' " +
                    "and pick another username.");
            }

            result.AdminUserName = userName;
            result.AdminPasswordCarriedOver = request.CopyCurrentAdmin;
            _logger.LogInformation("Seeded portal admin '{UserName}' into prepared database {Database}.",
                userName, result.DatabaseName);

            return (PrepareStepState.Succeeded, request.CopyCurrentAdmin
                ? $"Copied your sign-in '{userName}'. Your current password still works."
                : $"Created portal admin '{userName}'.");
        }

        /// <summary>
        /// What the /setup wizard should show.
        ///
        /// Deliberately carries NEITHER the SQL password, NOR the SQL login, NOR the server
        /// hostname, NOR the database name. /setup is anonymous and it already offers an
        /// unauthenticated visitor a connection test and a CREATE DATABASE against a server
        /// they name. Handing that visitor the real hostname turns a blind probe into a
        /// targeted one, and the database name tells them exactly what to ask for. This is
        /// the same argument already applied to the SQL login below, and to
        /// GetConfiguredServerName on the offline page.
        /// </summary>
        public async Task<SetupPrefill> GetSetupPrefillAsync()
        {
            var prefill = new SetupPrefill();
            var connectionString = EffectiveConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString) || IsPlaceholderConnectionString(connectionString))
            {
                return prefill;
            }

            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                prefill.ConnectionString = connectionString;
                prefill.IntegratedSecurity = builder.IntegratedSecurity;
                prefill.TrustServerCertificate = builder.TrustServerCertificate;

                var encrypt = builder.Encrypt.ToString();
                prefill.Encrypt = !(encrypt.Equals("False", StringComparison.OrdinalIgnoreCase) ||
                                    encrypt.Equals("Optional", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return new SetupPrefill();
            }

            // The wizard rewrites the Kestrel port on completion, so carry the configured
            // one forward rather than resetting a customized install back to the default.
            var url = _configuration["Kestrel:Endpoints:Http:Url"];
            if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) && parsedUrl.Port > 0)
            {
                prefill.ServerPort = parsedUrl.Port;
            }

            var jwt = _configuration["Jwt:SecretKey"];
            if (!string.IsNullOrWhiteSpace(jwt) && jwt.Length >= 32)
            {
                // Reuse the existing signing key rather than rotating it — a rotation here
                // would invalidate every API token already issued against this install.
                prefill.JwtSecretKey = jwt;
            }

            try
            {
                if (await _repository.PortalAdminsTableExistsAsync() &&
                    await _repository.CountActiveAdminsAsync() == 0)
                {
                    // Schema present, nobody can sign in. This is NOT a wizard state — see
                    // DatabaseStatus.NeedsAdminRecovery. The flag survives only so /setup can
                    // recognize the state and refuse to render a form for it; the wizard's
                    // JWT key field, its "Generate New" button, and its submit path all live
                    // inside the branch this flag turns off.
                    prefill.RecoveryRequired = true;
                    prefill.JwtSecretKey = null;
                }
            }
            catch (Exception ex)
            {
                // Unreachable or no schema — the full wizard is the right answer.
                _logger.LogDebug(ex, "Could not classify the configured database for setup prefill; showing the full wizard.");
            }

            return prefill;
        }

        /// <summary>
        /// Creates the FIRST portal admin on an install that has a schema and zero active
        /// admins, authorized by the one-time host-local recovery token.
        ///
        /// This is the entire unauthenticated write surface of the recovery path, and it is
        /// deliberately narrow. It touches PortalAdmins and nothing else — no connection
        /// string, no JWT signing key, no Kestrel port, no schema. Compare
        /// <see cref="ApplySetupAsync"/>, which rewrites all of those and is therefore gated
        /// on there being no PortalAdmins rows at all.
        ///
        /// Four things must hold, checked in this order, and none of them are inferred:
        ///   1. the presented token matches the one-time file only a host-local reader can read;
        ///   2. the database really is schema-present with zero ACTIVE admins, re-checked
        ///      here rather than trusted from the caller;
        ///   3. the username collides with NO existing row, active or deactivated — this
        ///      path is insert-only and never reactivates an account;
        ///   4. the INSERT succeeds, with the unique-constraint race caught and rendered.
        /// </summary>
        public async Task<(bool Success, string Message)> RecoverAdminAsync(
            string? presentedToken, string userName, string password)
        {
            // Checked FIRST, ahead of the token read, because the read is the cost being
            // bounded. See MaxRecoveryAttemptsPerCircuit.
            if (!IsRecoveryAttemptAllowed(_recoveryAttempts))
            {
                LogRecoveryRefusal(RecoveryRefusal.TooManyAttempts);
                return (false, DescribeRefusal(RecoveryRefusal.TooManyAttempts, userName));
            }
            _recoveryAttempts++;

            var tokenValid = AdminRecoveryToken.Validate(presentedToken, RecoveryTokenPathOverride);

            // Short-circuit: never query the database — not even to count admins — for a
            // caller who has not proven host access.
            var status = tokenValid ? await GetDatabaseStatusAsync() : DatabaseStatus.NeedsAdminRecovery;
            userName = (userName ?? string.Empty).Trim();

            // The username-taken check is the only step that needs the database, so it is
            // evaluated only once everything cheaper has passed.
            var provisional = DecideRecovery(tokenValid, status, userName, password, userNameExists: false);
            var refusal = provisional == RecoveryRefusal.None
                ? DecideRecovery(tokenValid, status, userName, password,
                    userNameExists: await _repository.AdminUserNameExistsAsync(userName))
                : provisional;

            if (refusal != RecoveryRefusal.None)
            {
                LogRecoveryRefusal(refusal);
                return (false, DescribeRefusal(refusal, userName));
            }

            var (hash, salt) = PasswordHasher.Hash(password);
            try
            {
                await _repository.InsertAdminAsync(userName, userName, hash, salt);
            }
            catch (SqlException ex) when (IsUniqueUserNameViolation(ex))
            {
                // Lost the race against a concurrent insert of the same name. Same refusal.
                return (false,
                    $"An account named '{userName}' already exists in this database. Choose a different username.");
            }

            AdminRecoveryToken.Consume(RecoveryTokenPathOverride);
            ClearStatusCache();
            _logger.LogWarning(
                "Admin recovery completed: portal admin '{UserName}' was created against a host-local recovery token, " +
                "and that token has been consumed.", userName);

            return (true, $"Portal admin '{userName}' created. You can sign in now.");
        }

        /// <summary>
        /// Operator-facing text for a refusal. <see cref="RecoveryRefusal.InvalidToken"/>
        /// covers "no token outstanding", "wrong token", and "unreadable token file" with
        /// one message — a caller learns only that it did not work.
        /// </summary>
        private static string DescribeRefusal(RecoveryRefusal refusal, string userName) => refusal switch
        {
            RecoveryRefusal.InvalidToken =>
                "That recovery token is not valid. Re-read the token file on the Conduit host and try again.",
            RecoveryRefusal.NotInRecoveryState =>
                "This install no longer needs admin recovery. Go to the sign-in page.",
            RecoveryRefusal.UserNameRequired =>
                "A username is required.",
            RecoveryRefusal.PasswordTooShort =>
                "The password must be at least 8 characters long.",
            RecoveryRefusal.TooManyAttempts =>
                "Too many recovery attempts in this session. Reload this page and try again.",
            RecoveryRefusal.UserNameTaken =>
                $"An account named '{userName}' already exists in this database — possibly deactivated. " +
                "Recovery never reuses or reactivates an existing account, because that account may belong " +
                "to someone else and carries their history. Choose a different username.",
            _ => "Recovery could not be completed."
        };

        /// <summary>
        /// True for SQL Server's unique-index / unique-constraint violations (2627, 2601).
        /// PortalAdmins.UserName carries UQ_PortalAdmins_UserName, and the raw message
        /// names the constraint — accurate, unactionable, and the sort of thing that should
        /// never reach an operator verbatim.
        /// </summary>
        private static bool IsUniqueUserNameViolation(SqlException ex) =>
            ex.Number is 2627 or 2601;

        /// <summary>
        /// Creates the target database on the server identified by the connection string.
        /// No-op if the database already exists.
        /// </summary>
        public async Task<(bool Success, string Message)> CreateDatabaseAsync(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return (false, "Connection string is empty.");
            }

            SqlConnectionStringBuilder builder;
            try
            {
                builder = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 10 };
            }
            catch (Exception ex)
            {
                return (false, $"Invalid connection string: {ex.Message}");
            }

            var databaseName = builder.InitialCatalog;
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                return (false, "No database name specified in connection string.");
            }

            builder.InitialCatalog = "master";

            try
            {
                using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync();

                var exists = await Dapper.SqlMapper.QuerySingleAsync<int>(connection,
                    "SELECT COUNT(*) FROM sys.databases WHERE name = @name",
                    new { name = databaseName }) > 0;

                if (exists)
                {
                    return (true, $"Database '{databaseName}' already exists.");
                }

                await Dapper.SqlMapper.ExecuteAsync(connection,
                    $"CREATE DATABASE {SqlIdentifier.QuoteName(databaseName)}");
                _logger.LogInformation("Created database {DatabaseName}", databaseName);
                return (true, $"Database '{databaseName}' created.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create database {DatabaseName}", databaseName);
                return (false, $"Failed to create database: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if the database is properly configured (schema present).
        ///
        /// IMPORTANT: this method deliberately does NOT swallow connect/network failures.
        /// A failure to OPEN the probe connection (host down, login timeout, pre-login
        /// handshake) is a fundamentally different condition from "connected fine but the
        /// schema isn't there yet" and the two must not be conflated — conflating them is
        /// what used to dump a first-run operator into the setup wizard on a transient
        /// network blip. We rethrow connect-class SqlExceptions as
        /// <see cref="DatabaseUnreachableException"/> so callers can branch on them; a
        /// clean connect that simply shows no schema returns <c>false</c> as before.
        /// </summary>
        private async Task<bool> IsDatabaseConfiguredAsync(string connectionString)
        {
            // Short connect timeout on the status probe so a dead host fails fast (a few
            // seconds) instead of hanging the UI ~15s per request.
            var probeString = WithProbeTimeout(connectionString);

            var builder = new SqlConnectionStringBuilder(probeString);
            var databaseName = builder.InitialCatalog;
            builder.InitialCatalog = "master";

            // --- Phase 1: open against master. A failure HERE is a connectivity failure. ---
            SqlConnection connection;
            try
            {
                connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
            }
            catch (SqlException ex) when (IsConnectivityFailure(ex))
            {
                throw new DatabaseUnreachableException(builder.DataSource, ex);
            }

            try
            {
                using (connection)
                {
                    // Check if database exists
                    var count = await connection.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM sys.databases WHERE name = @name",
                        new { name = databaseName });
                    if (count <= 0)
                    {
                        // Connected fine, target DB simply not created yet → NotConfigured.
                        return false;
                    }
                }

                // --- Phase 2: open against the target DB. Opening an existing DB that
                //     rejects the connection (e.g. 4060) is also connectivity-class. ---
                builder.InitialCatalog = databaseName;
                SqlConnection dbConnection;
                try
                {
                    dbConnection = new SqlConnection(builder.ConnectionString);
                    await dbConnection.OpenAsync();
                }
                catch (SqlException ex) when (IsConnectivityFailure(ex))
                {
                    throw new DatabaseUnreachableException(builder.DataSource, ex);
                }

                using (dbConnection)
                {
                    var tableCount = await dbConnection.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Users'");
                    return tableCount > 0;
                }
            }
            catch (DatabaseUnreachableException)
            {
                throw; // already classified — let callers handle it
            }
            catch (Exception ex)
            {
                // A query against an already-open connection failed for a non-connect
                // reason. Treat as "not configured" (legacy behavior) rather than offline.
                _logger.LogError(ex, "Error checking database configuration");
                return false;
            }
        }

        /// <summary>
        /// Returns a short-connect-timeout variant of the supplied connection string so the
        /// status probe fails fast when the host is down instead of hanging on the default
        /// 15s timeout. Best-effort: if the string can't be parsed we hand it back as-is.
        /// </summary>
        private static string WithProbeTimeout(string connectionString)
        {
            try
            {
                return new SqlConnectionStringBuilder(connectionString)
                {
                    ConnectTimeout = ProbeConnectTimeoutSeconds
                }.ConnectionString;
            }
            catch
            {
                return connectionString;
            }
        }

        /// <summary>
        /// Decides whether a <see cref="SqlException"/> is a CONNECT/network-class failure
        /// (host unreachable, transport, pre-login handshake, login timeout, DB-open
        /// rejection) versus a genuine authentication failure or a query error against an
        /// already-open connection. Connectivity failures = the DB is "offline"; everything
        /// else is left to the normal not-configured / error paths.
        ///
        /// The error-number list itself lives in <see cref="SqlConnectivity.IsTransient"/>
        /// (Conduit.DataAccess) so SetupService and DatabaseInitializer share one source of
        /// truth. See that helper for the covered numbers (53/40/-2/10060/10061/11001/233/4060)
        /// and the explicit 18456 (auth) exclusion.
        /// </summary>
        private static bool IsConnectivityFailure(SqlException ex)
            => SqlConnectivity.IsTransient(ex);

        /// <summary>
        /// Checks if a connection string is a placeholder/template value.
        /// Public: SecretsRelocator applies the same test when deciding whether a
        /// ConnectionStrings:DefaultConnection value is worth migrating.
        /// </summary>
        /// <remarks>
        /// Matches TEMPLATE TEXT only. It must NOT match a real data source: both the
        /// wizard and /database-settings generate <c>Server=(localdb)\mssqllocaldb;…</c>
        /// for the LocalDB option, so treating "(localdb)" as a placeholder classified
        /// every genuine LocalDB install as NotConfigured forever — it could never reach
        /// Ready no matter how complete the setup was.
        /// </remarks>
        public static bool IsPlaceholderConnectionString(string connectionString)
        {
            var upper = connectionString.ToUpperInvariant();
            return upper.Contains("YOUR_SERVER") ||
                   upper.Contains("YOUR_USER") ||
                   upper.Contains("YOUR_PASSWORD") ||
                   upper.Contains("YOUR_DATABASE") ||
                   upper.Contains("**");
        }

        /// <summary>
        /// Validates the setup configuration
        /// </summary>
        public SetupValidationResult ValidateSetup(SetupConfiguration config)
        {
            var result = new SetupValidationResult();

            // Validate database connection
            if (string.IsNullOrWhiteSpace(config.ConnectionString))
            {
                result.AddError("ConnectionString", "Database connection string is required");
            }

            // Validate admin credentials
            if (string.IsNullOrWhiteSpace(config.AdminUsername))
            {
                result.AddError("AdminUsername", "Admin username is required");
            }

            if (string.IsNullOrWhiteSpace(config.AdminPassword))
            {
                result.AddError("AdminPassword", "Admin password is required");
            }
            else if (config.AdminPassword.Length < 8)
            {
                result.AddError("AdminPassword", "Password must be at least 8 characters long");
            }

            // Validate JWT configuration
            if (string.IsNullOrWhiteSpace(config.JwtSecretKey) || config.JwtSecretKey.Length < 32)
            {
                result.AddError("JwtSecretKey", "JWT secret key must be at least 32 characters long");
            }

            return result;
        }

        /// <summary>
        /// Applies the setup configuration
        /// </summary>
        public virtual async Task<bool> ApplySetupAsync(SetupConfiguration config)
        {
            // SECURITY: server-side first-run gate. The /setup route is intentionally
            // anonymous (first-run) and only client-side redirects away once complete.
            // A crafted circuit/SignalR client could dispatch this submit before the
            // redirect tears down the circuit, overwriting admin creds and rewriting
            // the connection string. Re-verify completion HERE, authoritatively, before
            // any state mutation. A second apply after completion is a hard refusal —
            // never a credential overwrite. The legitimate first run (setup IS required)
            // proceeds unchanged.
            if (!await IsSetupRequiredAsync())
            {
                _logger.LogWarning("Rejected ApplySetupAsync: setup is already complete. " +
                    "Refusing to overwrite admin credentials or connection string.");
                throw new SetupAlreadyCompletedException();
            }

            // A POPULATED PortalAdmins TABLE IS NOT A FIRST RUN, whatever the status says.
            //
            // The guard above and the one in CreateAdminUserAsync both used to count only
            // ACTIVE admins, so an install whose admins had all been deactivated read as a
            // clean first run — and this method rewrites the connection string, the JWT
            // signing key, and the Kestrel port, from an anonymous page. Any row at all,
            // active or not, means this install has a history behind it and setup does not
            // get to run again. Checked before ANY state mutation.
            await AssertPortalAdminsTableIsEmptyAsync();

            try
            {
                // Update the live singleton so the rest of the app uses the new connection string
                _databaseConfig.SetConnectionString(config.ConnectionString);
                _databaseConfig.AutoCreateDatabase = config.AutoCreateDatabase;
                _databaseConfig.AutoMigrate = true;

                // Create logger factory for DatabaseInitializer
                using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
                var dbLogger = loggerFactory.CreateLogger<DatabaseInitializer>();

                var initializer = new DatabaseInitializer(_databaseConfig, dbLogger);
                await initializer.InitializeAsync();

                // Update configuration
                await UpdateConfigurationAsync(config);

                // Create admin user
                await CreateAdminUserAsync(config);

                // Mark setup as complete
                await File.WriteAllTextAsync(_setupCompleteFile, DateTime.UtcNow.ToString("O"));

                // Without this the just-configured database keeps reading NotConfigured for
                // the remainder of the cache TTL, and the operator gets bounced back to the
                // wizard they just finished.
                ClearStatusCache();

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying setup configuration: {Message}", ex.Message);
                
                // Also log inner exception if present
                if (ex.InnerException != null)
                {
                    _logger.LogError(ex.InnerException, "Inner exception: {Message}", ex.InnerException.Message);
                }
                
                // Write error to console for debugging
                Console.WriteLine($"Setup error: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner error: {ex.InnerException.Message}");
                }
                
                return false;
            }
        }

        /// <summary>
        /// Persists the setup configuration. Secrets (connection string + JWT) go to
        /// the ACL-restricted secrets.json; the non-secret Kestrel/port block goes to
        /// the environment config file. Both writes are read-merge-rewrite — never a
        /// whole-file replacement — and the environment file is anchored to
        /// ContentRootPath, NEVER Directory.GetCurrentDirectory(): under the SCM the
        /// process cwd is System32 and a cwd-anchored write lands the file there.
        /// </summary>
        private async Task UpdateConfigurationAsync(SetupConfiguration config)
        {
            SecretsFile.Update(root => ApplySecrets(root, config));

            var configPath = BuildEnvironmentConfigPath(
                _env.ContentRootPath, Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));

            System.Text.Json.Nodes.JsonObject envRoot;
            if (File.Exists(configPath))
            {
                var existing = await File.ReadAllTextAsync(configPath);
                envRoot = string.IsNullOrWhiteSpace(existing)
                    ? new System.Text.Json.Nodes.JsonObject()
                    : System.Text.Json.Nodes.JsonNode.Parse(existing)?.AsObject() ?? new System.Text.Json.Nodes.JsonObject();
            }
            else
            {
                envRoot = new System.Text.Json.Nodes.JsonObject();
            }

            ApplyKestrelPort(envRoot, config.ServerPort);

            await File.WriteAllTextAsync(configPath,
                envRoot.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>The environment config file for the Kestrel block, anchored to the content root.</summary>
        public static string BuildEnvironmentConfigPath(string contentRootPath, string? environment)
        {
            var configFileName = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase)
                ? "appsettings.Development.json"
                : "appsettings.Production.json";
            return Path.Combine(contentRootPath, configFileName);
        }

        /// <summary>Merges the secret parts of setup into a secrets.json root (other content preserved).</summary>
        public static void ApplySecrets(System.Text.Json.Nodes.JsonObject root, SetupConfiguration config)
        {
            if (root["ConnectionStrings"] is not System.Text.Json.Nodes.JsonObject conn)
            {
                conn = new System.Text.Json.Nodes.JsonObject();
                root["ConnectionStrings"] = conn;
            }
            conn["DefaultConnection"] = config.ConnectionString;

            if (root["Jwt"] is not System.Text.Json.Nodes.JsonObject jwt)
            {
                jwt = new System.Text.Json.Nodes.JsonObject();
                root["Jwt"] = jwt;
            }
            jwt["SecretKey"] = config.JwtSecretKey;
            jwt["Issuer"] = config.JwtIssuer ?? "Conduit";
            jwt["Audience"] = config.JwtAudience ?? "ConduitAPI";
        }

        /// <summary>Merges the non-secret Kestrel endpoint into an environment config root (other content preserved).</summary>
        public static void ApplyKestrelPort(System.Text.Json.Nodes.JsonObject root, int serverPort)
        {
            if (root["Kestrel"] is not System.Text.Json.Nodes.JsonObject kestrel)
            {
                kestrel = new System.Text.Json.Nodes.JsonObject();
                root["Kestrel"] = kestrel;
            }
            if (kestrel["Endpoints"] is not System.Text.Json.Nodes.JsonObject endpoints)
            {
                endpoints = new System.Text.Json.Nodes.JsonObject();
                kestrel["Endpoints"] = endpoints;
            }
            if (endpoints["Http"] is not System.Text.Json.Nodes.JsonObject http)
            {
                http = new System.Text.Json.Nodes.JsonObject();
                endpoints["Http"] = http;
            }
            http["Url"] = $"http://localhost:{serverPort}";
        }

        /// <summary>
        /// Creates the FIRST portal administrator. INSERT ONLY — there is deliberately no
        /// update branch. As of migration v10 this is decoupled from the SCIM Users table so
        /// directory data ops can't invalidate the portal login.
        /// </summary>
        private async Task CreateAdminUserAsync(SetupConfiguration config)
        {
            // SECURITY (defense in depth): re-assert the empty-table precondition
            // immediately before the write, so this can never be reached post-install even
            // if it is called from a future path that skipped ApplySetupAsync's gate.
            //
            // This call is the one that always queries. ApplySetupAsync's earlier gate runs
            // before SetConnectionString and therefore passes without a query on a fresh
            // install; by here the connection string names the operator's chosen database and
            // the gate is evaluated against THAT database's real PortalAdmins table. So
            // pointing a fresh install at an already-populated database still refuses here,
            // before any admin row is written.
            await AssertPortalAdminsTableIsEmptyAsync();

            // There used to be a "resolve by username, UPDATE if found" branch here whose
            // UPDATE also set Active = 1. From an anonymous wizard, typing a deactivated
            // admin's name reactivated that identity and reset its password. Insert-only
            // removes the branch rather than guarding it; the unique constraint turns a
            // collision into a hard failure instead of a silent takeover.
            var (hash, salt) = PasswordHasher.Hash(config.AdminPassword);
            await _repository.InsertAdminAsync(config.AdminUsername, config.AdminUsername, hash, salt);
            _logger.LogInformation("Created portal admin: {Username}", config.AdminUsername);
        }

        /// <summary>
        /// Throws <see cref="SetupAlreadyCompletedException"/> when PortalAdmins holds ANY
        /// row, active or deactivated. A missing table is a genuine first run and passes.
        ///
        /// A NO-DATABASE-CONFIGURED install passes WITHOUT A QUERY. This is the first thing
        /// on the setup path that touches SQL — <see cref="IsSetupRequiredAsync"/> short-
        /// circuits on the empty string and never opens a connection — and on a true first
        /// run appsettings ships DefaultConnection as "". Querying through the repository
        /// there means <c>new SqlConnection("").Open()</c>, which throws InvalidOperationException,
        /// and it threw BEFORE ApplySetupAsync's try block and before the connection string
        /// was set. That killed both consumers: the wizard's submit handler caught only
        /// SetupAlreadyCompletedException, so an unhandled exception tore down the Blazor
        /// circuit and every legitimate first-run operator got a dead page; and the
        /// installer's startup provisioning threw out of the startup scope, so the process
        /// failed to start. The gate was correct and the path it protected was dead.
        /// </summary>
        private async Task AssertPortalAdminsTableIsEmptyAsync()
        {
            var existing = 0;
            var decision = await EvaluateSetupGateAsync(EffectiveConnectionString, async () =>
            {
                var exists = await _repository.PortalAdminsTableExistsAsync();
                existing = exists ? await _repository.CountAdminsAsync() : 0;
                return (exists, existing);
            });

            if (decision == SetupGateDecision.RefuseTablePopulated)
            {
                _logger.LogWarning(
                    "Rejected setup: PortalAdmins already holds {Count} row(s). A populated table is not a first run, " +
                    "even when every row is deactivated — refusing to rewrite admin credentials, the connection string, " +
                    "or the JWT signing key. Zero-admin recovery goes through /admin-recovery.", existing);
                throw new SetupAlreadyCompletedException();
            }
        }
    }

    /// <summary>
    /// Thrown when the setup wizard is re-submitted after setup is already complete.
    /// The /setup route is anonymous by design (first-run); this is the server-side
    /// gate that refuses a second apply so admin credentials and the connection string
    /// can never be overwritten post-install.
    /// </summary>
    public class SetupAlreadyCompletedException : InvalidOperationException
    {
        public SetupAlreadyCompletedException()
            : base("Setup has already been completed. Re-running setup is not permitted.") { }
    }

    /// <summary>Whether first-run setup may run at all. See SetupService.DecideSetupGate.</summary>
    public enum SetupGateDecision
    {
        /// <summary>No PortalAdmins table, or the table is genuinely empty — a true first run.</summary>
        Allow,
        /// <summary>PortalAdmins holds at least one row, active or deactivated. Not a first run.</summary>
        RefuseTablePopulated
    }

    /// <summary>
    /// Why a zero-admin recovery attempt was refused. Distinct values so a caller — and a
    /// test — can assert the REASON rather than a bare false.
    /// </summary>
    public enum RecoveryRefusal
    {
        /// <summary>Admitted.</summary>
        None,
        /// <summary>
        /// No outstanding token, wrong token, an EXPIRED token, or an unreadable token file.
        /// One reason for all of them — a caller learns only that it did not work.
        /// </summary>
        InvalidToken,
        /// <summary>
        /// This circuit has spent its attempt budget. Not a statement about the credentials
        /// presented; it is refused before they are read. See
        /// SetupService.MaxRecoveryAttemptsPerCircuit.
        /// </summary>
        TooManyAttempts,
        /// <summary>This install does not have a schema with zero active admins.</summary>
        NotInRecoveryState,
        UserNameRequired,
        PasswordTooShort,
        /// <summary>
        /// A row already carries this username, active or deactivated. Recovery is
        /// insert-only; it never reactivates.
        /// </summary>
        UserNameTaken
    }

    /// <summary>
    /// Four-state classification of the configured database, used to keep a transient
    /// outage from being misread as a first-run, and a locked-out install from being
    /// misread as either.
    /// </summary>
    public enum DatabaseStatus
    {
        /// <summary>Reachable, schema present, an active portal admin exists → normal operation.</summary>
        Ready,
        /// <summary>Reachable but NO SCHEMA → legitimate first run; route to /setup.</summary>
        NotConfigured,
        /// <summary>Host down / connection cannot be opened → route to the offline page and retry.</summary>
        Unreachable,
        /// <summary>
        /// Reachable, schema present, ZERO ACTIVE portal admins → route to /admin-recovery.
        ///
        /// Kept strictly distinct from <see cref="NotConfigured"/>. Folding the two together
        /// makes the anonymous /setup wizard reachable on an install that has already been
        /// set up, which is the widening this status exists to close. Do not route this to
        /// /setup, and do not let <c>IsSetupRequiredAsync</c> return true for it.
        /// </summary>
        NeedsAdminRecovery
    }

    /// <summary>
    /// Raised internally when the status probe cannot OPEN a connection to SQL Server
    /// (network-class SqlException). Distinct from a query failure against an already-open
    /// connection so the setup decision can branch "offline" vs "needs setup."
    /// </summary>
    public class DatabaseUnreachableException : Exception
    {
        public string Server { get; }

        public DatabaseUnreachableException(string server, Exception inner)
            : base($"Database server '{server}' is unreachable.", inner)
        {
            Server = server;
        }
    }

    /// <summary>The ordered steps of <see cref="SetupService.PrepareDatabaseAsync"/>.</summary>
    public enum PrepareStep
    {
        CreateDatabase,
        BuildSchema,
        SeedAdmin,
        SaveConnection
    }

    public enum PrepareStepState
    {
        Pending,
        Running,
        Succeeded,
        /// <summary>Not needed — e.g. the target already has an active portal admin.</summary>
        Skipped,
        Failed
    }

    /// <summary>One step's live state, rendered as the prepare action progresses.</summary>
    public class PrepareStepResult
    {
        public PrepareStepResult(PrepareStep step, string title)
        {
            Step = step;
            Title = title;
        }

        public PrepareStep Step { get; }
        public string Title { get; }
        public PrepareStepState State { get; set; } = PrepareStepState.Pending;
        public string? Message { get; set; }
    }

    /// <summary>
    /// What to prepare. Credential material is supplied only for the "different admin"
    /// choice; the copy path names an account and never carries a password.
    /// </summary>
    public class PrepareDatabaseRequest
    {
        public string ConnectionString { get; set; } = string.Empty;

        /// <summary>
        /// Copy the signed-in admin's stored credential into the new database. WHICH
        /// account that is comes from the authenticated principal passed alongside this
        /// request — deliberately not a field here, so a username can never be supplied
        /// as input.
        /// </summary>
        public bool CopyCurrentAdmin { get; set; } = true;

        public string? NewAdminUserName { get; set; }
        public string? NewAdminPassword { get; set; }
    }

    /// <summary>
    /// Outcome of a prepare run. <see cref="Success"/> is true only when the database
    /// exists, the schema is built, a portal admin can sign in, and the connection string
    /// has been persisted — the connection is never saved for a partial run.
    /// </summary>
    public class PrepareDatabaseResult
    {
        public bool Success { get; set; }
        public string ServerName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;

        /// <summary>
        /// The admin this run CREATED in the new database, or null when it created none.
        /// Never a password. Null with <see cref="ExistingAdminsRetained"/> set means the
        /// target already had its own admins and this run left them alone.
        /// </summary>
        public string? AdminUserName { get; set; }

        /// <summary>How many active admins the target already had, when none was created.</summary>
        public int ExistingAdminsRetained { get; set; }

        /// <summary>True when that admin's existing password carries over unchanged.</summary>
        public bool AdminPasswordCarriedOver { get; set; }

        public List<PrepareStepResult> Steps { get; } = new();

        public PrepareStepResult AddStep(PrepareStep step, string title)
        {
            var result = new PrepareStepResult(step, title);
            Steps.Add(result);
            return result;
        }

        /// <summary>Marks a validation failure detected before any state was mutated.</summary>
        public PrepareDatabaseResult FailUpFront(PrepareStepResult step, string message)
        {
            step.State = PrepareStepState.Failed;
            step.Message = message;
            return this;
        }

        public PrepareStepResult? FailedStep => Steps.FirstOrDefault(s => s.State == PrepareStepState.Failed);
    }

    /// <summary>
    /// Non-secret connection SHAPE the app has already resolved (auth mode, TLS flags), so
    /// the /setup wizard's checkboxes start where the install actually is.
    ///
    /// Deliberately carries NONE of: the SQL password, the SQL login name, the server
    /// hostname, or the database name. /setup is anonymous and already offers an
    /// unauthenticated visitor a connection test and a CREATE DATABASE against a server
    /// they name; handing them the real hostname and database turns a blind probe into a
    /// targeted one, and the login would turn it into a password spray against a known
    /// account. Those are the things worth re-typing.
    /// </summary>
    public class SetupPrefill
    {
        /// <summary>The resolved connection string. Used server-side; never rendered.</summary>
        public string? ConnectionString { get; set; }

        public bool IntegratedSecurity { get; set; }
        public bool TrustServerCertificate { get; set; }
        public bool Encrypt { get; set; }
        public string? JwtSecretKey { get; set; }

        /// <summary>The configured Kestrel port, so completing setup does not reset it.</summary>
        public int? ServerPort { get; set; }

        /// <summary>
        /// The configured database has a schema and zero active admins. /setup renders NO
        /// FORM in this state — not a narrowed one. Creating the first admin here requires
        /// the host-local recovery token and happens at /admin-recovery.
        /// </summary>
        public bool RecoveryRequired { get; set; }

        public bool HasConnection => !string.IsNullOrWhiteSpace(ConnectionString);
    }

    /// <summary>
    /// Setup configuration model
    /// </summary>
    public class SetupConfiguration
    {
        public string ConnectionString { get; set; } = string.Empty;
        public string AdminUsername { get; set; } = "admin";
        public string AdminPassword { get; set; } = string.Empty;
        public string JwtSecretKey { get; set; } = string.Empty;
        public string? JwtIssuer { get; set; }
        public string? JwtAudience { get; set; }
        public int ServerPort { get; set; } = 5500;
        public bool UseHttps { get; set; } = false;
        public bool AutoCreateDatabase { get; set; } = true;
    }

    /// <summary>
    /// Result of probing a database connection string.
    /// </summary>
    public class ConnectionTestResult
    {
        public bool ServerReachable { get; set; }
        public bool DatabaseExists { get; set; }
        public bool SchemaExists { get; set; }
        public string DatabaseName { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Setup validation result
    /// </summary>
    public class SetupValidationResult
    {
        public bool IsValid => Errors.Count == 0;
        public Dictionary<string, List<string>> Errors { get; } = new();

        public void AddError(string field, string message)
        {
            if (!Errors.ContainsKey(field))
            {
                Errors[field] = new List<string>();
            }
            Errors[field].Add(message);
        }
    }
}