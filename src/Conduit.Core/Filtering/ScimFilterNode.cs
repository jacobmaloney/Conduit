using System;

namespace Conduit.Core.Models
{
    /// <summary>
    /// Base class for SCIM filter AST nodes
    /// </summary>
    public abstract class ScimFilterNode
    {
    }

    /// <summary>
    /// Comparison filter node (e.g., userName eq "john")
    /// </summary>
    public class ComparisonNode : ScimFilterNode
    {
        public string AttributePath { get; set; } = string.Empty;
        public string Operator { get; set; } = string.Empty;
        public string? Value { get; set; }
    }

    /// <summary>
    /// Present filter node (e.g., title pr)
    /// </summary>
    public class PresentNode : ScimFilterNode
    {
        public string AttributePath { get; set; } = string.Empty;
    }

    /// <summary>
    /// Logical filter node (and/or combining two sub-expressions)
    /// </summary>
    public class LogicalNode : ScimFilterNode
    {
        public string Operator { get; set; } = string.Empty; // "and" or "or"
        public ScimFilterNode Left { get; set; } = null!;
        public ScimFilterNode Right { get; set; } = null!;
    }

    /// <summary>
    /// Exception thrown when a SCIM filter expression cannot be parsed
    /// </summary>
    public class ScimFilterParseException : Exception
    {
        public ScimFilterParseException(string message) : base(message) { }
    }
}
