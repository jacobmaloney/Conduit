using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Conduit.Core.Models;

namespace Conduit.DataAccess.Repositories
{
    /// <summary>
    /// Repository for managing tenants (UI label: "Connected Systems").
    /// </summary>
    public class TenantRepository : BaseRepository
    {
        /// <summary>Fixed GUID of the seed Default tenant (see migration v8).</summary>
        public static readonly Guid DefaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        public TenantRepository(DatabaseConfig config) : base(config) { }

        public async Task<List<Tenant>> GetAllAsync(bool includeInactive = false)
        {
            var sql = includeInactive
                ? "SELECT * FROM Tenants ORDER BY Name"
                : "SELECT * FROM Tenants WHERE IsActive = 1 ORDER BY Name";
            var rows = await QueryAsync<Tenant>(sql);
            return rows.ToList();
        }

        public Task<Tenant?> GetByIdAsync(Guid id) =>
            QuerySingleOrDefaultAsync<Tenant>(
                "SELECT * FROM Tenants WHERE Id = @Id",
                new { Id = id });

        public Task<Tenant?> GetBySlugAsync(string slug) =>
            QuerySingleOrDefaultAsync<Tenant>(
                "SELECT * FROM Tenants WHERE Slug = @Slug",
                new { Slug = slug });

        public async Task<Tenant> CreateAsync(Tenant tenant)
        {
            if (tenant.Id == Guid.Empty) tenant.Id = Guid.NewGuid();
            tenant.Created = DateTime.UtcNow;
            tenant.LastModified = tenant.Created;

            const string sql = @"
                INSERT INTO Tenants (Id, Name, Slug, Description, SystemType, Domain, IsActive, LegalHold, Created, LastModified)
                VALUES (@Id, @Name, @Slug, @Description, @SystemType, @Domain, @IsActive, @LegalHold, @Created, @LastModified);";
            await ExecuteAsync(sql, tenant);
            return tenant;
        }

        public async Task<Tenant?> UpdateAsync(Tenant tenant)
        {
            tenant.LastModified = DateTime.UtcNow;

            const string sql = @"
                UPDATE Tenants
                   SET Name = @Name,
                       Slug = @Slug,
                       Description = @Description,
                       SystemType = @SystemType,
                       Domain = @Domain,
                       IsActive = @IsActive,
                       LegalHold = @LegalHold,
                       LastModified = @LastModified
                 WHERE Id = @Id;";

            var rows = await ExecuteAsync(sql, tenant);
            return rows == 0 ? null : tenant;
        }

        /// <summary>
        /// Sets the LegalHold flag in isolation — does NOT touch any other column.
        /// Called by the toggle on the Connected Systems card. The caller is expected
        /// to audit-log the change since the repo doesn't see the operator context.
        /// </summary>
        public async Task<bool> SetLegalHoldAsync(Guid id, bool enabled)
        {
            var rows = await ExecuteAsync(@"
                UPDATE Tenants
                   SET LegalHold = @Enabled, LastModified = SYSUTCDATETIME()
                 WHERE Id = @Id;",
                new { Id = id, Enabled = enabled });
            return rows > 0;
        }

        /// <summary>
        /// True if the Connected System has LegalHold = 1. Cheap read used by the
        /// repo's destructive paths to gate before any cascade work begins.
        /// </summary>
        public async Task<bool> IsOnLegalHoldAsync(Guid id)
        {
            return await ExecuteScalarAsync<bool>(
                "SELECT ISNULL(LegalHold, 0) FROM Tenants WHERE Id = @Id",
                new { Id = id });
        }

        /// <summary>
        /// Soft-delete by default — flips IsActive=0 so existing FKs stay valid.
        /// The Default tenant cannot be deleted.
        /// </summary>
        public async Task<bool> DeactivateAsync(Guid id)
        {
            if (id == DefaultTenantId) return false;
            var rows = await ExecuteAsync(
                "UPDATE Tenants SET IsActive = 0, LastModified = SYSUTCDATETIME() WHERE Id = @Id",
                new { Id = id });
            return rows > 0;
        }

        /// <summary>
        /// Per-tenant data wipe: removes all Users and Groups (with every FK
        /// dependent — memberships, role assignments, emails, phones, addresses)
        /// scoped to the supplied tenant. The Connected System row itself stays
        /// alive, as do its ApiTokens and SqlAccounts. Single transaction;
        /// rolls back cleanly on failure. The Default tenant is exempt only
        /// from the row delete in DeleteAsync; ClearDataAsync runs against any
        /// tenant including Default.
        /// Returns the number of (Users + Groups) rows removed.
        /// </summary>
        public async Task<int> ClearDataAsync(Guid id)
        {
            // Hard refusal on legal hold. Sentinel value -1 distinguishes "refused"
            // from "ran and removed 0 rows" in the caller's UI.
            if (await IsOnLegalHoldAsync(id)) return -1;

            var p = new DynamicParameters();
            p.Add("Id", id);

            using var connection = CreateConnection();
            using var tx = connection.BeginTransaction();
            try
            {
                // Cross-tenant FK cleanup first (same shape as DeleteAsync).
                await connection.ExecuteAsync(@"
                    UPDATE Users SET ManagerId = NULL
                     WHERE ManagerId IN (SELECT Id FROM Users WHERE TenantId = @Id);", p, tx);
                await connection.ExecuteAsync(@"
                    UPDATE Groups SET OwnerId = NULL
                     WHERE OwnerId IN (SELECT Id FROM Users WHERE TenantId = @Id);", p, tx);

                // Tenant-local dependents in bottom-up FK order.
                await connection.ExecuteAsync(@"
                    DELETE FROM GroupMembers
                     WHERE GroupId IN (SELECT Id FROM Groups WHERE TenantId = @Id);", p, tx);
                await connection.ExecuteAsync(@"
                    DELETE FROM UserRoles
                     WHERE UserId IN (SELECT Id FROM Users WHERE TenantId = @Id);", p, tx);
                await connection.ExecuteAsync(@"
                    DELETE FROM UserAddresses
                     WHERE UserId IN (SELECT Id FROM Users WHERE TenantId = @Id);", p, tx);
                await connection.ExecuteAsync(@"
                    DELETE FROM UserEmails
                     WHERE UserId IN (SELECT Id FROM Users WHERE TenantId = @Id);", p, tx);
                await connection.ExecuteAsync(@"
                    DELETE FROM UserPhoneNumbers
                     WHERE UserId IN (SELECT Id FROM Users WHERE TenantId = @Id);", p, tx);

                var groupsRemoved = await connection.ExecuteAsync(@"DELETE FROM Groups WHERE TenantId = @Id;", p, tx);
                var usersRemoved  = await connection.ExecuteAsync(@"DELETE FROM Users WHERE TenantId = @Id;", p, tx);

                tx.Commit();
                return groupsRemoved + usersRemoved;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Hard delete with full cascade. Removes the Connected System and every row
        /// that references it — users, groups, group memberships, user emails / phone
        /// numbers / addresses / role assignments, API tokens, and SqlAccounts. Refuses
        /// to delete the seed Default tenant. The whole operation runs in a single
        /// transaction so a mid-step failure rolls back cleanly.
        ///
        /// FK references that cross tenant boundaries (a manager in tenant B reporting
        /// to a user being deleted in tenant A; a group in B owned by a user in A) are
        /// nulled first so the cascade doesn't trip self-FKs.
        /// </summary>
        public async Task<bool> DeleteAsync(Guid id)
        {
            if (id == DefaultTenantId) return false;
            if (await IsOnLegalHoldAsync(id)) return false;

            var p = new DynamicParameters();
            p.Add("Id", id);

            // CreateConnection() already opens the connection — don't call Open() again
            // or SqlConnection throws "connection's current state is open."
            using var connection = CreateConnection();
            using var tx = connection.BeginTransaction();
            try
            {
                // Cross-tenant FK cleanup first — null out references pointing AT this
                // tenant's users/groups from rows that live elsewhere.
                await connection.ExecuteAsync(@"
                    UPDATE Users
                       SET ManagerId = NULL
                     WHERE ManagerId IN (SELECT Id FROM Users WHERE TenantId = @Id);", p, tx);
                await connection.ExecuteAsync(@"
                    UPDATE Groups
                       SET OwnerId = NULL
                     WHERE OwnerId IN (SELECT Id FROM Users WHERE TenantId = @Id);", p, tx);

                // Now strip dependent rows tenant-local in bottom-up FK order.
                await connection.ExecuteAsync(@"
                    DELETE FROM GroupMembers
                     WHERE GroupId IN (SELECT Id FROM Groups WHERE TenantId = @Id);", p, tx);
                await connection.ExecuteAsync(@"
                    DELETE FROM UserRoles
                     WHERE UserId IN (SELECT Id FROM Users WHERE TenantId = @Id);", p, tx);
                await connection.ExecuteAsync(@"
                    DELETE FROM UserAddresses
                     WHERE UserId IN (SELECT Id FROM Users WHERE TenantId = @Id);", p, tx);
                await connection.ExecuteAsync(@"
                    DELETE FROM UserEmails
                     WHERE UserId IN (SELECT Id FROM Users WHERE TenantId = @Id);", p, tx);
                await connection.ExecuteAsync(@"
                    DELETE FROM UserPhoneNumbers
                     WHERE UserId IN (SELECT Id FROM Users WHERE TenantId = @Id);", p, tx);
                await connection.ExecuteAsync(@"DELETE FROM Groups WHERE TenantId = @Id;", p, tx);
                await connection.ExecuteAsync(@"DELETE FROM Users WHERE TenantId = @Id;", p, tx);
                await connection.ExecuteAsync(@"DELETE FROM ApiTokens WHERE TenantId = @Id;", p, tx);
                await connection.ExecuteAsync(@"DELETE FROM SqlAccounts WHERE TenantId = @Id;", p, tx);

                var rows = await connection.ExecuteAsync(
                    @"DELETE FROM Tenants WHERE Id = @Id;", p, tx);

                tx.Commit();
                return rows > 0;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Returns user + group + active-token counts for each tenant.
        /// Used by the Connected Systems dashboard.
        /// </summary>
        public async Task<List<TenantStats>> GetStatsAsync()
        {
            const string sql = @"
                SELECT t.Id AS TenantId,
                       t.Name AS Name,
                       t.Slug AS Slug,
                       t.SystemType AS SystemType,
                       t.Domain AS Domain,
                       t.IsActive AS IsActive,
                       ISNULL(t.LegalHold, 0) AS LegalHold,
                       (SELECT COUNT(*) FROM Users  WHERE TenantId = t.Id) AS UserCount,
                       (SELECT COUNT(*) FROM Groups WHERE TenantId = t.Id) AS GroupCount,
                       (SELECT COUNT(*) FROM ApiTokens WHERE TenantId = t.Id AND IsActive = 1) AS TokenCount
                  FROM Tenants t
                 ORDER BY t.Name";
            var rows = await QueryAsync<TenantStats>(sql);
            return rows.ToList();
        }
    }

    /// <summary>
    /// Row shape for the Connected Systems dashboard summary.
    /// </summary>
    public class TenantStats
    {
        public Guid TenantId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string SystemType { get; set; } = "Emulator";
        public string? Domain { get; set; }
        public bool IsActive { get; set; }
        public bool LegalHold { get; set; }
        public int UserCount { get; set; }
        public int GroupCount { get; set; }
        public int TokenCount { get; set; }
    }
}
