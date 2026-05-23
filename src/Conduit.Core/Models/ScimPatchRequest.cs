using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Conduit.Core.Models
{
    /// <summary>
    /// SCIM patch request
    /// </summary>
    public class ScimPatchRequest
    {
        /// <summary>
        /// Gets or sets the request schemas
        /// </summary>
        [JsonProperty("schemas")]
        public List<string> Schemas { get; set; } = new List<string> { "urn:ietf:params:scim:api:messages:2.0:PatchOp" };

        /// <summary>
        /// Gets or sets the patch operations
        /// </summary>
        [JsonProperty("Operations")]
        public List<ScimPatchOperation> Operations { get; set; } = new List<ScimPatchOperation>();
    }

    /// <summary>
    /// SCIM patch operation
    /// </summary>
    public class ScimPatchOperation
    {
        /// <summary>
        /// Gets or sets the operation type
        /// </summary>
        [JsonProperty("op")]
        [JsonConverter(typeof(StringEnumConverter))]
        public ScimPatchOperationType Op { get; set; }

        /// <summary>
        /// Gets or sets the operation path
        /// </summary>
        [JsonProperty("path")]
        public string? Path { get; set; }

        /// <summary>
        /// Gets or sets the operation value
        /// </summary>
        [JsonProperty("value")]
        public object? Value { get; set; }
    }

    /// <summary>
    /// SCIM patch operation types
    /// </summary>
    public enum ScimPatchOperationType
    {
        /// <summary>
        /// Add operation
        /// </summary>
        [JsonProperty("add")]
        Add,

        /// <summary>
        /// Remove operation
        /// </summary>
        [JsonProperty("remove")]
        Remove,

        /// <summary>
        /// Replace operation
        /// </summary>
        [JsonProperty("replace")]
        Replace
    }
}