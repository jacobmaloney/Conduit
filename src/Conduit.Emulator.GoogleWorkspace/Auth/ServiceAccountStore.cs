using Dapper;
using Microsoft.Data.SqlClient;
using Conduit.DataAccess;

namespace Conduit.Emulator.GoogleWorkspace.Auth;

public sealed record ServiceAccountRecord(
    string ClientEmail,
    string ClientId,
    string PrivateKeyId,
    string PublicKeyPem,
    string PrivateKeyPem,
    string ProjectId,
    string AllowedScopes,
    bool Disabled,
    DateTime CreatedAt);

public sealed record IssuedToken(
    string Token,
    string ClientEmail,
    string? Subject,
    string Scopes,
    DateTime IssuedAt,
    DateTime ExpiresAt);

public sealed class ServiceAccountStore
{
    private readonly DatabaseConfig _config;

    public ServiceAccountStore(DatabaseConfig config) => _config = config;

    private SqlConnection Open()
    {
        var c = new SqlConnection(_config.ConnectionString);
        c.Open();
        return c;
    }

    public async Task<ServiceAccountRecord?> GetByClientEmailAsync(string clientEmail)
    {
        using var c = Open();
        return await c.QuerySingleOrDefaultAsync<ServiceAccountRecord>(
            "SELECT ClientEmail, ClientId, PrivateKeyId, PublicKeyPem, PrivateKeyPem, ProjectId, AllowedScopes, Disabled, CreatedAt FROM gw_service_accounts WHERE ClientEmail = @ClientEmail",
            new { ClientEmail = clientEmail });
    }

    public async Task<IReadOnlyList<ServiceAccountRecord>> ListAsync()
    {
        using var c = Open();
        var rows = await c.QueryAsync<ServiceAccountRecord>(
            "SELECT ClientEmail, ClientId, PrivateKeyId, PublicKeyPem, PrivateKeyPem, ProjectId, AllowedScopes, Disabled, CreatedAt FROM gw_service_accounts ORDER BY CreatedAt");
        return rows.ToList();
    }

    public async Task UpsertAsync(ServiceAccountRecord record)
    {
        using var c = Open();
        const string sql = @"
MERGE gw_service_accounts AS t
USING (SELECT @ClientEmail AS ClientEmail) AS s ON t.ClientEmail = s.ClientEmail
WHEN MATCHED THEN UPDATE SET
    ClientId = @ClientId, PrivateKeyId = @PrivateKeyId, PublicKeyPem = @PublicKeyPem,
    PrivateKeyPem = @PrivateKeyPem, ProjectId = @ProjectId, AllowedScopes = @AllowedScopes,
    Disabled = @Disabled
WHEN NOT MATCHED THEN INSERT (ClientEmail, ClientId, PrivateKeyId, PublicKeyPem, PrivateKeyPem, ProjectId, AllowedScopes, Disabled, CreatedAt)
    VALUES (@ClientEmail, @ClientId, @PrivateKeyId, @PublicKeyPem, @PrivateKeyPem, @ProjectId, @AllowedScopes, @Disabled, @CreatedAt);";
        await c.ExecuteAsync(sql, record);
    }

    public async Task<int> CountAsync()
    {
        using var c = Open();
        return await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM gw_service_accounts");
    }

    public async Task StoreAccessTokenAsync(IssuedToken token)
    {
        using var c = Open();
        await c.ExecuteAsync(
            @"INSERT INTO gw_access_tokens (Token, ClientEmail, Subject, Scopes, IssuedAt, ExpiresAt)
              VALUES (@Token, @ClientEmail, @Subject, @Scopes, @IssuedAt, @ExpiresAt)",
            token);
    }

    public async Task<IssuedToken?> GetAccessTokenAsync(string token)
    {
        using var c = Open();
        return await c.QuerySingleOrDefaultAsync<IssuedToken>(
            "SELECT Token, ClientEmail, Subject, Scopes, IssuedAt, ExpiresAt FROM gw_access_tokens WHERE Token = @Token",
            new { Token = token });
    }

    public async Task PurgeExpiredTokensAsync()
    {
        using var c = Open();
        await c.ExecuteAsync("DELETE FROM gw_access_tokens WHERE ExpiresAt < SYSUTCDATETIME()");
    }
}
