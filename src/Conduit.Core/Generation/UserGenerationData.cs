using System;
using System.Collections.Generic;

namespace Conduit.Core.Generation
{
    /// <summary>
    /// Static data for user generation
    /// </summary>
    public static class UserGenerationData
    {
        /// <summary>
        /// Common first names
        /// </summary>
        public static readonly string[] FirstNames = new[]
        {
            "James", "Mary", "Robert", "Patricia", "John", "Jennifer", "Michael", "Linda",
            "William", "Elizabeth", "David", "Barbara", "Richard", "Susan", "Joseph", "Jessica",
            "Thomas", "Sarah", "Christopher", "Karen", "Charles", "Lisa", "Daniel", "Nancy",
            "Matthew", "Betty", "Anthony", "Helen", "Mark", "Sandra", "Donald", "Donna",
            "Kenneth", "Carol", "Steven", "Ruth", "Paul", "Sharon", "Andrew", "Michelle",
            "Joshua", "Laura", "Kevin", "Emily", "Brian", "Kimberly", "George", "Deborah",
            "Timothy", "Dorothy", "Ronald", "Lisa", "Edward", "Amy", "Jason", "Angela",
            "Jeffrey", "Ashley", "Ryan", "Brenda", "Jacob", "Emma", "Gary", "Olivia",
            "Nicholas", "Sophia", "Eric", "Isabella", "Jonathan", "Charlotte", "Stephen", "Amelia",
            "Larry", "Mia", "Justin", "Harper", "Scott", "Evelyn", "Brandon", "Abigail",
            "Benjamin", "Emily", "Samuel", "Ella", "Gregory", "Madison", "Alexander", "Scarlett",
            "Frank", "Victoria", "Patrick", "Aria", "Raymond", "Grace", "Kyle", "Chloe",
            "Aaron", "Camila", "Jose", "Penelope", "Nathan", "Riley", "Adam", "Layla",
            "Noah", "Lillian", "Henry", "Nora", "Zachary", "Zoey", "Douglas", "Mila",
            "Carlos", "Maria", "Luis", "Ana", "Juan", "Sofia", "Pedro", "Elena",
            "Miguel", "Isabella", "Jorge", "Lucia", "Roberto", "Carmen", "Eduardo", "Rosa",
            "Raj", "Priya", "Amit", "Anita", "Vikram", "Sunita", "Arjun", "Kavita",
            "Wei", "Li", "Jun", "Mei", "Hiroshi", "Yuki", "Takeshi", "Sakura",
            "Pierre", "Marie", "Jean", "Sophie", "Klaus", "Anna", "Hans", "Emma"
        };

        /// <summary>
        /// Common last names
        /// </summary>
        public static readonly string[] LastNames = new[]
        {
            "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis",
            "Rodriguez", "Martinez", "Hernandez", "Lopez", "Gonzalez", "Wilson", "Anderson", "Thomas",
            "Taylor", "Moore", "Jackson", "Martin", "Lee", "Perez", "Thompson", "White",
            "Harris", "Sanchez", "Clark", "Ramirez", "Lewis", "Robinson", "Walker", "Young",
            "Allen", "King", "Wright", "Scott", "Torres", "Nguyen", "Hill", "Flores",
            "Green", "Adams", "Nelson", "Baker", "Hall", "Rivera", "Campbell", "Mitchell",
            "Carter", "Roberts", "Gomez", "Phillips", "Evans", "Turner", "Diaz", "Parker",
            "Cruz", "Edwards", "Collins", "Reyes", "Stewart", "Morris", "Morales", "Murphy",
            "Cook", "Rogers", "Gutierrez", "Ortiz", "Morgan", "Cooper", "Peterson", "Bailey",
            "Reed", "Kelly", "Howard", "Ramos", "Kim", "Cox", "Ward", "Richardson",
            "Watson", "Brooks", "Chavez", "Wood", "James", "Bennett", "Gray", "Mendoza",
            "Ruiz", "Hughes", "Price", "Alvarez", "Castillo", "Sanders", "Patel", "Myers",
            "Long", "Ross", "Foster", "Jimenez", "Powell", "Jenkins", "Perry", "Russell",
            "Sullivan", "Bell", "Coleman", "Butler", "Henderson", "Barnes", "Gonzales", "Fisher",
            "Vasquez", "Simmons", "Romero", "Jordan", "Patterson", "Alexander", "Hamilton", "Graham",
            "Reynolds", "Griffin", "Wallace", "Moreno", "West", "Cole", "Hayes", "Bryant",
            "Wang", "Li", "Zhang", "Chen", "Liu", "Yang", "Huang", "Wu",
            "Tanaka", "Suzuki", "Takahashi", "Watanabe", "Yamamoto", "Nakamura", "Kobayashi", "Saito",
            "Patel", "Sharma", "Kumar", "Singh", "Gupta", "Mehta", "Shah", "Desai",
            "Mueller", "Schmidt", "Schneider", "Fischer", "Weber", "Meyer", "Wagner", "Becker",
            "Dubois", "Moreau", "Laurent", "Simon", "Michel", "Lefevre", "Leroy", "Roux"
        };

        /// <summary>
        /// Office locations
        /// </summary>
        public static readonly Location[] Locations = new[]
        {
            new Location { City = "New York", State = "NY", Country = "USA", Timezone = "America/New_York", IsHeadquarters = true },
            new Location { City = "San Francisco", State = "CA", Country = "USA", Timezone = "America/Los_Angeles" },
            new Location { City = "Chicago", State = "IL", Country = "USA", Timezone = "America/Chicago" },
            new Location { City = "Boston", State = "MA", Country = "USA", Timezone = "America/New_York" },
            new Location { City = "Seattle", State = "WA", Country = "USA", Timezone = "America/Los_Angeles" },
            new Location { City = "Austin", State = "TX", Country = "USA", Timezone = "America/Chicago" },
            new Location { City = "Denver", State = "CO", Country = "USA", Timezone = "America/Denver" },
            new Location { City = "Atlanta", State = "GA", Country = "USA", Timezone = "America/New_York" },
            new Location { City = "Miami", State = "FL", Country = "USA", Timezone = "America/New_York" },
            new Location { City = "Los Angeles", State = "CA", Country = "USA", Timezone = "America/Los_Angeles" },
            new Location { City = "London", State = "", Country = "UK", Timezone = "Europe/London" },
            new Location { City = "Paris", State = "", Country = "France", Timezone = "Europe/Paris" },
            new Location { City = "Berlin", State = "", Country = "Germany", Timezone = "Europe/Berlin" },
            new Location { City = "Tokyo", State = "", Country = "Japan", Timezone = "Asia/Tokyo" },
            new Location { City = "Singapore", State = "", Country = "Singapore", Timezone = "Asia/Singapore" },
            new Location { City = "Sydney", State = "NSW", Country = "Australia", Timezone = "Australia/Sydney" },
            new Location { City = "Toronto", State = "ON", Country = "Canada", Timezone = "America/Toronto" },
            new Location { City = "Mumbai", State = "MH", Country = "India", Timezone = "Asia/Kolkata" },
            new Location { City = "Remote", State = "", Country = "", Timezone = "", IsRemote = true }
        };

        /// <summary>
        /// Phone number prefixes by country
        /// </summary>
        public static readonly Dictionary<string, string> PhonePrefixes = new()
        {
            { "USA", "+1" },
            { "UK", "+44" },
            { "France", "+33" },
            { "Germany", "+49" },
            { "Japan", "+81" },
            { "Singapore", "+65" },
            { "Australia", "+61" },
            { "Canada", "+1" },
            { "India", "+91" }
        };

        /// <summary>
        /// Common email domains
        /// </summary>
        public static readonly string[] EmailDomains = new[]
        {
            "example.com",
            "company.com",
            "corporate.com",
            "enterprise.com",
            "business.com"
        };

        /// <summary>
        /// Skills by department
        /// </summary>
        public static readonly Dictionary<string, List<string>> SkillsByDepartment = new()
        {
            {
                "Engineering",
                new List<string>
                {
                    "Java", "Python", "C#", "JavaScript", "TypeScript", "React", "Angular", "Vue.js",
                    "Node.js", "AWS", "Azure", "Docker", "Kubernetes", "Git", "Agile", "Scrum",
                    "SQL", "NoSQL", "MongoDB", "PostgreSQL", "Redis", "CI/CD", "Jenkins", "DevOps"
                }
            },
            {
                "Product",
                new List<string>
                {
                    "Product Strategy", "Roadmap Planning", "User Research", "A/B Testing", "Analytics",
                    "Jira", "Confluence", "Market Analysis", "Competitive Analysis", "PRD Writing",
                    "Stakeholder Management", "Agile", "Scrum", "User Stories", "Feature Prioritization"
                }
            },
            {
                "Sales",
                new List<string>
                {
                    "Salesforce", "CRM", "Lead Generation", "Cold Calling", "Negotiation", "Closing",
                    "Pipeline Management", "Account Management", "Relationship Building", "Presentation Skills",
                    "Sales Strategy", "Territory Management", "Forecasting", "B2B Sales", "B2C Sales"
                }
            },
            {
                "Marketing",
                new List<string>
                {
                    "SEO", "SEM", "Content Marketing", "Social Media", "Email Marketing", "Google Analytics",
                    "HubSpot", "Marketing Automation", "Brand Management", "Campaign Management",
                    "Copywriting", "Adobe Creative Suite", "PPC", "Lead Generation", "Marketing Strategy"
                }
            },
            {
                "HR",
                new List<string>
                {
                    "Recruiting", "Talent Acquisition", "Employee Relations", "Performance Management",
                    "Compensation & Benefits", "HRIS", "Workday", "ADP", "Labor Law", "Training & Development",
                    "Organizational Development", "Change Management", "Diversity & Inclusion", "Employee Engagement"
                }
            },
            {
                "Finance",
                new List<string>
                {
                    "Financial Analysis", "Budgeting", "Forecasting", "Financial Reporting", "Excel",
                    "QuickBooks", "SAP", "Oracle", "GAAP", "Tax Compliance", "Audit", "Risk Management",
                    "Treasury", "FP&A", "Cost Accounting", "Financial Modeling"
                }
            }
        };

        /// <summary>
        /// Common certifications by department
        /// </summary>
        public static readonly Dictionary<string, List<string>> CertificationsByDepartment = new()
        {
            {
                "Engineering",
                new List<string>
                {
                    "AWS Certified Solutions Architect", "Azure Solutions Architect", "Google Cloud Professional",
                    "Certified Kubernetes Administrator", "Cisco CCNA", "CompTIA Security+", "PMP",
                    "Scrum Master Certification", "Oracle Certified Java Developer"
                }
            },
            {
                "Product",
                new List<string>
                {
                    "Certified Product Manager", "Pragmatic Marketing Certified", "Scrum Product Owner",
                    "Google Analytics Certified", "HubSpot Content Marketing", "Certified Agile Product Manager"
                }
            },
            {
                "Sales",
                new List<string>
                {
                    "Salesforce Certified Administrator", "HubSpot Sales Software Certified",
                    "Certified Sales Professional", "Miller Heiman Strategic Selling", "SPIN Selling Certified"
                }
            },
            {
                "Finance",
                new List<string>
                {
                    "CPA", "CFA", "FRM", "CMA", "CIA", "ACCA", "QuickBooks ProAdvisor", "Excel Specialist"
                }
            },
            {
                "HR",
                new List<string>
                {
                    "SHRM-CP", "SHRM-SCP", "PHR", "SPHR", "CIPD", "Workday Certified", "ADP Certified"
                }
            }
        };
    }

    /// <summary>
    /// Represents a location
    /// </summary>
    public class Location
    {
        /// <summary>
        /// Gets or sets the city
        /// </summary>
        public string City { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the state/province
        /// </summary>
        public string State { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the country
        /// </summary>
        public string Country { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the timezone
        /// </summary>
        public string Timezone { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether this is the headquarters
        /// </summary>
        public bool IsHeadquarters { get; set; }

        /// <summary>
        /// Gets or sets whether this is a remote location
        /// </summary>
        public bool IsRemote { get; set; }

        /// <summary>
        /// Gets the formatted address
        /// </summary>
        public string FormattedAddress
        {
            get
            {
                if (IsRemote) return "Remote";
                var parts = new List<string> { City };
                if (!string.IsNullOrEmpty(State)) parts.Add(State);
                if (!string.IsNullOrEmpty(Country)) parts.Add(Country);
                return string.Join(", ", parts);
            }
        }
    }
}