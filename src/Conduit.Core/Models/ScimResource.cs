using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Conduit.Core.Models
{
    /// <summary>
    /// Base class for all SCIM resources
    /// </summary>
    public abstract class ScimResource
    {
        /// <summary>
        /// Gets or sets the resource identifier
        /// </summary>
        [JsonProperty("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Gets or sets the external identifier
        /// </summary>
        [JsonProperty("externalId")]
        public string? ExternalId { get; set; }

        /// <summary>
        /// Gets or sets the resource schemas
        /// </summary>
        [JsonProperty("schemas")]
        public List<string> Schemas { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets the metadata
        /// </summary>
        [JsonProperty("meta")]
        public ScimMetadata Meta { get; set; } = new ScimMetadata();
    }

    /// <summary>
    /// SCIM resource metadata
    /// </summary>
    public class ScimMetadata
    {
        /// <summary>
        /// Gets or sets the resource type
        /// </summary>
        [JsonProperty("resourceType")]
        public string? ResourceType { get; set; }

        /// <summary>
        /// Gets or sets when the resource was created
        /// </summary>
        [JsonProperty("created")]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets when the resource was last modified
        /// </summary>
        [JsonProperty("lastModified")]
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets the resource location
        /// </summary>
        [JsonProperty("location")]
        public string? Location { get; set; }

        /// <summary>
        /// Gets or sets the resource version
        /// </summary>
        [JsonProperty("version")]
        public string? Version { get; set; }
    }
}