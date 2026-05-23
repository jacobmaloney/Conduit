using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Conduit.Core.Models
{
    /// <summary>
    /// API token for authentication
    /// </summary>
    public class ApiToken
    {
        /// <summary>
        /// Gets or sets the token ID
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Gets or sets the token name
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the token value (only set when creating)
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? Token { get; set; }

        /// <summary>
        /// Gets or sets the token hash (stored in database)
        /// </summary>
        [JsonIgnore]
        public string TokenHash { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the token type
        /// </summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public TokenType Type { get; set; }

        /// <summary>
        /// Gets or sets the token scopes (comma-separated)
        /// </summary>
        public string? Scopes { get; set; }

        /// <summary>
        /// Gets or sets whether the token is active
        /// </summary>
        public bool Active { get; set; } = true;

        /// <summary>
        /// Gets or sets when the token expires
        /// </summary>
        public DateTime? ExpiresAt { get; set; }

        /// <summary>
        /// Gets or sets when the token was created
        /// </summary>
        public DateTime Created { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets when the token was last used
        /// </summary>
        public DateTime? LastUsed { get; set; }

        /// <summary>
        /// Gets the list of scopes
        /// </summary>
        [JsonIgnore]
        public List<string> ScopeList
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Scopes))
                    return new List<string>();
                
                return new List<string>(Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries));
            }
        }

        /// <summary>
        /// Checks if the token has a specific scope
        /// </summary>
        /// <param name="scope">The scope to check</param>
        /// <returns>True if the token has the scope</returns>
        public bool HasScope(string scope)
        {
            if (string.IsNullOrWhiteSpace(scope))
                return false;

            return ScopeList.Contains(scope, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if the token is expired
        /// </summary>
        /// <returns>True if the token is expired</returns>
        public bool IsExpired()
        {
            return ExpiresAt.HasValue && ExpiresAt.Value < DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Token types
    /// </summary>
    public enum TokenType
    {
        /// <summary>
        /// Bearer token
        /// </summary>
        Bearer,

        /// <summary>
        /// Basic authentication
        /// </summary>
        Basic,

        /// <summary>
        /// API key
        /// </summary>
        ApiKey
    }

    /// <summary>
    /// Token scopes
    /// </summary>
    public static class TokenScopes
    {
        /// <summary>
        /// Read users
        /// </summary>
        public const string UsersRead = "users.read";

        /// <summary>
        /// Write users
        /// </summary>
        public const string UsersWrite = "users.write";

        /// <summary>
        /// Read groups
        /// </summary>
        public const string GroupsRead = "groups.read";

        /// <summary>
        /// Write groups
        /// </summary>
        public const string GroupsWrite = "groups.write";

        /// <summary>
        /// Read roles
        /// </summary>
        public const string RolesRead = "roles.read";

        /// <summary>
        /// Write roles
        /// </summary>
        public const string RolesWrite = "roles.write";

        /// <summary>
        /// Admin access
        /// </summary>
        public const string Admin = "admin";

        /// <summary>
        /// All scopes
        /// </summary>
        public static readonly string[] AllScopes = new[]
        {
            UsersRead,
            UsersWrite,
            GroupsRead,
            GroupsWrite,
            RolesRead,
            RolesWrite,
            Admin
        };
    }
}