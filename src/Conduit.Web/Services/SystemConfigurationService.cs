using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Conduit.DataAccess;

namespace Conduit.Web.Services
{
    /// <summary>
    /// Thin Dapper accessor over the SystemConfiguration key/value table.
    /// Used to surface runtime configuration (e.g. SqlEmulator.ConnectionString,
    /// ArsProxy.Server / .Username / .Password) without baking them into appsettings.
    /// </summary>
    public class SystemConfigurationService
    {
        private readonly DatabaseConfig _db;

        public SystemConfigurationService(DatabaseConfig db)
        {
            _db = db;
        }

        public async Task<string?> GetAsync(string key)
        {
            using var conn = new SqlConnection(_db.ConnectionString);
            return await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT [Value] FROM SystemConfiguration WHERE [Key] = @Key",
                new { Key = key });
        }

        public async Task SetAsync(string key, string value, string type = "String", string? description = null)
        {
            using var conn = new SqlConnection(_db.ConnectionString);
            await conn.ExecuteAsync(@"
                MERGE SystemConfiguration AS target
                USING (SELECT @Key AS [Key]) AS src
                   ON target.[Key] = src.[Key]
                WHEN MATCHED THEN
                    UPDATE SET [Value] = @Value, [Type] = @Type, [Description] = @Description, [LastModified] = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN
                    INSERT ([Key], [Value], [Type], [Description])
                    VALUES (@Key, @Value, @Type, @Description);",
                new { Key = key, Value = value, Type = type, Description = description });
        }
    }
}
