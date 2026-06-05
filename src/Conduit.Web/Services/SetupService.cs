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
            var connectionString = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(connectionString) || IsPlaceholderConnectionString(connectionString))
            {
                return true;
            }

            try
            {
                // 1. Schema present?
                var configured = await IsDatabaseConfiguredAsync(connectionString);
                if (!configured)
                {
                    return true;
                }

                // 2. Active portal admin present? This is the real signal — the operator
                //    can sign in only if there's a row here. If the table doesn't exist
                //    yet (pre-v10 schema) we treat that as "setup required" since the
                //    next migration will create it.
                var hasTable = await _repository.PortalAdminsTableExistsAsync();
                if (!hasTable)
                {
                    return true;
                }
                var activeAdmins = await _repository.CountActiveAdminsAsync();
                if (activeAdmins > 0)
                {
                    // Setup is effectively complete — backfill the marker file so any
                    // downstream code that still reads it agrees.
                    try { if (!File.Exists(_setupCompleteFile)) File.WriteAllText(_setupCompleteFile, DateTime.UtcNow.ToString("O")); }
                    catch { /* best-effort; harmless if it fails */ }
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not connect to database, setup required");
                return true;
            }

            // No active admin and no marker file → run the wizard.
            return !File.Exists(_setupCompleteFile);
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
        /// Checks if the database is properly configured
        /// </summary>
        private async Task<bool> IsDatabaseConfiguredAsync(string connectionString)
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                var databaseName = builder.InitialCatalog;
                builder.InitialCatalog = "master";

                using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync();

                // Check if database exists
                var count = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM sys.databases WHERE name = @name",
                    new { name = databaseName });
                if (count <= 0)
                {
                    return false;
                }

                // Check if tables exist
                builder.InitialCatalog = databaseName;
                using var dbConnection = new SqlConnection(builder.ConnectionString);
                await dbConnection.OpenAsync();

                var tableCount = await dbConnection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Users'");
                return tableCount > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking database configuration");
                return false;
            }
        }

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