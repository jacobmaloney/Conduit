using System;
using System.Collections.Generic;

namespace Conduit.Core.Generation
{
    /// <summary>
    /// Options for generating users
    /// </summary>
    public class UserGenerationOptions
    {
        /// <summary>
        /// Target Connected System (UI label) / Tenant (DB) the generated users
        /// belong to. Required at the UI level; persistence layer falls back to
        /// the Default tenant if null.
        /// </summary>
        public Guid? TenantId { get; set; }

        /// <summary>
        /// Gets or sets the number of users to generate
        /// </summary>
        public int NumberOfUsers { get; set; } = 100;

        /// <summary>
        /// Gets or sets the company structure template to use
        /// </summary>
        public string CompanyTemplate { get; set; } = "TechCompany";

        /// <summary>
        /// Gets or sets custom company structure (overrides template)
        /// </summary>
        public CompanyStructure? CustomStructure { get; set; }

        /// <summary>
        /// Gets or sets the email pattern
        /// </summary>
        public EmailPattern EmailPattern { get; set; } = EmailPattern.FirstLastName;

        /// <summary>
        /// Gets or sets the custom email domain
        /// </summary>
        public string? CustomEmailDomain { get; set; }

        /// <summary>
        /// Gets or sets whether to use random emails
        /// </summary>
        public bool UseRandomEmails { get; set; }

        /// <summary>
        /// Gets or sets the percentage of active users
        /// </summary>
        public double ActiveUserPercentage { get; set; } = 0.95;

        /// <summary>
        /// Gets or sets whether to generate phone numbers
        /// </summary>
        public bool GeneratePhoneNumbers { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to generate addresses
        /// </summary>
        public bool GenerateAddresses { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to assign managers
        /// </summary>
        public bool AssignManagers { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to create department groups
        /// </summary>
        public bool CreateDepartmentGroups { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to create location groups
        /// </summary>
        public bool CreateLocationGroups { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to create role groups
        /// </summary>
        public bool CreateRoleGroups { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to assign skills
        /// </summary>
        public bool AssignSkills { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to assign certifications
        /// </summary>
        public bool AssignCertifications { get; set; } = true;

        /// <summary>
        /// Gets or sets the start date range for hire dates
        /// </summary>
        public DateTime HireDateRangeStart { get; set; } = DateTime.Now.AddYears(-10);

        /// <summary>
        /// Gets or sets the end date range for hire dates
        /// </summary>
        public DateTime HireDateRangeEnd { get; set; } = DateTime.Now;

        /// <summary>
        /// Gets or sets specific emails to include
        /// </summary>
        public List<string> SpecificEmails { get; set; } = new();

        /// <summary>
        /// Gets or sets the random seed for reproducible generation
        /// </summary>
        public int? RandomSeed { get; set; }
    }

    /// <summary>
    /// Email pattern options
    /// </summary>
    public enum EmailPattern
    {
        /// <summary>
        /// firstname.lastname@domain
        /// </summary>
        FirstLastName,

        /// <summary>
        /// firstname@domain
        /// </summary>
        FirstName,

        /// <summary>
        /// f.lastname@domain
        /// </summary>
        FirstInitialLastName,

        /// <summary>
        /// firstname.l@domain
        /// </summary>
        FirstNameLastInitial,

        /// <summary>
        /// flastname@domain
        /// </summary>
        FirstInitialLastNameNoSeparator,

        /// <summary>
        /// Random username@domain
        /// </summary>
        Random
    }

    /// <summary>
    /// Result of user generation
    /// </summary>
    public class UserGenerationResult
    {
        /// <summary>
        /// Gets or sets the generated users
        /// </summary>
        public List<GeneratedUser> Users { get; set; } = new();

        /// <summary>
        /// Gets or sets the generated groups
        /// </summary>
        public List<GeneratedGroup> Groups { get; set; } = new();

        /// <summary>
        /// Gets or sets the organizational hierarchy
        /// </summary>
        public OrganizationalHierarchy? Hierarchy { get; set; }

        /// <summary>
        /// Gets or sets generation statistics
        /// </summary>
        public GenerationStatistics Statistics { get; set; } = new();
    }

    /// <summary>
    /// Represents a generated user
    /// </summary>
    public class GeneratedUser
    {
        /// <summary>
        /// Gets or sets the user ID
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Gets or sets the username
        /// </summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the email
        /// </summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the first name
        /// </summary>
        public string FirstName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the last name
        /// </summary>
        public string LastName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the display name
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the job title
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the department
        /// </summary>
        public string Department { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the manager ID
        /// </summary>
        public string? ManagerId { get; set; }

        /// <summary>
        /// Gets or sets the phone number
        /// </summary>
        public string? PhoneNumber { get; set; }

        /// <summary>
        /// Gets or sets the work location
        /// </summary>
        public Location? Location { get; set; }

        /// <summary>
        /// Gets or sets the employee number
        /// </summary>
        public string EmployeeNumber { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the cost center
        /// </summary>
        public string? CostCenter { get; set; }

        /// <summary>
        /// Gets or sets the hire date
        /// </summary>
        public DateTime HireDate { get; set; }

        /// <summary>
        /// Gets or sets whether the user is active
        /// </summary>
        public bool Active { get; set; } = true;

        /// <summary>
        /// Gets or sets the user's skills
        /// </summary>
        public List<string> Skills { get; set; } = new();

        /// <summary>
        /// Gets or sets the user's certifications
        /// </summary>
        public List<string> Certifications { get; set; } = new();

        /// <summary>
        /// Gets or sets the groups the user belongs to
        /// </summary>
        public List<string> Groups { get; set; } = new();

        /// <summary>
        /// Gets or sets custom attributes
        /// </summary>
        public Dictionary<string, object> CustomAttributes { get; set; } = new();
    }

    /// <summary>
    /// Represents a generated group
    /// </summary>
    public class GeneratedGroup
    {
        /// <summary>
        /// Gets or sets the group ID
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Gets or sets the group name
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the display name
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the group description
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the group type
        /// </summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the group type (deprecated, use Type instead)
        /// </summary>
        [Obsolete("Use Type instead")]
        public string GroupType 
        { 
            get => Type;
            set => Type = value;
        }

        /// <summary>
        /// Gets or sets the member IDs
        /// </summary>
        public List<string> MemberIds { get; set; } = new();
    }

    /// <summary>
    /// Represents the organizational hierarchy
    /// </summary>
    public class OrganizationalHierarchy
    {
        /// <summary>
        /// Gets or sets the CEO ID
        /// </summary>
        public string CeoId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the executive team
        /// </summary>
        public List<string> ExecutiveIds { get; set; } = new();

        /// <summary>
        /// Gets or sets the management structure
        /// </summary>
        public Dictionary<string, List<string>> ManagementStructure { get; set; } = new();

        /// <summary>
        /// Gets or sets the department heads
        /// </summary>
        public Dictionary<string, string> DepartmentHeads { get; set; } = new();
    }

    /// <summary>
    /// Generation statistics
    /// </summary>
    public class GenerationStatistics
    {
        /// <summary>
        /// Gets or sets the total users generated
        /// </summary>
        public int TotalUsers { get; set; }

        /// <summary>
        /// Gets or sets the total groups generated
        /// </summary>
        public int TotalGroups { get; set; }

        /// <summary>
        /// Gets or sets users by department
        /// </summary>
        public Dictionary<string, int> UsersByDepartment { get; set; } = new();

        /// <summary>
        /// Gets or sets users by location
        /// </summary>
        public Dictionary<string, int> UsersByLocation { get; set; } = new();

        /// <summary>
        /// Gets or sets the number of managers
        /// </summary>
        public int ManagerCount { get; set; }

        /// <summary>
        /// Gets or sets the number of remote workers
        /// </summary>
        public int RemoteWorkerCount { get; set; }

        /// <summary>
        /// Gets or sets the number of active users
        /// </summary>
        public int ActiveUserCount { get; set; }
    }
}