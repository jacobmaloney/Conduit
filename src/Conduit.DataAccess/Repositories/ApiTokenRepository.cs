using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Conduit.DataAccess.Repositories;

/// <summary>
/// Row shape for the ApiTokens table. Mirrors the columns the
/// ApiTokenService reads/writes; the token's raw value is never stored,
/// only its hash.
/// </summary>
public class ApiTokenRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsActive { get; set; }
    public Guid? TenantId { get; set; }
    public string Scope { get; set; } = "Tenant";
}

/// <summary>
/// Dapper data access for the ApiTokens table. The token hashing/validation
/// logic stays in ApiTokenService — this repository only relocates the SQL.
/// </summary>
public class ApiTokenRepository : BaseRepository
{
    public ApiTokenRepository(DatabaseConfig config) : base(config) { }

    public Task InsertAsync(ApiTokenRecord token) =>
        ExecuteAsync(@"
            INSERT INTO ApiTokens (Id, Name, Description, TokenHash, CreatedAt, ExpiresAt, IsActive, TenantId, Scope)
            VALUES (@Id, @Name, @Description, @TokenHash, @CreatedAt, @ExpiresAt, @IsActive, @TenantId, @Scope);",
            token);

    public Task<int> CountByHashAsync(string tokenHash) =>
        ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ApiTokens WHERE TokenHash = @TokenHash",
            new { TokenHash = tokenHash });

    public async Task<List<ApiTokenRecord>> GetAllAsync()
    {
        var rows = await QueryAsync<ApiTokenRecord>("SELECT * FROM ApiTokens ORDER BY CreatedAt DESC;");
        return rows.ToList();
    }

    public Task<ApiTokenRecord?> GetActiveByHashAsync(string tokenHash, DateTime now) =>
        QuerySingleOrDefaultAsync<ApiTokenRecord>(@"
            SELECT * FROM ApiTokens
            WHERE TokenHash = @TokenHash
            AND IsActive = 1
            AND (ExpiresAt IS NULL OR ExpiresAt > @Now);",
            new { TokenHash = tokenHash, Now = now });

    public Task TouchLastUsedAsync(Guid id, DateTime now) =>
        ExecuteAsync(
            "UPDATE ApiTokens SET LastUsedAt = @Now WHERE Id = @Id",
            new { Now = now, Id = id });

    public Task ToggleActiveAsync(Guid id) =>
        ExecuteAsync(@"
            UPDATE ApiTokens
            SET IsActive = CASE WHEN IsActive = 1 THEN 0 ELSE 1 END
            WHERE Id = @Id;",
            new { Id = id });

    public Task DeleteAsync(Guid id) =>
        ExecuteAsync("DELETE FROM ApiTokens WHERE Id = @Id;", new { Id = id });
}
