using System.Collections.Generic;
using Newtonsoft.Json;

namespace Conduit.Core.Models
{
    /// <summary>
    /// SCIM error response
    /// </summary>
    public class ScimError
    {
        /// <summary>
        /// Gets or sets the error schemas
        /// </summary>
        [JsonProperty("schemas")]
        public List<string> Schemas { get; set; } = new List<string> { "urn:ietf:params:scim:api:messages:2.0:Error" };

        /// <summary>
        /// Gets or sets the HTTP status code
        /// </summary>
        [JsonProperty("status")]
        public int Status { get; set; }

        /// <summary>
        /// Gets or sets the SCIM error type
        /// </summary>
        [JsonProperty("scimType")]
        public string? ScimType { get; set; }

        /// <summary>
        /// Gets or sets the error detail
        /// </summary>
        [JsonProperty("detail")]
        public string? Detail { get; set; }
    }

    /// <summary>
    /// SCIM error types
    /// </summary>
    public static class ScimErrorType
    {
        /// <summary>
        /// The specified filter syntax was invalid
        /// </summary>
        public const string InvalidFilter = "invalidFilter";

        /// <summary>
        /// The specified filter yields too many results
        /// </summary>
        public const string TooMany = "tooMany";

        /// <summary>
        /// The specified resource does not exist
        /// </summary>
        public const string NotFound = "notFound";

        /// <summary>
        /// The specified version number does not match the resource's latest version
        /// </summary>
        public const string InvalidVersion = "invalidVersion";

        /// <summary>
        /// The specified SCIM path does not exist
        /// </summary>
        public const string NoTarget = "noTarget";

        /// <summary>
        /// The specified path attribute value is invalid
        /// </summary>
        public const string InvalidPath = "invalidPath";

        /// <summary>
        /// The specified attribute value is invalid
        /// </summary>
        public const string InvalidValue = "invalidValue";

        /// <summary>
        /// The specified syntax of the request is invalid
        /// </summary>
        public const string InvalidSyntax = "invalidSyntax";

        /// <summary>
        /// The specified resource already exists
        /// </summary>
        public const string Uniqueness = "uniqueness";

        /// <summary>
        /// The specified attribute is mutability
        /// </summary>
        public const string Mutability = "mutability";

        /// <summary>
        /// The specified request cannot be completed due to the state of another resource
        /// </summary>
        public const string Sensitive = "sensitive";
    }
}