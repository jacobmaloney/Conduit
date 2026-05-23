using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Conduit.Core.Models
{
    /// <summary>
    /// Custom attribute schema definition
    /// </summary>
    public class CustomAttributeSchema
    {
        /// <summary>
        /// Gets or sets the schema ID
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Gets or sets the schema URN
        /// </summary>
        public string SchemaUrn { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the attribute name
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the attribute description
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the attribute type
        /// </summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public AttributeType Type { get; set; }

        /// <summary>
        /// Gets or sets whether the attribute is multi-valued
        /// </summary>
        public bool MultiValued { get; set; }

        /// <summary>
        /// Gets or sets whether the attribute is required
        /// </summary>
        public bool Required { get; set; }

        /// <summary>
        /// Gets or sets whether the attribute is case-sensitive
        /// </summary>
        public bool CaseExact { get; set; }

        /// <summary>
        /// Gets or sets the attribute mutability
        /// </summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public AttributeMutability Mutability { get; set; } = AttributeMutability.ReadWrite;

        /// <summary>
        /// Gets or sets when the attribute is returned
        /// </summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public AttributeReturned Returned { get; set; } = AttributeReturned.Default;

        /// <summary>
        /// Gets or sets the attribute uniqueness
        /// </summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public AttributeUniqueness Uniqueness { get; set; } = AttributeUniqueness.None;

        /// <summary>
        /// Gets or sets the resource type this attribute applies to
        /// </summary>
        public string ResourceType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets when the attribute was created
        /// </summary>
        public DateTime Created { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Custom attribute value
    /// </summary>
    public class CustomAttributeValue
    {
        /// <summary>
        /// Gets or sets the value ID
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Gets or sets the schema ID
        /// </summary>
        public Guid SchemaId { get; set; }

        /// <summary>
        /// Gets or sets the resource ID this value belongs to
        /// </summary>
        public Guid ResourceId { get; set; }

        /// <summary>
        /// Gets or sets the attribute value
        /// </summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets when the value was created
        /// </summary>
        public DateTime Created { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets when the value was last modified
        /// </summary>
        public DateTime LastModified { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Attribute data types
    /// </summary>
    public enum AttributeType
    {
        /// <summary>
        /// String type
        /// </summary>
        String,

        /// <summary>
        /// Integer type
        /// </summary>
        Integer,

        /// <summary>
        /// Decimal type
        /// </summary>
        Decimal,

        /// <summary>
        /// Boolean type
        /// </summary>
        Boolean,

        /// <summary>
        /// DateTime type
        /// </summary>
        DateTime,

        /// <summary>
        /// Reference type
        /// </summary>
        Reference,

        /// <summary>
        /// Complex type
        /// </summary>
        Complex,

        /// <summary>
        /// Binary type
        /// </summary>
        Binary
    }

    /// <summary>
    /// Attribute mutability options
    /// </summary>
    public enum AttributeMutability
    {
        /// <summary>
        /// Attribute is read-only
        /// </summary>
        ReadOnly,

        /// <summary>
        /// Attribute is read-write
        /// </summary>
        ReadWrite,

        /// <summary>
        /// Attribute is immutable after creation
        /// </summary>
        Immutable,

        /// <summary>
        /// Attribute is write-only
        /// </summary>
        WriteOnly
    }

    /// <summary>
    /// Attribute returned options
    /// </summary>
    public enum AttributeReturned
    {
        /// <summary>
        /// Always returned
        /// </summary>
        Always,

        /// <summary>
        /// Never returned
        /// </summary>
        Never,

        /// <summary>
        /// Returned by default
        /// </summary>
        Default,

        /// <summary>
        /// Returned only on request
        /// </summary>
        Request
    }

    /// <summary>
    /// Attribute uniqueness options
    /// </summary>
    public enum AttributeUniqueness
    {
        /// <summary>
        /// No uniqueness enforced
        /// </summary>
        None,

        /// <summary>
        /// Unique within the server
        /// </summary>
        Server,

        /// <summary>
        /// Globally unique
        /// </summary>
        Global
    }
}