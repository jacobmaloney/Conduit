using Dapper;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using Conduit.DataAccess;
using Conduit.Emulator.GoogleWorkspace.Infrastructure;
using Conduit.Emulator.GoogleWorkspace.Models;

namespace Conduit.Emulator.GoogleWorkspace.Repositories;

public sealed class GwCustomerRepository
{
    private readonly DatabaseConfig _config;

    public GwCustomerRepository(DatabaseConfig config) => _config = config;

    private SqlConnection Open()
    {
        var c = new SqlConnection(_config.ConnectionString);
        c.Open();
        return c;
    }

    public async Task<GwCustomer?> GetAsync(string customerKey)
    {
        using var c = Open();
        var row = await c.QuerySingleOrDefaultAsync<dynamic>(
            @"SELECT CustomerId, CustomerDomain, AlternateEmail, PhoneNumber, Language, PostalAddress_JSON, Etag, CreationTime
                FROM gw_customers
               WHERE CustomerId = @Key OR CustomerId = 'my_customer' OR @Key = 'my_customer'", new { Key = customerKey });
        return row is null ? null : Map(row);
    }

    public async Task<GwCustomer?> GetDefaultAsync()
    {
        using var c = Open();
        var row = await c.QuerySingleOrDefaultAsync<dynamic>(
            @"SELECT TOP 1 CustomerId, CustomerDomain, AlternateEmail, PhoneNumber, Language, PostalAddress_JSON, Etag, CreationTime
                FROM gw_customers ORDER BY CreationTime");
        return row is null ? null : Map(row);
    }

    public async Task UpsertAsync(string customerId, string customerDomain)
    {
        using var c = Open();
        await c.ExecuteAsync(@"
MERGE gw_customers AS t
USING (SELECT @CustomerId AS CustomerId) AS s ON t.CustomerId = s.CustomerId
WHEN MATCHED THEN UPDATE SET CustomerDomain = @CustomerDomain, Etag = @Etag
WHEN NOT MATCHED THEN INSERT (CustomerId, CustomerDomain, Etag)
                       VALUES (@CustomerId, @CustomerDomain, @Etag);",
            new { CustomerId = customerId, CustomerDomain = customerDomain, Etag = EtagGenerator.New() });
    }

    public async Task<int> CountAsync()
    {
        using var c = Open();
        return await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM gw_customers");
    }

    private static GwCustomer Map(dynamic r) => new()
    {
        Id = r.CustomerId,
        CustomerDomain = r.CustomerDomain,
        AlternateEmail = r.AlternateEmail,
        PhoneNumber = r.PhoneNumber,
        Language = r.Language ?? "en",
        CustomerCreationTime = r.CreationTime,
        Etag = r.Etag,
        PostalAddress = string.IsNullOrEmpty((string?)r.PostalAddress_JSON)
            ? null
            : JsonConvert.DeserializeObject<GwPostalAddress>((string)r.PostalAddress_JSON)
    };
}
