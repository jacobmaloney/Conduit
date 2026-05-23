using Dapper;
using Microsoft.Data.SqlClient;
using Conduit.DataAccess;
using Conduit.Emulator.GoogleWorkspace.Infrastructure;
using Conduit.Emulator.GoogleWorkspace.Models;

namespace Conduit.Emulator.GoogleWorkspace.Repositories;

public sealed class GwDomainRepository
{
    private readonly DatabaseConfig _config;

    public GwDomainRepository(DatabaseConfig config) => _config = config;

    private SqlConnection Open()
    {
        var c = new SqlConnection(_config.ConnectionString);
        c.Open();
        return c;
    }

    public async Task<IReadOnlyList<GwDomain>> ListAsync(string customerId)
    {
        using var c = Open();
        var rows = await c.QueryAsync<dynamic>(
            @"SELECT CustomerId, DomainName, IsPrimary, Verified, Etag, CreationTime
                FROM gw_domains WHERE CustomerId = @CustomerId ORDER BY IsPrimary DESC, DomainName",
            new { CustomerId = customerId });
        return rows.Select(Map).ToList();
    }

    public async Task<GwDomain?> GetAsync(string customerId, string domainName)
    {
        using var c = Open();
        var row = await c.QuerySingleOrDefaultAsync<dynamic>(
            @"SELECT CustomerId, DomainName, IsPrimary, Verified, Etag, CreationTime
                FROM gw_domains WHERE CustomerId = @CustomerId AND DomainName = @DomainName",
            new { CustomerId = customerId, DomainName = domainName });
        return row is null ? null : Map(row);
    }

    public async Task UpsertAsync(string customerId, string domainName, bool isPrimary)
    {
        using var c = Open();
        await c.ExecuteAsync(@"
MERGE gw_domains AS t
USING (SELECT @CustomerId AS CustomerId, @DomainName AS DomainName) AS s
    ON t.CustomerId = s.CustomerId AND t.DomainName = s.DomainName
WHEN MATCHED THEN UPDATE SET IsPrimary = @IsPrimary, Etag = @Etag
WHEN NOT MATCHED THEN
    INSERT (CustomerId, DomainName, IsPrimary, Verified, Etag)
    VALUES (@CustomerId, @DomainName, @IsPrimary, 1, @Etag);",
            new { CustomerId = customerId, DomainName = domainName, IsPrimary = isPrimary ? 1 : 0, Etag = EtagGenerator.New() });
    }

    public async Task<bool> DeleteAsync(string customerId, string domainName)
    {
        using var c = Open();
        var rows = await c.ExecuteAsync(
            "DELETE FROM gw_domains WHERE CustomerId = @CustomerId AND DomainName = @DomainName AND IsPrimary = 0",
            new { CustomerId = customerId, DomainName = domainName });
        return rows > 0;
    }

    private static GwDomain Map(dynamic r) => new()
    {
        DomainName = r.DomainName,
        IsPrimary = r.IsPrimary,
        Verified = r.Verified,
        Etag = r.Etag,
        CreationTime = new DateTimeOffset((DateTime)r.CreationTime, TimeSpan.Zero).ToUnixTimeMilliseconds()
    };
}
