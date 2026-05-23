using System;
using System.Collections.Generic;

namespace Conduit.Core.Models
{
    /// <summary>
    /// Department for organizational structure
    /// </summary>
    public class Department
    {
        /// <summary>
        /// Gets or sets the department ID
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Gets or sets the department name
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the parent department ID
        /// </summary>
        public Guid? ParentId { get; set; }

        /// <summary>
        /// Gets or sets the department level in the hierarchy
        /// </summary>
        public int Level { get; set; }

        /// <summary>
        /// Gets or sets when the department was created
        /// </summary>
        public DateTime Created { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets the parent department
        /// </summary>
        public Department? Parent { get; set; }

        /// <summary>
        /// Gets or sets the child departments
        /// </summary>
        public List<Department> Children { get; set; } = new List<Department>();

        /// <summary>
        /// Gets or sets the employees in this department
        /// </summary>
        public List<ScimUser> Employees { get; set; } = new List<ScimUser>();
    }
}