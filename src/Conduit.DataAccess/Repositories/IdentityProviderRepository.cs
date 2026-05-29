using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Conduit.DataAccess.Repositories
{
    /// <summary>
    /// Repository for external identity providers (SSO). Mirrors IdentityCenter's
    /// IdentityProviders shape but lives in Conduit's lighter Dapper stack — see
    /// migration v18. SSO is OPTIONAL: zero rows here means the portal keeps using
    /// local username/password only, which is the standalone default.
    ///
    /// The <see cref="IdentityProvider.Configuration"/> column is a JSON blob whose
    /// ClientSecret field is encrypted at rest (see ISecretProtector). This repository
    /// stores and returns the JSON verbatim — encryption/decryption is the caller's
    /// concern so the secret is never decrypted unless actually needed.
    /// </summary>
    public class IdentityProviderRepository
    {
        private readonly DatabaseConfig _config;

        public IdentityProviderRepository(DatabaseConfig config)
        {
            _config = config;
        }

        private SqlConnection NewConn() => new SqlConnection(_config.ConnectionString);

        public async Task<List<IdentityProvider>> GetAllAsync()
        {
            using var conn = NewConn();
            var rows = await conn.QueryAsync<IdentityProvider>(
                "SELECT * FROM IdentityProviders ORDER BY Name");
            return rows.ToList();
        }

        /// <summary>
        /// Returns only the enabled providers. Used by the startup registrar and the
        /// login page. Tolerates a missing table (fresh / un-migrated DB) by letting
        /// the SqlException propagate to a caller that wraps it — see the registrar.
        /// </summary>
        public async Task<List<IdentityProvider>> GetEnabledAsync()
        {
            using var conn = NewConn();
            var rows = await conn.QueryAsync<IdentityProvider>(
                "SELECT * FROM IdentityProviders WHERE IsEnabled = 1 ORDER BY Name");
            return rows.ToList();
        }

        public async Task<IdentityProvider?> GetByIdAsync(Guid id)
        {
            using var conn = NewConn();
            return await conn.QuerySingleOrDefaultAsync<IdentityProvider>(
                "SELECT * FROM IdentityProviders WHERE Id = @Id", new { Id = id });
        }

        public async Task<IdentityProvider?> GetByNameAsync(string name)
        {
            using var conn = NewConn();
            return await conn.QuerySingleOrDefaultAsync<IdentityProvider>(
                "SELECT TOP 1 * FROM IdentityProviders WHERE LOWER(Name) = LOWER(@Name)",
                new { Name = name });
        }

        public async Task<IdentityProvider> CreateAsync(IdentityProvider provider)
        {
            provider.Id = provider.Id == Guid.Empty ? Guid.NewGuid() : provider.Id;
            provider.CreatedAt = DateTime.UtcNow;
            provider.ModifiedAt = DateTime.UtcNow;

            using var conn = NewConn();
            await conn.ExecuteAsync(@"
                INSERT INTO IdentityProviders
                    (Id, Name, Type, IsEnabled, IsPrimary, Configuration, Metadata,
                     CreatedAt, CreatedBy, ModifiedAt, ModifiedBy)
                VALUES
                    (@Id, @Name, @Type, @IsEnabled, @IsPrimary, @Configuration, @Metadata,
                     @CreatedAt, @CreatedBy, @ModifiedAt, @ModifiedBy)",
                provider);
            return provider;
        }

        public async Task UpdateAsync(IdentityProvider provider)
        {
            provider.ModifiedAt = DateTime.UtcNow;
            using var conn = NewConn();
            await conn.ExecuteAsync(@"
                UPDATE IdentityProviders SET
                    Name = @Name,
                    Type = @Type,
                    IsEnabled = @IsEnabled,
                    IsPrimary = @IsPrimary,
                    Configuration = @Configuration,
                    Metadata = @Metadata,
                    ModifiedAt = @ModifiedAt,
                    ModifiedBy = @ModifiedBy
                WHERE Id = @Id",
                provider);
        }

        public async Task SetEnabledAsync(Guid id, bool enabled, string? modifiedBy)
        {
            using var conn = NewConn();
            await conn.ExecuteAsync(@"
                UPDATE IdentityProviders
                   SET IsEnabled = @Enabled, ModifiedAt = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy
                 WHERE Id = @Id",
                new { Id = id, Enabled = enabled, ModifiedBy = modifiedBy });
        }

        public async Task<bool> DeleteAsync(Guid id)
        {
            using var conn = NewConn();
            var rows = await conn.ExecuteAsync(
                "DELETE FROM IdentityProviders WHERE Id = @Id", new { Id = id });
            return rows > 0;
        }
    }

    /// <summary>
    /// An external identity provider configuration row. The <see cref="Configuration"/>
    /// string is JSON; its shape is <c>IdentityProviderConfig</c> in Conduit.Web. The
    /// ClientSecret inside that JSON is encrypted at rest.
    /// </summary>
    public class IdentityProvider
    {
        public Guid Id { get; set; }

        /// <summary>Doubles as the OIDC authentication scheme name. Must be unique.</summary>
        public string Name { get; set; } = "";

        /// <summary>"OIDC" | "AzureAD" | "OAuth".</summary>
        public string Type { get; set; } = "OIDC";

        public bool IsEnabled { get; set; }
        public bool IsPrimary { get; set; }

        /// <summary>JSON: ClientId, ClientSecret (ENCRYPTED), Authority, TenantId, Scopes, ClaimMappings.</summary>
        public string Configuration { get; set; } = "{}";

        public string? Metadata { get; set; }

        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime ModifiedAt { get; set; }
        public string? ModifiedBy { get; set; }
    }
}
