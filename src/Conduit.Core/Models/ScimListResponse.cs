using System.Collections.Generic;
using Newtonsoft.Json;

namespace Conduit.Core.Models
{
    /// <summary>
    /// SCIM list response for returning multiple resources
    /// </summary>
    /// <typeparam name="T">The type of resources in the list</typeparam>
    public class ScimListResponse<T> where T : ScimResource
    {
        /// <summary>
        /// Gets or sets the response schemas
        /// </summary>
        [JsonProperty("schemas")]
        public List<string> Schemas { get; set; } = new List<string> { "urn:ietf:params:scim:api:messages:2.0:ListResponse" };

        /// <summary>
        /// Gets or sets the total number of results
        /// </summary>
        [JsonProperty("totalResults")]
        public int TotalResults { get; set; }

        /// <summary>
        /// Gets or sets the number of results per page
        /// </summary>
        [JsonProperty("itemsPerPage")]
        public int ItemsPerPage { get; set; }

        /// <summary>
        /// Gets or sets the 1-based start index
        /// </summary>
        [JsonProperty("startIndex")]
        public int StartIndex { get; set; } = 1;

        /// <summary>
        /// Gets or sets the resources
        /// </summary>
        [JsonProperty("Resources")]
        public List<T> Resources { get; set; } = new List<T>();
    }
}