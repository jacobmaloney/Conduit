using Dapper;
using Microsoft.Data.SqlClient;
using Conduit.DataAccess;
using Conduit.Emulator.GoogleWorkspace.Infrastructure;
using Conduit.Emulator.GoogleWorkspace.Models;

namespace Conduit.Emulator.GoogleWorkspace.Repositories;

public sealed class GwGroupRepository
{
    private readonly DatabaseConfig _config;

    public GwGroupRepository(DatabaseConfig config) => _config = config;

    private SqlConnection Open()
    {
        var c = new SqlConnection(_config.ConnectionString);
        c.Open();
        return c;
    }

    public async Task<GwGroup?> ResolveAsync(string groupKey)
    {
        using var c = Open();
        const string sql = @"
SELECT TOP 1 *
FROM gw_groups g
WHERE g.Id = @Key OR g.Email = @Key
   OR EXISTS (SELECT 1 FROM gw_aliases a WHERE a.Alias = @Key AND a.TargetKind = 'group' AND a.TargetId = g.Id);";
        var row = await c.QuerySingleOrDefaultAsync<dynamic>(sql, new { Key = groupKey });
        return row is null ? null : Map(row);
    }

    public async Task<(IReadOnlyList<GwGroup> Groups, int Total)> ListAsync(
        string customerId,
        string? domain,
        string? userKey,
        string? query,
        int offset,
        int limit)
    {
        using var c = Open();
        var p = new DynamicParameters();
        p.Add("CustomerId", customerId);
        p.Add("Offset", offset);
        p.Add("Limit", limit);

        var filters = new List<string> { "g.CustomerId = @CustomerId" };

        if (!string.IsNullOrEmpty(domain))
        {
            filters.Add("g.Email LIKE @Domain");
            p.Add("Domain", "%@" + domain);
        }

        if (!string.IsNullOrEmpty(userKey))
        {
            filters.Add(@"EXISTS (SELECT 1 FROM gw_members m WHERE m.GroupId = g.Id
                          AND (m.MemberId = @UserKey OR m.Email = @UserKey))");
            p.Add("UserKey", userKey);
        }

        if (!string.IsNullOrEmpty(query))
        {
            filters.Add("(g.Email LIKE @Q OR g.Name LIKE @Q OR g.Description LIKE @Q)");
            p.Add("Q", "%" + query + "%");
        }

        var where = string.Join(" AND ", filters);
        var total = await c.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM gw_groups g WHERE {where}", p);
        var rows = await c.QueryAsync<dynamic>(
            $@"SELECT g.* FROM gw_groups g WHERE {where} ORDER BY g.Email OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY",
            p);
        return (rows.Select(Map).ToList(), total);
    }

    public async Task<GwGroup> InsertAsync(GwGroup group)
    {
        using var c = Open();
        group.Etag = EtagGenerator.New();
        const string sql = @"
INSERT INTO gw_groups (Id, CustomerId, Email, Name, Description, DirectMembersCount, AdminCreated, Etag, CreationTime)
VALUES (@Id, @CustomerId, @Email, @Name, @Description, 0, 1, @Etag, SYSUTCDATETIME());";
        await c.ExecuteAsync(sql, new
        {
            group.Id,
            CustomerId = group.Kind == "admin#directory#group" ? await GetCustomerIdFallback(c) : string.Empty,
            group.Email,
            group.Name,
            group.Description,
            group.Etag
        });
        return group;
    }

    public async Task<GwGroup> InsertWithCustomerAsync(GwGroup group, string customerId)
    {
        using var c = Open();
        group.Etag = EtagGenerator.New();
        const string sql = @"
INSERT INTO gw_groups (Id, CustomerId, Email, Name, Description, DirectMembersCount, AdminCreated, Etag, CreationTime)
VALUES (@Id, @CustomerId, @Email, @Name, @Description, @DirectMembersCount, 1, @Etag, SYSUTCDATETIME());";
        await c.ExecuteAsync(sql, new
        {
            group.Id,
            CustomerId = customerId,
            group.Email,
            group.Name,
            group.Description,
            group.DirectMembersCount,
            group.Etag
        });
        return group;
    }

    public async Task<GwGroup?> UpdateAsync(string id, GwGroup updated, string? ifMatch)
    {
        using var c = Open();
        var existing = await c.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT * FROM gw_groups WHERE Id = @Id", new { Id = id });
        if (existing is null) return null;
        if (!string.IsNullOrEmpty(ifMatch) && !string.Equals(ifMatch, (string)existing.Etag, StringComparison.Ordinal))
            throw new EtagMismatchException();

        var newEtag = EtagGenerator.New();
        await c.ExecuteAsync(
            @"UPDATE gw_groups SET Email = @Email, Name = @Name, Description = @Description, Etag = @Etag WHERE Id = @Id",
            new { Id = id, updated.Email, updated.Name, updated.Description, Etag = newEtag });

        return await ResolveAsync(id);
    }

    public async Task<bool> DeleteAsync(string id)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        await c.ExecuteAsync("DELETE FROM gw_members WHERE GroupId = @Id", new { Id = id }, tx);
        var rows = await c.ExecuteAsync("DELETE FROM gw_groups WHERE Id = @Id", new { Id = id }, tx);
        tx.Commit();
        return rows > 0;
    }

    public async Task RefreshMemberCountAsync(string groupId)
    {
        using var c = Open();
        await c.ExecuteAsync(
            @"UPDATE gw_groups SET DirectMembersCount = (SELECT COUNT(*) FROM gw_members WHERE GroupId = @Id),
                                   Etag = @Etag WHERE Id = @Id",
            new { Id = groupId, Etag = EtagGenerator.New() });
    }

    private async Task<string> GetCustomerIdFallback(SqlConnection c)
        => await c.ExecuteScalarAsync<string>("SELECT TOP 1 CustomerId FROM gw_customers") ?? "C00acme01";

    private static GwGroup Map(dynamic r) => new()
    {
        Id = r.Id,
        Email = r.Email,
        Name = r.Name,
        Description = r.Description,
        DirectMembersCount = r.DirectMembersCount,
        AdminCreated = r.AdminCreated,
        Etag = r.Etag
    };
}
