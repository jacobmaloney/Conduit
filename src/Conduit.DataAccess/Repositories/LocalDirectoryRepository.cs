using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace Conduit.DataAccess.Repositories;

/// <summary>
/// Privileged read access to Conduit's OWN Users / Groups / GroupMembers /
/// UserEmails tables for a specific connection (tenant) by id — the SOURCE side
/// of the Local Directory connector. Deliberately NOT the scoped UserRepository:
/// like <see cref="EmulatorSinkRepository"/> (the matching sink side) it targets a
/// caller-supplied tenant explicitly so a Sync Project or the inbound read-through
/// can enumerate exactly one connection's local objects regardless of ambient
/// tenant context.
///
/// Read-only. The Local Directory sink writes through <see cref="EmulatorSinkRepository"/>
/// (already the proven path into these same tables), so this type adds nothing
/// destructive.
/// </summary>
public class LocalDirectoryRepository : BaseRepository
{
    public LocalDirectoryRepository(DatabaseConfig config) : base(config) { }

    public sealed class LocalUserRow
    {
        public Guid Id { get; set; }
        public string? ExternalId { get; set; }
        public string UserName { get; set; } = string.Empty;
        public bool Active { get; set; }
        public string? DisplayName { get; set; }
        public string? GivenName { get; set; }
        public string? FamilyName { get; set; }
        public string? Title { get; set; }
        public string? Department { get; set; }
        public string? EmployeeNumber { get; set; }
        public string? Email { get; set; }
    }

    public sealed class LocalGroupRow
    {
        public Guid Id { get; set; }
        public string? ExternalId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Type { get; set; }
    }

    /// <summary>
    /// All users for a connection, joined to their primary email. Ordered by Id so
    /// paging in the source is deterministic.
    /// </summary>
    public async Task<List<LocalUserRow>> GetUsersAsync(Guid tenantId)
    {
        var rows = await QueryAsync<LocalUserRow>(@"
            SELECT u.Id, u.ExternalId, u.UserName, u.Active, u.DisplayName,
                   u.GivenName, u.FamilyName, u.Title, u.Department, u.EmployeeNumber,
                   (SELECT TOP 1 e.Value FROM UserEmails e
                     WHERE e.UserId = u.Id AND e.[Primary] = 1) AS Email
              FROM Users u
             WHERE u.TenantId = @TenantId
             ORDER BY u.Id;",
            new { TenantId = tenantId });
        return rows.ToList();
    }

    /// <summary>All groups for a connection. Members are resolved separately.</summary>
    public async Task<List<LocalGroupRow>> GetGroupsAsync(Guid tenantId)
    {
        var rows = await QueryAsync<LocalGroupRow>(@"
            SELECT g.Id, g.ExternalId, g.DisplayName, g.Description, g.Type
              FROM Groups g
             WHERE g.TenantId = @TenantId
             ORDER BY g.Id;",
            new { TenantId = tenantId });
        return rows.ToList();
    }

    /// <summary>
    /// Member external ids for a group, falling back to the member Users.Id when a
    /// member user has no ExternalId. Returns the stable identifiers a downstream
    /// sink can resolve.
    /// </summary>
    public async Task<List<string>> GetGroupMemberExternalIdsAsync(Guid groupId)
    {
        var rows = await QueryAsync<string>(@"
            SELECT COALESCE(u.ExternalId, CONVERT(nvarchar(36), u.Id)) AS MemberId
              FROM GroupMembers gm
              JOIN Users u ON u.Id = gm.Value
             WHERE gm.GroupId = @GroupId;",
            new { GroupId = groupId });
        return rows.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }
}
