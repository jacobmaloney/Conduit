using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Conduit.DataAccess.Repositories;

namespace Conduit.Web.Services
{
    /// <summary>
    /// Service for managing API tokens
    /// </summary>
    public class ApiTokenService
    {
        private readonly ApiTokenRepository _repository;

        public ApiTokenService(ApiTokenRepository repository)
        {
            _repository = repository;
        }

        /// <summary>
        /// Creates a new API token with a random value. Returns the raw token (with scim_ prefix).
        /// </summary>
        public Task<string> CreateTokenAsync(string name, string? description, DateTime? expiresAt) =>
            CreateTokenAsync(name, description, expiresAt, tenantId: null, scope: "Tenant", fixedRawValue: null);

        /// <summary>
        /// Creates a new API token. If <paramref name="fixedRawValue"/> is provided, it is used
        /// as the raw token value (allowing deterministic demo tokens such as "demo-it-2024").
        /// The full bearer header is always <c>scim_{value}</c>.
        /// </summary>
        // Tokens minted without an explicit expiration default to this much wall-clock —
        // long enough for a real provisioner not to surprise-rotate during a quarter, short
        // enough that a forgotten token doesn't live forever. Fixed-value demo tokens
        // (EnsureFixedTokenAsync) intentionally pass null and stay non-expiring.
        public static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromDays(90);

        public async Task<string> CreateTokenAsync(
            string name,
            string? description,
            DateTime? expiresAt,
            Guid? tenantId,
            string scope,
            string? fixedRawValue)
        {
            var tokenId = Guid.NewGuid();
            var tokenValue = string.IsNullOrEmpty(fixedRawValue) ? GenerateSecureToken() : fixedRawValue;
            var hashedToken = HashToken(tokenValue);

            // Only apply the default to random-value tokens — demo seed passes a fixed value
            // AND null expiresAt to mean "never expire," which is the right behavior for
            // documented sample credentials.
            DateTime? effectiveExpiry = expiresAt;
            if (effectiveExpiry is null && string.IsNullOrEmpty(fixedRawValue))
            {
                effectiveExpiry = DateTime.UtcNow.Add(DefaultTokenLifetime);
            }

            await _repository.InsertAsync(new ApiTokenRecord
            {
                Id = tokenId,
                Name = name,
                Description = description,
                TokenHash = hashedToken,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = effectiveExpiry,
                IsActive = true,
                TenantId = tenantId,
                Scope = string.IsNullOrEmpty(scope) ? "Tenant" : scope
            });

            return $"scim_{tokenValue}";
        }

        /// <summary>
        /// Idempotent: creates the token only if no row exists with the same hash.
        /// Returns the raw token. Used by demo seed for fixed-value tokens.
        /// </summary>
        public async Task<string> EnsureFixedTokenAsync(string name, string rawValue, Guid? tenantId, string scope, string? description = null)
        {
            var hash = HashToken(rawValue);
            var exists = await _repository.CountByHashAsync(hash);
            if (exists == 0)
            {
                await CreateTokenAsync(name, description, expiresAt: null, tenantId, scope, fixedRawValue: rawValue);
            }
            return $"scim_{rawValue}";
        }

        /// <summary>
        /// Gets all tokens (without the actual token values)
        /// </summary>
        public async Task<List<ApiToken>> GetAllTokensAsync()
        {
            var rows = await _repository.GetAllAsync();
            return rows.Select(MapRecord).ToList();
        }

        /// <summary>
        /// Validates a token
        /// </summary>
        public async Task<ApiToken?> ValidateTokenAsync(string token)
        {
            if (string.IsNullOrEmpty(token) || !token.StartsWith("scim_"))
                return null;

            var tokenValue = token.Substring(5); // Remove "scim_" prefix
            var hashedToken = HashToken(tokenValue);

            var record = await _repository.GetActiveByHashAsync(hashedToken, DateTime.UtcNow);
            if (record == null)
                return null;

            // Update last used timestamp
            await _repository.TouchLastUsedAsync(record.Id, DateTime.UtcNow);

            return MapRecord(record);
        }

        /// <summary>
        /// Toggles a token's active status
        /// </summary>
        public Task ToggleTokenAsync(Guid tokenId) => _repository.ToggleActiveAsync(tokenId);

        /// <summary>
        /// Deletes a token
        /// </summary>
        public Task DeleteTokenAsync(Guid tokenId) => _repository.DeleteAsync(tokenId);

        private static ApiToken MapRecord(ApiTokenRecord r) => new()
        {
            Id = r.Id,
            Name = r.Name,
            Description = r.Description,
            TokenHash = r.TokenHash,
            CreatedAt = r.CreatedAt,
            LastUsedAt = r.LastUsedAt,
            ExpiresAt = r.ExpiresAt,
            IsActive = r.IsActive,
            TenantId = r.TenantId,
            Scope = r.Scope
        };

        /// <summary>
        /// Generates a secure random token
        /// </summary>
        private string GenerateSecureToken()
        {
            var randomBytes = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(randomBytes);
            return Convert.ToBase64String(randomBytes)
                .Replace("+", "")
                .Replace("/", "")
                .Replace("=", "");
        }

        /// <summary>
        /// Hashes a token for secure storage
        /// </summary>
        private string HashToken(string token)
        {
            using var sha256 = SHA256.Create();
            var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
            return Convert.ToBase64String(hashedBytes);
        }
    }

    /// <summary>
    /// API Token model
    /// </summary>
    public class ApiToken
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public string TokenHash { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime? LastUsedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public bool IsActive { get; set; }

        /// <summary>
        /// Connected System scope. NULL = Admin / all systems (only valid when Scope = "Admin" or "ArsProxy").
        /// </summary>
        public Guid? TenantId { get; set; }

        /// <summary>
        /// Authorization scope: "Admin" | "Tenant" | "ArsProxy". Default = "Tenant".
        /// </summary>
        public string Scope { get; set; } = "Tenant";
    }
}