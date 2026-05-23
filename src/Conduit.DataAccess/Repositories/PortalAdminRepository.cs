using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Conduit.DataAccess.Repositories
{
    /// <summary>
    /// Repository for portal/web-UI admin accounts. Lives in its own table — see
    /// migration v10 — so SCIM data operations (Delete All Users, DELETE /scim/v2/Users/{id},
    /// tenant resets, etc.) cannot lock the operator out of their own server.
    /// </summary>
    public class PortalAdminRepository
    {
        private readonly DatabaseConfig _config;

        public PortalAdminRepository(DatabaseConfig config)
        {
            _config = config;
        }

        private SqlConnection NewConn() => new SqlConnection(_config.ConnectionString);

        public async Task<PortalAdmin?> GetByUserNameAsync(string userName)
        {
            using var conn = NewConn();
            return await conn.QuerySingleOrDefaultAsync<PortalAdmin>(@"
                SELECT TOP 1 *
                FROM PortalAdmins
                WHERE LOWER(UserName) = LOWER(@UserName)",
                new { UserName = userName });
        }

        public async Task<PortalAdmin?> GetByIdAsync(Guid id)
        {
            using var conn = NewConn();
            return await conn.QuerySingleOrDefaultAsync<PortalAdmin>(
                "SELECT * FROM PortalAdmins WHERE Id = @Id", new { Id = id });
        }

        public async Task<List<PortalAdmin>> GetAllAsync()
        {
            using var conn = NewConn();
            var rows = await conn.QueryAsync<PortalAdmin>(
                "SELECT * FROM PortalAdmins ORDER BY UserName");
            return rows.ToList();
        }

        public async Task<int> CountAsync()
        {
            using var conn = NewConn();
            return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PortalAdmins WHERE Active = 1");
        }

        public async Task<PortalAdmin> CreateAsync(string userName, string? displayName, string passwordHash, string passwordSalt)
        {
            var admin = new PortalAdmin
            {
                Id = Guid.NewGuid(),
                UserName = userName,
                DisplayName = displayName,
                PasswordHash = passwordHash,
                PasswordSalt = passwordSalt,
                Active = true,
                Created = DateTime.UtcNow,
                LastModified = DateTime.UtcNow
            };

            using var conn = NewConn();
            await conn.ExecuteAsync(@"
                INSERT INTO PortalAdmins (Id, UserName, DisplayName, PasswordHash, PasswordSalt, Active, Created, LastModified)
                VALUES (@Id, @UserName, @DisplayName, @PasswordHash, @PasswordSalt, @Active, @Created, @LastModified)",
                admin);
            return admin;
        }

        public async Task UpdatePasswordAsync(Guid id, string passwordHash, string passwordSalt)
        {
            using var conn = NewConn();
            await conn.ExecuteAsync(@"
                UPDATE PortalAdmins
                   SET PasswordHash = @Hash, PasswordSalt = @Salt, LastModified = SYSUTCDATETIME()
                 WHERE Id = @Id",
                new { Id = id, Hash = passwordHash, Salt = passwordSalt });
        }

        public async Task MarkLoggedInAsync(Guid id)
        {
            using var conn = NewConn();
            await conn.ExecuteAsync(
                "UPDATE PortalAdmins SET LastLoginAt = SYSUTCDATETIME() WHERE Id = @Id",
                new { Id = id });
        }

        public async Task SetActiveAsync(Guid id, bool active)
        {
            using var conn = NewConn();
            await conn.ExecuteAsync(
                "UPDATE PortalAdmins SET Active = @Active, LastModified = SYSUTCDATETIME() WHERE Id = @Id",
                new { Id = id, Active = active });
        }

        /// <summary>
        /// Refuses to delete the last active admin — keeps the operator from locking
        /// themselves out via a single click.
        /// </summary>
        public async Task<bool> DeleteAsync(Guid id)
        {
            using var conn = NewConn();
            var remaining = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM PortalAdmins WHERE Active = 1 AND Id <> @Id",
                new { Id = id });
            if (remaining < 1) return false;
            var rows = await conn.ExecuteAsync(
                "DELETE FROM PortalAdmins WHERE Id = @Id", new { Id = id });
            return rows > 0;
        }
    }

    public class PortalAdmin
    {
        public Guid Id { get; set; }
        public string UserName { get; set; } = "";
        public string? DisplayName { get; set; }
        public string PasswordHash { get; set; } = "";
        public string PasswordSalt { get; set; } = "";
        public bool Active { get; set; } = true;
        public DateTime Created { get; set; }
        public DateTime LastModified { get; set; }
        public DateTime? LastLoginAt { get; set; }
    }
}
