namespace Conduit.Web.Authentication
{
    /// <summary>
    /// JWT configuration settings
    /// </summary>
    public class JwtConfig
    {
        /// <summary>
        /// Gets or sets the JWT secret key
        /// </summary>
        public string SecretKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the JWT issuer
        /// </summary>
        public string Issuer { get; set; } = "Conduit";

        /// <summary>
        /// Gets or sets the JWT audience
        /// </summary>
        public string Audience { get; set; } = "ConduitAPI";

        /// <summary>
        /// Gets or sets the token expiration in minutes
        /// </summary>
        public int ExpirationMinutes { get; set; } = 60;

        /// <summary>
        /// Gets or sets whether to validate the issuer
        /// </summary>
        public bool ValidateIssuer { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to validate the audience
        /// </summary>
        public bool ValidateAudience { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to validate the lifetime
        /// </summary>
        public bool ValidateLifetime { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to validate the signing key
        /// </summary>
        public bool ValidateIssuerSigningKey { get; set; } = true;
    }
}