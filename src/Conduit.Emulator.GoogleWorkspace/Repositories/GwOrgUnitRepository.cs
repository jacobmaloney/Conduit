using Dapper;
using Microsoft.Data.SqlClient;
using Conduit.DataAccess;
using Conduit.Emulator.GoogleWorkspace.Infrastructure;

namespace Conduit.Emulator.GoogleWorkspace.Repositories;

public sealed class GwOrgUnitRepository
{
    private readonly DatabaseConfig _config;

    public GwOrgUnitRepository(DatabaseConfig config) => _config = config;

    private SqlConnection Open()
    {
        var c = new SqlConnection(_config.ConnectionString);
        c.Open();
        return c;
    }

    public async Task<int> CountAsync(string customerId)
    {
        using var c = Open();
        return await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM gw_orgunits WHERE CustomerId = @CustomerId",
            new { CustomerId = customerId });
    }

    public async Task UpsertAsync(string customerId, string path, string? parentPath, string name, string? description)
    {
        var id = IdGenerator.NewOrgUnitId();
        using var c = Open();
        await c.ExecuteAsync(@"
MERGE gw_orgunits AS t
USING (SELECT @CustomerId AS CustomerId, @OrgUnitPath AS OrgUnitPath) AS s
    ON t.CustomerId = s.CustomerId AND t.OrgUnitPath = s.OrgUnitPath
WHEN MATCHED THEN UPDATE SET Name = @Name, Description = @Description,
                             ParentOrgUnitPath = @ParentPath, Etag = @Etag
WHEN NOT MATCHED THEN
    INSERT (OrgUnitId, CustomerId, OrgUnitPath, ParentOrgUnitPath, Name, Description, BlockInheritance, Etag)
    VALUES (@OrgUnitId, @CustomerId, @OrgUnitPath, @ParentPath, @Name, @Description, 0, @Etag);",
            new { OrgUnitId = id, CustomerId = customerId, OrgUnitPath = path, ParentPath = parentPath, Name = name, Description = description, Etag = EtagGenerator.New() });
    }

    public async Task<IReadOnlyList<string>> ListPathsAsync(string customerId)
    {
        using var c = Open();
        var rows = await c.QueryAsync<string>(
            "SELECT OrgUnitPath FROM gw_orgunits WHERE CustomerId = @CustomerId",
            new { CustomerId = customerId });
        return rows.ToList();
    }
}
