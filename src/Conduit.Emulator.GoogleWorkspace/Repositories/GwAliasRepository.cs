using Dapper;
using Microsoft.Data.SqlClient;
using Conduit.DataAccess;
using Conduit.Emulator.GoogleWorkspace.Infrastructure;

namespace Conduit.Emulator.GoogleWorkspace.Repositories;

public sealed class GwAliasRepository
{
    private readonly DatabaseConfig _config;

    public GwAliasRepository(DatabaseConfig config) => _config = config;

    private SqlConnection Open()
    {
        var c = new SqlConnection(_config.ConnectionString);
        c.Open();
        return c;
    }

    public async Task UpsertAsync(string alias, string targetId, string targetKind, string primaryEmail, bool editable = true)
    {
        using var c = Open();
        await c.ExecuteAsync(@"
MERGE gw_aliases AS t
USING (SELECT @Alias AS Alias) AS s ON t.Alias = s.Alias
WHEN MATCHED THEN UPDATE SET TargetId = @TargetId, TargetKind = @TargetKind,
                             PrimaryEmail = @PrimaryEmail, Editable = @Editable, Etag = @Etag
WHEN NOT MATCHED THEN
    INSERT (Alias, TargetId, TargetKind, PrimaryEmail, Editable, Etag)
    VALUES (@Alias, @TargetId, @TargetKind, @PrimaryEmail, @Editable, @Etag);",
            new { Alias = alias, TargetId = targetId, TargetKind = targetKind, PrimaryEmail = primaryEmail,
                  Editable = editable ? 1 : 0, Etag = EtagGenerator.New() });
    }
}
