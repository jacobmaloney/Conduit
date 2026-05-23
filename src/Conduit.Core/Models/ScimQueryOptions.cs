using System.Collections.Generic;

namespace Conduit.Core.Models
{
    /// <summary>
    /// SCIM query options for filtering, sorting, and pagination
    /// </summary>
    public class ScimQueryOptions
    {
        /// <summary>
        /// Gets or sets the filter expression
        /// </summary>
        public string? Filter { get; set; }

        /// <summary>
        /// Gets or sets the sort by attribute
        /// </summary>
        public string? SortBy { get; set; }

        /// <summary>
        /// Gets or sets the sort order (ascending or descending)
        /// </summary>
        public string? SortOrder { get; set; } = "ascending";

        /// <summary>
        /// Gets or sets the 1-based start index
        /// </summary>
        public int StartIndex { get; set; } = 1;

        /// <summary>
        /// Gets or sets the count of results to return
        /// </summary>
        public int Count { get; set; } = 100;

        /// <summary>
        /// Gets or sets the attributes to return
        /// </summary>
        public List<string> Attributes { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets the attributes to exclude
        /// </summary>
        public List<string> ExcludedAttributes { get; set; } = new List<string>();

        /// <summary>
        /// Gets whether sort order is descending
        /// </summary>
        public bool IsDescending => SortOrder?.ToLowerInvariant() == "descending";

        /// <summary>
        /// Gets the zero-based skip value for pagination
        /// </summary>
        public int Skip => StartIndex > 0 ? StartIndex - 1 : 0;
    }
}