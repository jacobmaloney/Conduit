using System;

namespace Conduit.DataAccess
{
    /// <summary>
    /// Database configuration settings
    /// </summary>
    public class DatabaseConfig
    {
        /// <summary>
        /// Gets or sets the database connection string
        /// </summary>
        public string ConnectionString { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether to automatically create the database if it doesn't exist
        /// </summary>
        public bool AutoCreateDatabase { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to automatically run migrations
        /// </summary>
        public bool AutoMigrate { get; set; } = true;

        /// <summary>
        /// Gets or sets the command timeout in seconds
        /// </summary>
        public int CommandTimeout { get; set; } = 30;

        /// <summary>
        /// Gets or sets whether to enable query logging
        /// </summary>
        public bool EnableQueryLogging { get; set; } = false;

        /// <summary>
        /// Updates the connection string at runtime (used by setup wizard)
        /// </summary>
        public void SetConnectionString(string connectionString)
        {
            ConnectionString = connectionString;
        }
    }
}