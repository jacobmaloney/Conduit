using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Conduit.Core.Models;

namespace Conduit.Web.Authentication
{
    /// <summary>
    /// Service for generating and validating JWT tokens
    /// </summary>
    public class TokenService
    {
        private readonly JwtConfig _jwtConfig;
        private readonly JwtSecurityTokenHandler _tokenHandler;

        /// <summary>
        /// Initializes a new instance of the TokenService class
        /// </summary>
        /// <param name="jwtConfig">JWT configuration</param>
        public TokenService(IOptions<JwtConfig> jwtConfig)
        {
            _jwtConfig = jwtConfig.Value;
            _tokenHandler = new JwtSecurityTokenHandler();
        }

        /// <summary>
        /// Generates a JWT token for the given API token
        /// </summary>
        /// <param name="apiToken">The API token</param>
        /// <returns>JWT token string</returns>
        public string GenerateJwtToken(ApiToken apiToken)
        {
            var key = Encoding.UTF8.GetBytes(_jwtConfig.SecretKey);
            var signingCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, apiToken.Id.ToString()),
                new Claim(ClaimTypes.Name, apiToken.Name),
                new Claim("TokenType", apiToken.Type.ToString())
            };

            // Add scopes as claims
            foreach (var scope in apiToken.ScopeList)
            {
                claims.Add(new Claim("scope", scope));
            }

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = apiToken.ExpiresAt ?? DateTime.UtcNow.AddMinutes(_jwtConfig.ExpirationMinutes),
                Issuer = _jwtConfig.Issuer,
                Audience = _jwtConfig.Audience,
                SigningCredentials = signingCredentials
            };

            var token = _tokenHandler.CreateToken(tokenDescriptor);
            return _tokenHandler.WriteToken(token);
        }

        /// <summary>
        /// Validates a JWT token
        /// </summary>
        /// <param name="token">The JWT token</param>
        /// <returns>Claims principal if valid, null otherwise</returns>
        public ClaimsPrincipal? ValidateToken(string token)
        {
            try
            {
                var key = Encoding.UTF8.GetBytes(_jwtConfig.SecretKey);
                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = _jwtConfig.ValidateIssuer,
                    ValidateAudience = _jwtConfig.ValidateAudience,
                    ValidateLifetime = _jwtConfig.ValidateLifetime,
                    ValidateIssuerSigningKey = _jwtConfig.ValidateIssuerSigningKey,
                    ValidIssuer = _jwtConfig.Issuer,
                    ValidAudience = _jwtConfig.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ClockSkew = TimeSpan.Zero
                };

                var principal = _tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);
                return principal;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Generates a secure random API key
        /// </summary>
        /// <returns>A secure random API key</returns>
        public static string GenerateApiKey()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return Convert.ToBase64String(bytes)
                .Replace("+", "-")
                .Replace("/", "_")
                .TrimEnd('=');
        }

        /// <summary>
        /// Hashes an API token
        /// </summary>
        /// <param name="token">The token to hash</param>
        /// <returns>Hashed token</returns>
        public static string HashToken(string token)
        {
            using (var sha256 = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(token);
                var hash = sha256.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }
        }
    }
}