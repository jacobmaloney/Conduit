using System;
using System.Threading.Tasks;

namespace Conduit.DataAccess.Repositories;

/// <summary>
/// Dapper data access for setup-time operations against Conduit's own database.
/// The pre-database connectivity probes (server reachable, database exists,
/// schema present) stay in SetupService since they run before the DB exists and
/// target master / a candidate connection string. This repository owns only the
/// real schema/data reads and writes, executed against the configured database.
/// </summary>
public class SetupRepository : BaseRepository
{
    public SetupRepository(DatabaseConfig config) : base(config) { }

    /// <summary>True when the PortalAdmins table exists in the configured database.</summary>
    public async Task<bool> PortalAdminsTableExistsAsync() =>
        await ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'PortalAdmins'") > 0;

    /// <summary>Count of active portal administrators.</summary>
    public Task<int> CountActiveAdminsAsync() =>
        ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PortalAdmins WHERE Active = 1");

    public Task<Guid?> GetAdminIdByUserNameAsync(string userName) =>
        QuerySingleOrDefaultAsync<Guid?>(
            "SELECT [Id] FROM [PortalAdmins] WHERE LOWER([UserName]) = LOWER(@UserName)",
            new { UserName = userName });

    public Task UpdateAdminPasswordAsync(Guid id, string hash, string salt) =>
        ExecuteAsync(@"
            UPDATE [PortalAdmins]
            SET [PasswordHash] = @Hash, [PasswordSalt] = @Salt, [Active] = 1, [LastModified] = SYSUTCDATETIME()
            WHERE [Id] = @Id",
            new { Hash = hash, Salt = salt, Id = id });

    public Task InsertAdminAsync(string userName, string displayName, string hash, string salt) =>
        ExecuteAsync(@"
            INSERT INTO [PortalAdmins] ([Id], [UserName], [DisplayName], [PasswordHash], [PasswordSalt], [Active])
            VALUES (NEWID(), @UserName, @DisplayName, @Hash, @Salt, 1)",
            new { UserName = userName, DisplayName = displayName, Hash = hash, Salt = salt });
}
