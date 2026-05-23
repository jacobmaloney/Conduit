using System.Collections.Generic;
using Newtonsoft.Json;

namespace Conduit.Core.Models
{
    /// <summary>
    /// SCIM bulk request
    /// </summary>
    public class ScimBulkRequest
    {
        /// <summary>
        /// Gets or sets the request schemas
        /// </summary>
        [JsonProperty("schemas")]
        public List<string> Schemas { get; set; } = new List<string> { "urn:ietf:params:scim:api:messages:2.0:BulkRequest" };

        /// <summary>
        /// Gets or sets the bulk operations
        /// </summary>
        [JsonProperty("Operations")]
        public List<ScimBulkOperation> Operations { get; set; } = new List<ScimBulkOperation>();

        /// <summary>
        /// Gets or sets the number of errors to accept before failing
        /// </summary>
        [JsonProperty("failOnErrors")]
        public int? FailOnErrors { get; set; }
    }

    /// <summary>
    /// SCIM bulk operation
    /// </summary>
    public class ScimBulkOperation
    {
        /// <summary>
        /// Gets or sets the HTTP method
        /// </summary>
        [JsonProperty("method")]
        public string Method { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the bulk ID for referencing
        /// </summary>
        [JsonProperty("bulkId")]
        public string? BulkId { get; set; }

        /// <summary>
        /// Gets or sets the resource path
        /// </summary>
        [JsonProperty("path")]
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the operation data
        /// </summary>
        [JsonProperty("data")]
        public object? Data { get; set; }

        /// <summary>
        /// Gets or sets the version for conditional operations
        /// </summary>
        [JsonProperty("version")]
        public string? Version { get; set; }
    }

    /// <summary>
    /// SCIM bulk response
    /// </summary>
    public class ScimBulkResponse
    {
        /// <summary>
        /// Gets or sets the response schemas
        /// </summary>
        [JsonProperty("schemas")]
        public List<string> Schemas { get; set; } = new List<string> { "urn:ietf:params:scim:api:messages:2.0:BulkResponse" };

        /// <summary>
        /// Gets or sets the bulk operation responses
        /// </summary>
        [JsonProperty("Operations")]
        public List<ScimBulkOperationResponse> Operations { get; set; } = new List<ScimBulkOperationResponse>();
    }

    /// <summary>
    /// SCIM bulk operation response
    /// </summary>
    public class ScimBulkOperationResponse
    {
        /// <summary>
        /// Gets or sets the HTTP method
        /// </summary>
        [JsonProperty("method")]
        public string Method { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the bulk ID
        /// </summary>
        [JsonProperty("bulkId")]
        public string? BulkId { get; set; }

        /// <summary>
        /// Gets or sets the version
        /// </summary>
        [JsonProperty("version")]
        public string? Version { get; set; }

        /// <summary>
        /// Gets or sets the location of created resource
        /// </summary>
        [JsonProperty("location")]
        public string? Location { get; set; }

        /// <summary>
        /// Gets or sets the response data
        /// </summary>
        [JsonProperty("response")]
        public object? Response { get; set; }

        /// <summary>
        /// Gets or sets the status
        /// </summary>
        [JsonProperty("status")]
        public int Status { get; set; }
    }
}