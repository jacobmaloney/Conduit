using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Conduit.Core.Models
{
    /// <summary>
    /// SCIM Group resource
    /// </summary>
    public class ScimGroup : ScimResource
    {
        /// <summary>
        /// Initializes a new instance of the ScimGroup class
        /// </summary>
        public ScimGroup()
        {
            Schemas.Add("urn:ietf:params:scim:schemas:core:2.0:Group");
            Meta.ResourceType = "Group";
        }

        /// <summary>
        /// Gets or sets the group display name
        /// </summary>
        [JsonProperty("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the group description
        /// </summary>
        [JsonProperty("description")]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the group type
        /// </summary>
        [JsonProperty("type")]
        public string? Type { get; set; }

        /// <summary>
        /// Gets or sets the group owner
        /// </summary>
        [JsonProperty("owner")]
        public ScimGroupMember? Owner { get; set; }

        /// <summary>
        /// Gets or sets the group members
        /// </summary>
        [JsonProperty("members")]
        public List<ScimGroupMember> Members { get; set; } = new List<ScimGroupMember>();
    }

    /// <summary>
    /// SCIM group member complex type
    /// </summary>
    public class ScimGroupMember
    {
        /// <summary>
        /// Gets or sets the member value (ID)
        /// </summary>
        [JsonProperty("value")]
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the reference URI
        /// </summary>
        [JsonProperty("$ref")]
        public string? Ref { get; set; }

        /// <summary>
        /// Gets or sets the member type (User or Group)
        /// </summary>
        [JsonProperty("type")]
        public string Type { get; set; } = "User";

        /// <summary>
        /// Gets or sets the display value
        /// </summary>
        [JsonProperty("display")]
        public string? Display { get; set; }
    }
}