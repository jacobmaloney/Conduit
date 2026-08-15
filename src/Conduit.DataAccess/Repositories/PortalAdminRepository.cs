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

        /// <summary>
        /// Activates or deactivates an admin. DEACTIVATING refuses when it would leave the
        /// install with zero active admins; returns false rather than throwing so a caller
        /// can render the refusal. Activating is always allowed and always returns true.
        ///
        /// <see cref="DeleteAsync"/> has carried this guard for a long time and this method
        /// did not, which is a distinction without a difference to whoever is clicking:
        /// both routes end with nobody able to sign in. Recovering from that state now
        /// requires physical access to the host (see AdminRecoveryToken), so the cheap
        /// refusal here is worth a great deal more than it used to be.
        ///
        /// Nothing calls this today — it is currently the ONLY code in the repo that writes
        /// PortalAdmins.Active, with zero callers. The guard is here so that the first
        /// callsite added inherits it rather than reintroducing the lockout.
        /// </summary>
        public async Task<bool> SetActiveAsync(Guid id, bool active)
        {
            using var conn = NewConn();

            if (!active)
            {
                var remaining = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM PortalAdmins WHERE Active = 1 AND Id <> @Id",
                    new { Id = id });
                if (remaining < 1) return false;
            }

            await conn.ExecuteAsync(
                "UPDATE PortalAdmins SET Active = @Active, LastModified = SYSUTCDATETIME() WHERE Id = @Id",
                new { Id = id, Active = active });
            return true;
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
