using System;
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
        /// Classifies the database into one of three states so callers can tell a transient
        /// outage from a genuine first-run:
        ///   <list type="bullet">
        ///     <item><see cref="DatabaseStatus.Ready"/> — reachable, schema present, an
        ///       active portal admin exists (or a marker file confirms prior setup).</item>
        ///     <item><see cref="DatabaseStatus.NotConfigured"/> — reachable, but no schema /
        ///       no admin yet. This is the legitimate first-run; route to /setup.</item>
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
            lock (_statusLock)
            {
                if (_cachedStatus.HasValue && DateTime.UtcNow - _cachedStatusAtUtc < StatusCacheTtl)
                {
                    return _cachedStatus.Value;
                }
            }

            var status = await ProbeDatabaseStatusAsync();

            lock (_statusLock)
            {
                _cachedStatus = status;
                _cachedStatusAtUtc = DateTime.UtcNow;
            }
            return status;
        }

        /// <summary>
        /// Invalidates the cached database status so the next probe runs immediately.
        /// Called after setup completes so the freshly-configured DB is recognized at once.
        /// </summary>
        /// <summary>
        /// Returns the SQL server/host portion of the configured connection string for
        /// display on the offline page. Never returns credentials. Empty if unknown.
        /// </summary>
        public string GetConfiguredServerName()
        {
            try
            {
                var cs = _configuration.GetConnectionString("DefaultConnection");
                if (string.IsNullOrWhiteSpace(cs)) return string.Empty;
                return new SqlConnectionStringBuilder(cs).DataSource ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

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
            var connectionString = _configuration.GetConnectionString("DefaultConnection");

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
                if (activeAdmins > 0)
                {
                    // Setup is effectively complete — backfill the marker file so any
                    // downstream code that still reads it agrees.
                    try { if (!File.Exists(_setupCompleteFile)) File.WriteAllText(_setupCompleteFile, DateTime.UtcNow.ToString("O")); }
                    catch { /* best-effort; harmless if it fails */ }
                    return DatabaseStatus.Ready;
                }

                // Reachable, schema present, but no active admin and no marker file →
                // still first-run; otherwise consider it set up.
                return File.Exists(_setupCompleteFile) ? DatabaseStatus.Ready : DatabaseStatus.NotConfigured;
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
        /// Updates only the database connection string in appsettings.{Environment}.json
        /// while preserving other sections. Also updates the live DatabaseConfig singleton.
        /// </summary>
        public async Task<(bool Success, string Message)> UpdateConnectionStringAsync(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return (false, "Connection string is empty.");
            }

            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
            var configFileName = environment == "Development"
                ? "appsettings.Development.json"
                : "appsettings.Production.json";
            var configPath = Path.Combine(Directory.GetCurrentDirectory(), configFileName);

            try
            {
                System.Text.Json.Nodes.JsonObject root;
                if (File.Exists(configPath))
                {
                    var existing = await File.ReadAllTextAsync(configPath);
                    root = string.IsNullOrWhiteSpace(existing)
                        ? new System.Text.Json.Nodes.JsonObject()
                        : System.Text.Json.Nodes.JsonNode.Parse(existing)?.AsObject() ?? new System.Text.Json.Nodes.JsonObject();
                }
                else
                {
                    root = new System.Text.Json.Nodes.JsonObject();
                }

                if (root["ConnectionStrings"] is not System.Text.Json.Nodes.JsonObject conn)
                {
                    conn = new System.Text.Json.Nodes.JsonObject();
                    root["ConnectionStrings"] = conn;
                }
                conn["DefaultConnection"] = connectionString;

                var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                await File.WriteAllTextAsync(configPath, root.ToJsonString(opts));

                _databaseConfig.SetConnectionString(connectionString);
                return (true, $"Connection string saved to {configFileName}.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update connection string in {ConfigPath}", configPath);
                return (false, $"Failed to save: {ex.Message}");
            }
        }

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

                var escapedName = databaseName.Replace("]", "]]");
                await Dapper.SqlMapper.ExecuteAsync(connection, $"CREATE DATABASE [{escapedName}]");
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
        /// Checks if a connection string is a placeholder/template value
        /// </summary>
        private static bool IsPlaceholderConnectionString(string connectionString)
        {
            var upper = connectionString.ToUpperInvariant();
            return upper.Contains("YOUR_SERVER") ||
                   upper.Contains("YOUR_USER") ||
                   upper.Contains("YOUR_PASSWORD") ||
                   upper.Contains("YOUR_DATABASE") ||
                   upper.Contains("(LOCALDB)") ||
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
        public async Task<bool> ApplySetupAsync(SetupConfiguration config)
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
        /// Updates the application configuration
        /// </summary>
        private async Task UpdateConfigurationAsync(SetupConfiguration config)
        {
            // Get the current environment
            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
            
            // Update the environment-specific config file
            var configFileName = environment == "Development" 
                ? "appsettings.Development.json" 
                : "appsettings.Production.json";
                
            var configPath = Path.Combine(Directory.GetCurrentDirectory(), configFileName);
            
            var settings = new
            {
                ConnectionStrings = new
                {
                    DefaultConnection = config.ConnectionString
                },
                Jwt = new
                {
                    SecretKey = config.JwtSecretKey,
                    Issuer = config.JwtIssuer ?? "Conduit",
                    Audience = config.JwtAudience ?? "ConduitAPI"
                },
                Kestrel = new
                {
                    Endpoints = new
                    {
                        Http = new
                        {
                            Url = $"http://localhost:{config.ServerPort}"
                        }
                    }
                }
            };

            var json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions 
            { 
                WriteIndented = true 
            });

            await File.WriteAllTextAsync(configPath, json);
        }

        /// <summary>
        /// Creates (or updates) the portal administrator account in the PortalAdmins table.
        /// As of migration v10 this is decoupled from the SCIM Users table so directory
        /// data ops can't invalidate the portal login.
        /// </summary>
        private async Task CreateAdminUserAsync(SetupConfiguration config)
        {
            var (hash, salt) = PasswordHasher.Hash(config.AdminPassword);

            // SECURITY (defense in depth): this path may only CREATE the first admin
            // during first-run. If any active admin already exists, setup is complete
            // and we must never silently UPDATE an existing admin's password from the
            // anonymous wizard. ApplySetupAsync already gates this, but we re-assert
            // here so the credential-overwrite branch can never be reached post-install
            // even if this method is ever called from a new path.
            if (await _repository.CountActiveAdminsAsync() > 0)
            {
                _logger.LogWarning("Rejected admin creation: an active portal admin already exists.");
                throw new SetupAlreadyCompletedException();
            }

            var existingId = await _repository.GetAdminIdByUserNameAsync(config.AdminUsername);

            if (existingId.HasValue)
            {
                await _repository.UpdateAdminPasswordAsync(existingId.Value, hash, salt);
                _logger.LogInformation("Updated existing portal admin: {Username}", config.AdminUsername);
            }
            else
            {
                await _repository.InsertAdminAsync(config.AdminUsername, config.AdminUsername, hash, salt);
                _logger.LogInformation("Created portal admin: {Username}", config.AdminUsername);
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

    /// <summary>
    /// Three-state classification of the configured database, used to keep a transient
    /// outage from being misread as a first-run.
    /// </summary>
    public enum DatabaseStatus
    {
        /// <summary>Reachable, schema present, prior setup confirmed → normal operation.</summary>
        Ready,
        /// <summary>Reachable but no schema/admin yet → legitimate first run; route to /setup.</summary>
        NotConfigured,
        /// <summary>Host down / connection cannot be opened → route to the offline page and retry.</summary>
        Unreachable
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