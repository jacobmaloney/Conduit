using System;
using System.Threading.Tasks;
using Dapper;

namespace Conduit.DataAccess.Repositories;

/// <summary>
/// Stores per-tenant credentials as AES-GCM ciphertext. The Ciphertext / Nonce /
/// Tag triple is opaque to SQL; the key comes from configuration at runtime.
/// Callers wrap this with a CredentialProtector that encrypts/decrypts.
/// </summary>
public class ConnectionCredentialRepository : BaseRepository
{
    public ConnectionCredentialRepository(DatabaseConfig config) : base(config) { }

    public sealed class CredentialRow
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string CredentialName { get; set; } = string.Empty;
        public byte[] Ciphertext { get; set; } = Array.Empty<byte>();
        public byte[] Nonce { get; set; } = Array.Empty<byte>();
        public byte[] Tag { get; set; } = Array.Empty<byte>();
        public DateTime CreatedAt { get; set; }
        public DateTime LastModified { get; set; }
    }

    public Task<CredentialRow?> GetAsync(Guid tenantId, string credentialName) =>
        QuerySingleOrDefaultAsync<CredentialRow>(@"
            SELECT * FROM ConnectionCredentials
             WHERE TenantId = @TenantId AND CredentialName = @Name",
            new { TenantId = tenantId, Name = credentialName });

    public async Task UpsertAsync(CredentialRow row)
    {
        if (row.Id == Guid.Empty) row.Id = Guid.NewGuid();
        row.LastModified = DateTime.UtcNow;

        const string sql = @"
            MERGE ConnectionCredentials AS tgt
            USING (SELECT @TenantId AS TenantId, @CredentialName AS CredentialName) AS src
               ON tgt.TenantId = src.TenantId AND tgt.CredentialName = src.CredentialName
            WHEN MATCHED THEN
                UPDATE SET Ciphertext = @Ciphertext,
                           Nonce = @Nonce,
                           Tag = @Tag,
                           LastModified = @LastModified
            WHEN NOT MATCHED THEN
                INSERT (Id, TenantId, CredentialName, Ciphertext, Nonce, Tag, CreatedAt, LastModified)
                VALUES (@Id, @TenantId, @CredentialName, @Ciphertext, @Nonce, @Tag, @CreatedAt, @LastModified);";

        if (row.CreatedAt == default) row.CreatedAt = DateTime.UtcNow;
        await ExecuteAsync(sql, row);
    }

    public Task DeleteAsync(Guid tenantId, string credentialName) =>
        ExecuteAsync(@"
            DELETE FROM ConnectionCredentials
             WHERE TenantId = @TenantId AND CredentialName = @Name",
            new { TenantId = tenantId, Name = credentialName });

    /// <summary>
    /// Phase 2 multi-credential UX: list every credential name stored for a
    /// tenant (no ciphertext). Used by the modal to show "existing credentials"
    /// and by the Sync Project edit form to populate the credential dropdown.
    /// </summary>
    public async Task<System.Collections.Generic.List<string>> ListNamesByTenantAsync(Guid tenantId)
    {
        var rows = await QueryAsync<string>(@"
            SELECT CredentialName FROM ConnectionCredentials
             WHERE TenantId = @TenantId
             ORDER BY CredentialName",
            new { TenantId = tenantId });
        return System.Linq.Enumerable.ToList(rows);
    }
}
