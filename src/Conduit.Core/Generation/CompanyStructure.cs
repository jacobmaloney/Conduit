using System;
using System.Collections.Generic;

namespace Conduit.Core.Generation
{
    /// <summary>
    /// Represents a company organizational structure for user generation
    /// </summary>
    public class CompanyStructure
    {
        /// <summary>
        /// Gets or sets the company name
        /// </summary>
        public string CompanyName { get; set; } = "Acme Corporation";

        /// <summary>
        /// Gets or sets the list of departments
        /// </summary>
        public List<Department> Departments { get; set; } = new();

        /// <summary>
        /// Gets or sets the executive structure
        /// </summary>
        public ExecutiveStructure Executive { get; set; } = new();

        /// <summary>
        /// Gets or sets the percentage of remote workers
        /// </summary>
        public double RemoteWorkerPercentage { get; set; } = 0.3;

        /// <summary>
        /// Gets or sets the percentage of managers
        /// </summary>
        public double ManagerPercentage { get; set; } = 0.15;

        /// <summary>
        /// Gets or sets the average team size
        /// </summary>
        public int AverageTeamSize { get; set; } = 7;
    }

    /// <summary>
    /// Represents a department in the company
    /// </summary>
    public class Department
    {
        /// <summary>
        /// Gets or sets the department name
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the department code
        /// </summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the typical roles in this department
        /// </summary>
        public List<string> TypicalRoles { get; set; } = new();

        /// <summary>
        /// Gets or sets the relative size weight (1-10)
        /// </summary>
        public int SizeWeight { get; set; } = 5;

        /// <summary>
        /// Gets or sets the cost center
        /// </summary>
        public string CostCenter { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents the executive structure
    /// </summary>
    public class ExecutiveStructure
    {
        /// <summary>
        /// Gets or sets the CEO information
        /// </summary>
        public ExecutiveRole CEO { get; set; } = new() { Title = "Chief Executive Officer", ReportsTo = null };

        /// <summary>
        /// Gets or sets the C-level executives
        /// </summary>
        public List<ExecutiveRole> CLevelExecutives { get; set; } = new();

        /// <summary>
        /// Gets or sets the VP level executives
        /// </summary>
        public List<ExecutiveRole> VicePresidents { get; set; } = new();
    }

    /// <summary>
    /// Represents an executive role
    /// </summary>
    public class ExecutiveRole
    {
        /// <summary>
        /// Gets or sets the title
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the department they oversee
        /// </summary>
        public string Department { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets who they report to (null for CEO)
        /// </summary>
        public string? ReportsTo { get; set; }
    }

    /// <summary>
    /// Predefined company structure templates
    /// </summary>
    public static class CompanyTemplates
    {
        /// <summary>
        /// Creates a technology company structure
        /// </summary>
        public static CompanyStructure TechCompany()
        {
            return new CompanyStructure
            {
                CompanyName = "TechCorp Solutions",
                RemoteWorkerPercentage = 0.4,
                ManagerPercentage = 0.12,
                AverageTeamSize = 6,
                Departments = new List<Department>
                {
                    new Department
                    {
                        Name = "Engineering",
                        Code = "ENG",
                        SizeWeight = 10,
                        CostCenter = "1000",
                        TypicalRoles = new List<string>
                        {
                            "Software Engineer",
                            "Senior Software Engineer",
                            "Staff Engineer",
                            "Principal Engineer",
                            "Engineering Manager",
                            "Director of Engineering",
                            "VP of Engineering"
                        }
                    },
                    new Department
                    {
                        Name = "Product",
                        Code = "PROD",
                        SizeWeight = 5,
                        CostCenter = "2000",
                        TypicalRoles = new List<string>
                        {
                            "Product Manager",
                            "Senior Product Manager",
                            "Principal Product Manager",
                            "Director of Product",
                            "VP of Product"
                        }
                    },
                    new Department
                    {
                        Name = "Design",
                        Code = "UX",
                        SizeWeight = 3,
                        CostCenter = "2100",
                        TypicalRoles = new List<string>
                        {
                            "UX Designer",
                            "Senior UX Designer",
                            "Lead Designer",
                            "Design Manager",
                            "Director of Design"
                        }
                    },
                    new Department
                    {
                        Name = "Sales",
                        Code = "SALES",
                        SizeWeight = 7,
                        CostCenter = "3000",
                        TypicalRoles = new List<string>
                        {
                            "Sales Representative",
                            "Account Executive",
                            "Senior Account Executive",
                            "Sales Manager",
                            "Regional Sales Director",
                            "VP of Sales"
                        }
                    },
                    new Department
                    {
                        Name = "Marketing",
                        Code = "MKTG",
                        SizeWeight = 4,
                        CostCenter = "3100",
                        TypicalRoles = new List<string>
                        {
                            "Marketing Specialist",
                            "Marketing Manager",
                            "Content Manager",
                            "SEO Specialist",
                            "Director of Marketing",
                            "VP of Marketing"
                        }
                    },
                    new Department
                    {
                        Name = "Customer Success",
                        Code = "CS",
                        SizeWeight = 5,
                        CostCenter = "3200",
                        TypicalRoles = new List<string>
                        {
                            "Customer Success Representative",
                            "Customer Success Manager",
                            "Senior Customer Success Manager",
                            "Director of Customer Success"
                        }
                    },
                    new Department
                    {
                        Name = "Human Resources",
                        Code = "HR",
                        SizeWeight = 2,
                        CostCenter = "4000",
                        TypicalRoles = new List<string>
                        {
                            "HR Coordinator",
                            "HR Generalist",
                            "HR Manager",
                            "Recruiter",
                            "Senior Recruiter",
                            "Director of HR",
                            "VP of People"
                        }
                    },
                    new Department
                    {
                        Name = "Finance",
                        Code = "FIN",
                        SizeWeight = 3,
                        CostCenter = "5000",
                        TypicalRoles = new List<string>
                        {
                            "Financial Analyst",
                            "Senior Financial Analyst",
                            "Accounting Manager",
                            "Controller",
                            "Director of Finance",
                            "CFO"
                        }
                    },
                    new Department
                    {
                        Name = "Operations",
                        Code = "OPS",
                        SizeWeight = 4,
                        CostCenter = "6000",
                        TypicalRoles = new List<string>
                        {
                            "Operations Analyst",
                            "Operations Manager",
                            "Senior Operations Manager",
                            "Director of Operations",
                            "VP of Operations"
                        }
                    },
                    new Department
                    {
                        Name = "Legal",
                        Code = "LEGAL",
                        SizeWeight = 1,
                        CostCenter = "7000",
                        TypicalRoles = new List<string>
                        {
                            "Legal Counsel",
                            "Senior Legal Counsel",
                            "General Counsel"
                        }
                    }
                },
                Executive = new ExecutiveStructure
                {
                    CEO = new ExecutiveRole { Title = "Chief Executive Officer", ReportsTo = null },
                    CLevelExecutives = new List<ExecutiveRole>
                    {
                        new ExecutiveRole { Title = "Chief Technology Officer", Department = "Engineering", ReportsTo = "Chief Executive Officer" },
                        new ExecutiveRole { Title = "Chief Product Officer", Department = "Product", ReportsTo = "Chief Executive Officer" },
                        new ExecutiveRole { Title = "Chief Revenue Officer", Department = "Sales", ReportsTo = "Chief Executive Officer" },
                        new ExecutiveRole { Title = "Chief Financial Officer", Department = "Finance", ReportsTo = "Chief Executive Officer" },
                        new ExecutiveRole { Title = "Chief People Officer", Department = "Human Resources", ReportsTo = "Chief Executive Officer" },
                        new ExecutiveRole { Title = "Chief Operating Officer", Department = "Operations", ReportsTo = "Chief Executive Officer" }
                    },
                    VicePresidents = new List<ExecutiveRole>
                    {
                        new ExecutiveRole { Title = "VP of Engineering", Department = "Engineering", ReportsTo = "Chief Technology Officer" },
                        new ExecutiveRole { Title = "VP of Product", Department = "Product", ReportsTo = "Chief Product Officer" },
                        new ExecutiveRole { Title = "VP of Sales", Department = "Sales", ReportsTo = "Chief Revenue Officer" },
                        new ExecutiveRole { Title = "VP of Marketing", Department = "Marketing", ReportsTo = "Chief Revenue Officer" },
                        new ExecutiveRole { Title = "VP of Customer Success", Department = "Customer Success", ReportsTo = "Chief Revenue Officer" },
                        new ExecutiveRole { Title = "VP of People", Department = "Human Resources", ReportsTo = "Chief People Officer" },
                        new ExecutiveRole { Title = "VP of Operations", Department = "Operations", ReportsTo = "Chief Operating Officer" }
                    }
                }
            };
        }

        /// <summary>
        /// Creates a financial services company structure
        /// </summary>
        public static CompanyStructure FinancialServices()
        {
            return new CompanyStructure
            {
                CompanyName = "Global Financial Group",
                RemoteWorkerPercentage = 0.2,
                ManagerPercentage = 0.18,
                AverageTeamSize = 8,
                Departments = new List<Department>
                {
                    new Department
                    {
                        Name = "Investment Banking",
                        Code = "IB",
                        SizeWeight = 8,
                        CostCenter = "1000",
                        TypicalRoles = new List<string>
                        {
                            "Analyst",
                            "Associate",
                            "Vice President",
                            "Director",
                            "Managing Director"
                        }
                    },
                    new Department
                    {
                        Name = "Trading",
                        Code = "TRADE",
                        SizeWeight = 7,
                        CostCenter = "2000",
                        TypicalRoles = new List<string>
                        {
                            "Junior Trader",
                            "Trader",
                            "Senior Trader",
                            "Head Trader",
                            "Trading Director"
                        }
                    },
                    new Department
                    {
                        Name = "Risk Management",
                        Code = "RISK",
                        SizeWeight = 6,
                        CostCenter = "3000",
                        TypicalRoles = new List<string>
                        {
                            "Risk Analyst",
                            "Senior Risk Analyst",
                            "Risk Manager",
                            "Senior Risk Manager",
                            "Chief Risk Officer"
                        }
                    },
                    new Department
                    {
                        Name = "Compliance",
                        Code = "COMP",
                        SizeWeight = 4,
                        CostCenter = "4000",
                        TypicalRoles = new List<string>
                        {
                            "Compliance Analyst",
                            "Compliance Officer",
                            "Senior Compliance Officer",
                            "Compliance Manager",
                            "Chief Compliance Officer"
                        }
                    },
                    new Department
                    {
                        Name = "Technology",
                        Code = "TECH",
                        SizeWeight = 9,
                        CostCenter = "5000",
                        TypicalRoles = new List<string>
                        {
                            "Developer",
                            "Senior Developer",
                            "Tech Lead",
                            "Engineering Manager",
                            "Director of Technology"
                        }
                    },
                    new Department
                    {
                        Name = "Operations",
                        Code = "OPS",
                        SizeWeight = 10,
                        CostCenter = "6000",
                        TypicalRoles = new List<string>
                        {
                            "Operations Analyst",
                            "Senior Operations Analyst",
                            "Operations Manager",
                            "VP of Operations"
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Creates a healthcare company structure
        /// </summary>
        public static CompanyStructure Healthcare()
        {
            return new CompanyStructure
            {
                CompanyName = "MedCare Health Systems",
                RemoteWorkerPercentage = 0.15,
                ManagerPercentage = 0.20,
                AverageTeamSize = 10,
                Departments = new List<Department>
                {
                    new Department
                    {
                        Name = "Clinical Services",
                        Code = "CLIN",
                        SizeWeight = 10,
                        CostCenter = "1000",
                        TypicalRoles = new List<string>
                        {
                            "Registered Nurse",
                            "Nurse Practitioner",
                            "Physician",
                            "Medical Director",
                            "Chief Medical Officer"
                        }
                    },
                    new Department
                    {
                        Name = "Administration",
                        Code = "ADMIN",
                        SizeWeight = 5,
                        CostCenter = "2000",
                        TypicalRoles = new List<string>
                        {
                            "Administrative Assistant",
                            "Office Manager",
                            "Department Administrator",
                            "VP of Administration"
                        }
                    },
                    new Department
                    {
                        Name = "Information Technology",
                        Code = "IT",
                        SizeWeight = 4,
                        CostCenter = "3000",
                        TypicalRoles = new List<string>
                        {
                            "IT Support Specialist",
                            "Systems Administrator",
                            "IT Manager",
                            "Director of IT",
                            "Chief Information Officer"
                        }
                    },
                    new Department
                    {
                        Name = "Patient Services",
                        Code = "PS",
                        SizeWeight = 7,
                        CostCenter = "4000",
                        TypicalRoles = new List<string>
                        {
                            "Patient Care Coordinator",
                            "Patient Services Representative",
                            "Patient Services Manager",
                            "Director of Patient Services"
                        }
                    },
                    new Department
                    {
                        Name = "Quality Assurance",
                        Code = "QA",
                        SizeWeight = 3,
                        CostCenter = "5000",
                        TypicalRoles = new List<string>
                        {
                            "QA Analyst",
                            "QA Specialist",
                            "QA Manager",
                            "Director of Quality"
                        }
                    }
                }
            };
        }
    }
}