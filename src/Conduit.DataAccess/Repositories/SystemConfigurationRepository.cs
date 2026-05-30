using System.Threading.Tasks;

namespace Conduit.DataAccess.Repositories;

/// <summary>
/// Dapper data access for the SystemConfiguration key/value table.
/// Backs both SystemConfigurationService (general settings) and OpenAccessState
/// (the Portal.OpenAccess flag) — the SQL for both lives here.
/// </summary>
public class SystemConfigurationRepository : BaseRepository
{
    public SystemConfigurationRepository(DatabaseConfig config) : base(config) { }

    public Task<string?> GetValueAsync(string key) =>
        ExecuteScalarAsync<string?>(
            "SELECT [Value] FROM SystemConfiguration WHERE [Key] = @Key",
            new { Key = key });

    public Task UpsertAsync(string key, string value, string type, string? description) =>
        ExecuteAsync(@"
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
