using Dapper;
using Microsoft.Data.SqlClient;
using Conduit.DataAccess;
using Conduit.Emulator.GoogleWorkspace.Infrastructure;
using Conduit.Emulator.GoogleWorkspace.Models;

namespace Conduit.Emulator.GoogleWorkspace.Repositories;

public sealed class GwMemberRepository
{
    private readonly DatabaseConfig _config;

    public GwMemberRepository(DatabaseConfig config) => _config = config;

    private SqlConnection Open()
    {
        var c = new SqlConnection(_config.ConnectionString);
        c.Open();
        return c;
    }

    public async Task<GwMember?> GetAsync(string groupId, string memberKey)
    {
        using var c = Open();
        const string sql = @"
SELECT TOP 1 m.*
FROM gw_members m
WHERE m.GroupId = @GroupId AND (m.MemberId = @Key OR m.Email = @Key);";
        var row = await c.QuerySingleOrDefaultAsync<dynamic>(sql, new { GroupId = groupId, Key = memberKey });
        return row is null ? null : Map(row);
    }

    public async Task<(IReadOnlyList<GwMember> Members, int Total)> ListAsync(
        string groupId, string? roles, bool includeDerived, int offset, int limit)
    {
        using var c = Open();
        var p = new DynamicParameters();
        p.Add("GroupId", groupId);
        p.Add("Offset", offset);
        p.Add("Limit", limit);

        var filters = new List<string> { "m.GroupId = @GroupId" };
        if (!string.IsNullOrWhiteSpace(roles))
        {
            var roleList = roles!.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                 .Select(r => r.Trim().ToUpperInvariant()).ToArray();
            p.Add("Roles", roleList);
            filters.Add("m.Role IN @Roles");
        }

        var where = string.Join(" AND ", filters);
        var total = await c.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM gw_members m WHERE {where}", p);
        var rows = await c.QueryAsync<dynamic>(
            $@"SELECT m.* FROM gw_members m WHERE {where} ORDER BY m.Email
               OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY", p);
        return (rows.Select(Map).ToList(), total);
    }

    public async Task<bool> ExistsAsync(string groupId, string memberId)
    {
        using var c = Open();
        return await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM gw_members WHERE GroupId = @GroupId AND MemberId = @MemberId",
            new { GroupId = groupId, MemberId = memberId }) > 0;
    }

    public async Task InsertAsync(string groupId, GwMember member)
    {
        using var c = Open();
        member.Etag = EtagGenerator.New();
        const string sql = @"
INSERT INTO gw_members (GroupId, MemberId, Email, Role, Type, Status, DeliverySettings, Etag)
VALUES (@GroupId, @MemberId, @Email, @Role, @Type, @Status, @DeliverySettings, @Etag);";
        await c.ExecuteAsync(sql, new
        {
            GroupId = groupId,
            MemberId = member.Id,
            member.Email,
            member.Role,
            member.Type,
            member.Status,
            member.DeliverySettings,
            member.Etag
        });
    }

    public async Task<bool> UpdateAsync(string groupId, string memberId, string role, string? deliverySettings)
    {
        using var c = Open();
        var rows = await c.ExecuteAsync(
            @"UPDATE gw_members
                 SET Role = @Role,
                     DeliverySettings = COALESCE(@DeliverySettings, DeliverySettings),
                     Etag = @Etag
               WHERE GroupId = @GroupId AND MemberId = @MemberId",
            new { GroupId = groupId, MemberId = memberId, Role = role, DeliverySettings = deliverySettings, Etag = EtagGenerator.New() });
        return rows > 0;
    }

    public async Task<bool> DeleteAsync(string groupId, string memberId)
    {
        using var c = Open();
        var rows = await c.ExecuteAsync(
            "DELETE FROM gw_members WHERE GroupId = @GroupId AND MemberId = @MemberId",
            new { GroupId = groupId, MemberId = memberId });
        return rows > 0;
    }

    private static GwMember Map(dynamic r) => new()
    {
        Id = r.MemberId,
        Email = r.Email,
        Role = r.Role,
        Type = r.Type,
        Status = r.Status,
        DeliverySettings = r.DeliverySettings,
        Etag = r.Etag
    };
}
