using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace Conduit.DataAccess.Repositories;

/// <summary>
/// Dapper data access for the Emulator sink's writes into Conduit's own
/// Users / Groups / GroupMembers / UserEmails tables under a specific sink
/// TenantId. EmulatorSink keeps the connector contract and attribute mapping;
/// this repository owns the SQL so the sink no longer opens its own connections.
/// All writes target a caller-supplied tenant by id — this is the privileged
/// sink path, intentionally not the scoped UserRepository.
/// </summary>
public class EmulatorSinkRepository : BaseRepository
{
    public EmulatorSinkRepository(DatabaseConfig config) : base(config) { }

    public Task<Guid?> FindUserIdAsync(Guid tenantId, string? externalId, string userName) =>
        QuerySingleOrDefaultAsync<Guid?>(@"
            SELECT TOP 1 Id FROM Users
             WHERE TenantId = @TenantId
               AND (ExternalId = @ExternalId OR (ExternalId IS NULL AND UserName = @UserName))",
            new { TenantId = tenantId, ExternalId = externalId, UserName = userName });

    public Task InsertUserAsync(Guid tenantId, string? externalId, string userName, bool active,
        string? displayName, string? familyName, string? givenName, string? title,
        string? department, string? employeeNumber) =>
        ExecuteAsync(@"
            INSERT INTO Users
                (Id, TenantId, ExternalId, UserName, Active, Created, LastModified, Version,
                 DisplayName, FamilyName, GivenName, Title, Department, EmployeeNumber)
            VALUES
                (NEWID(), @TenantId, @ExternalId, @UserName, @Active, SYSUTCDATETIME(), SYSUTCDATETIME(), 1,
                 @DisplayName, @FamilyName, @GivenName, @Title, @Department, @EmployeeNumber);",
            new
            {
                TenantId = tenantId,
                ExternalId = externalId,
                UserName = userName,
                Active = active,
                DisplayName = displayName,
                FamilyName = familyName,
                GivenName = givenName,
                Title = title,
                Department = department,
                EmployeeNumber = employeeNumber
            });

    public Task<Guid> GetUserIdByExternalIdAsync(Guid tenantId, string? externalId) =>
        ExecuteScalarAsync<Guid>(
            "SELECT Id FROM Users WHERE TenantId = @TenantId AND ExternalId = @ExternalId",
            new { TenantId = tenantId, ExternalId = externalId });

    public Task UpdateUserAsync(Guid id, string userName, bool active,
        string? displayName, string? familyName, string? givenName, string? title,
        string? department, string? employeeNumber) =>
        ExecuteAsync(@"
            UPDATE Users
               SET UserName = @UserName,
                   Active = @Active,
                   DisplayName = COALESCE(@DisplayName, DisplayName),
                   FamilyName = COALESCE(@FamilyName, FamilyName),
                   GivenName = COALESCE(@GivenName, GivenName),
                   Title = COALESCE(@Title, Title),
                   Department = COALESCE(@Department, Department),
                   EmployeeNumber = COALESCE(@EmployeeNumber, EmployeeNumber),
                   LastModified = SYSUTCDATETIME(),
                   Version = Version + 1
             WHERE Id = @Id;",
            new
            {
                Id = id,
                UserName = userName,
                Active = active,
                DisplayName = displayName,
                FamilyName = familyName,
                GivenName = givenName,
                Title = title,
                Department = department,
                EmployeeNumber = employeeNumber
            });

    public Task UpsertPrimaryEmailAsync(Guid userId, string email) =>
        ExecuteAsync(@"
            IF EXISTS (SELECT 1 FROM UserEmails WHERE UserId = @UserId AND [Primary] = 1)
                UPDATE UserEmails SET Value = @Email WHERE UserId = @UserId AND [Primary] = 1;
            ELSE
                INSERT INTO UserEmails (Id, UserId, Value, [Type], [Primary])
                VALUES (NEWID(), @UserId, @Email, 'work', 1);",
            new { UserId = userId, Email = email });

    public Task<Guid?> FindGroupIdAsync(Guid tenantId, string? externalId, string displayName) =>
        QuerySingleOrDefaultAsync<Guid?>(@"
            SELECT TOP 1 Id FROM Groups
             WHERE TenantId = @TenantId
               AND (ExternalId = @ExternalId OR (ExternalId IS NULL AND DisplayName = @DisplayName))",
            new { TenantId = tenantId, ExternalId = externalId, DisplayName = displayName });

    public Task InsertGroupAsync(Guid id, Guid tenantId, string? externalId,
        string displayName, string? description, string? type) =>
        ExecuteAsync(@"
            INSERT INTO Groups
                (Id, TenantId, ExternalId, DisplayName, Description, Type, Created, LastModified, Version)
            VALUES
                (@Id, @TenantId, @ExternalId, @DisplayName, @Description, @Type, SYSUTCDATETIME(), SYSUTCDATETIME(), 1);",
            new
            {
                Id = id,
                TenantId = tenantId,
                ExternalId = externalId,
                DisplayName = displayName,
                Description = description,
                Type = type
            });

    public Task UpdateGroupAsync(Guid id, string displayName, string? description, string? type) =>
        ExecuteAsync(@"
            UPDATE Groups
               SET DisplayName = @DisplayName,
                   Description = COALESCE(@Description, Description),
                   Type = COALESCE(@Type, Type),
                   LastModified = SYSUTCDATETIME(),
                   Version = Version + 1
             WHERE Id = @Id;",
            new { Id = id, DisplayName = displayName, Description = description, Type = type });

    public async Task<List<Guid>> ResolveMemberUserIdsAsync(Guid tenantId, IEnumerable<string> externalIds)
    {
        var rows = await QueryAsync<Guid>(@"
            SELECT Id FROM Users
             WHERE TenantId = @TenantId
               AND ExternalId IN @ExternalIds",
            new { TenantId = tenantId, ExternalIds = externalIds });
        return rows.ToList();
    }

    /// <summary>
    /// Full-replace the membership set for a group: delete existing rows then
    /// insert one row per member. Mirrors the sink's original (non-transactional)
    /// delete-then-insert sequence on a single connection.
    /// </summary>
    public async Task ReplaceGroupMembersAsync(Guid groupId, IReadOnlyList<Guid> memberUserIds)
    {
        using var conn = CreateConnection();

        await conn.ExecuteAsync(
            "DELETE FROM GroupMembers WHERE GroupId = @GroupId",
            new { GroupId = groupId });

        foreach (var uid in memberUserIds)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO GroupMembers (Id, GroupId, Value, [Type], [Primary])
                VALUES (NEWID(), @GroupId, @Value, 'User', 0);",
                new { GroupId = groupId, Value = uid });
        }
    }

    public Task<int> CountTenantAsync(Guid tenantId) =>
        ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Tenants WHERE Id = @Id",
            new { Id = tenantId });
}
