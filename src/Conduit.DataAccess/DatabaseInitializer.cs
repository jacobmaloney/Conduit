using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.DataAccess
{
    /// <summary>
    /// Handles database initialization and migration
    /// </summary>
    public class DatabaseInitializer
    {
        private readonly DatabaseConfig _config;
        private readonly ILogger<DatabaseInitializer> _logger;

        /// <summary>
        /// Initializes a new instance of the DatabaseInitializer class
        /// </summary>
        /// <param name="config">Database configuration</param>
        /// <param name="logger">Logger instance</param>
        public DatabaseInitializer(DatabaseConfig config, ILogger<DatabaseInitializer>? logger = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? NullLogger<DatabaseInitializer>.Instance;
        }

        /// <summary>
        /// Initializes the database, creating it if necessary
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_config.AutoCreateDatabase)
            {
                await EnsureDatabaseExistsAsync();
            }

            if (_config.AutoMigrate)
            {
                await RunMigrationsAsync();
            }
        }

        /// <summary>
        /// Ensures the database exists, creating it if necessary
        /// </summary>
        private async Task EnsureDatabaseExistsAsync()
        {
            var builder = new SqlConnectionStringBuilder(_config.ConnectionString);
            var databaseName = builder.InitialCatalog;
            builder.InitialCatalog = "master";

            using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();

            var exists = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM sys.databases WHERE name = @name",
                new { name = databaseName }) > 0;

            if (!exists)
            {
                await connection.ExecuteAsync($"CREATE DATABASE [{databaseName}]");
                Console.WriteLine($"Created database: {databaseName}");
            }
        }

        /// <summary>
        /// Runs database migrations
        /// </summary>
        private async Task RunMigrationsAsync()
        {
            // Create migrator with logger
            var migrator = new DatabaseMigrator(_config.ConnectionString, NullLogger<DatabaseMigrator>.Instance);

            // Ensure schema version table exists
            await migrator.EnsureSchemaVersionTableAsync();

            // Analyze current schema
            var analysis = await migrator.AnalyzeSchemaAsync();

            // If essential tables don't exist, run initial schema
            var essentialTables = new[] { "Users", "Groups", "GroupMembers" };
            var missingEssentialTables = essentialTables.Where(t => !analysis.ExistingTables.Contains(t)).ToList();
            
            if (missingEssentialTables.Any())
            {
                _logger.LogInformation($"Missing essential tables: {string.Join(", ", missingEssentialTables)}. Running initial schema creation.");
                using var connection = new SqlConnection(_config.ConnectionString);
                await connection.OpenAsync();
                await RunInitialSchemaAsync(connection);
            }
            else
            {
                // Apply any pending migrations
                var migrations = migrator.GetRequiredMigrations(analysis);
                if (migrations.Any())
                {
                    _logger.LogInformation($"Found {migrations.Count} migrations to apply");
                    await migrator.ApplyMigrationsAsync(migrations);
                }
                else
                {
                    _logger.LogInformation("Database schema is up to date");
                }
            }
        }

        /// <summary>
        /// Runs the initial database schema creation
        /// </summary>
        private async Task RunInitialSchemaAsync(IDbConnection connection)
        {
            var schemaScript = GetInitialSchemaScript();
            
            // Split by GO statements and execute each batch
            var batches = schemaScript.Split(new[] { "\nGO\n", "\nGO\r\n", "\r\nGO\r\n", "\r\nGO\n" }, 
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var batch in batches)
            {
                if (!string.IsNullOrWhiteSpace(batch))
                {
                    await connection.ExecuteAsync(batch);
                }
            }

            // Record the schema version if not already present
            var versionExists = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM [SchemaVersion] WHERE [Version] = 1") > 0;
                
            if (!versionExists)
            {
                await connection.ExecuteAsync(
                    "INSERT INTO [SchemaVersion] ([Version], [Description]) VALUES (@Version, @Description)",
                    new { Version = 1, Description = "Initial schema creation" });
            }

            Console.WriteLine("Database schema created successfully");
        }

        /// <summary>
        /// Returns the canonical bootstrap schema, loaded from the embedded
        /// Database/CreateDatabase.sql resource. This is the single source of truth
        /// for the initial schema — incremental changes go in DatabaseMigrator.
        /// </summary>
        private string GetInitialSchemaScript()
        {
            var assembly = typeof(DatabaseInitializer).Assembly;
            const string resourceName = "Conduit.DataAccess.CreateDatabase.sql";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new InvalidOperationException(
                    $"Embedded schema resource '{resourceName}' not found. " +
                    "Check that Database/CreateDatabase.sql is included as an EmbeddedResource in the csproj.");
            }
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }


        /// <summary>
        /// Runs any pending database migrations
        /// </summary>
        private async Task RunPendingMigrationsAsync(IDbConnection connection)
        {
            // In a real application, this would check for and apply migration scripts
            // For now, we'll just ensure the latest version is recorded
            var currentVersion = await connection.QuerySingleOrDefaultAsync<int?>(
                "SELECT MAX([Version]) FROM [SchemaVersion]") ?? 0;

            if (currentVersion < 1)
            {
                // This shouldn't happen if the schema was properly initialized
                await RunInitialSchemaAsync(connection);
            }

            // Future migrations would be applied here
            // Example:
            // if (currentVersion < 2) { await RunMigration2(connection); }
            // if (currentVersion < 3) { await RunMigration3(connection); }
        }
    }
}