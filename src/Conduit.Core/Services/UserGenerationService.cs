using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Conduit.Core.Generation;

namespace Conduit.Core.Services
{
    /// <summary>
    /// Service for generating test users with realistic company structures
    /// </summary>
    public class UserGenerationService
    {
        private Random _random = null!;
        private CompanyStructure _companyStructure = null!;
        private int _employeeCounter;

        // Reservation sets for the current generation run. Both are seeded with whatever
        // the caller passes in (typically the live tenant's existing usernames/emails),
        // then every GenerateUsername / GenerateEmail call appends a numeric suffix until
        // it finds a free slot — guaranteeing no in-run or cross-run collisions.
        private HashSet<string> _usedUsernames = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _usedEmails = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Generates users based on the provided options. Pass <paramref name="existingUsernames"/>
        /// and <paramref name="existingEmails"/> from the live tenant so the generator avoids
        /// re-using names that already exist in the database.
        /// </summary>
        public UserGenerationResult GenerateUsers(
            UserGenerationOptions options,
            IEnumerable<string>? existingUsernames = null,
            IEnumerable<string>? existingEmails = null)
        {
            // Initialize random with seed if provided
            _random = options.RandomSeed.HasValue ? new Random(options.RandomSeed.Value) : new Random();
            _employeeCounter = 1000;

            _usedUsernames = new HashSet<string>(existingUsernames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            _usedEmails    = new HashSet<string>(existingEmails    ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            // Get company structure
            _companyStructure = GetCompanyStructure(options);

            var result = new UserGenerationResult();
            var allUsers = new List<GeneratedUser>();

            // Generate executive team first
            var executives = GenerateExecutives();
            allUsers.AddRange(executives);

            // Generate department heads and teams
            var departmentUsers = GenerateDepartmentUsers(options.NumberOfUsers - executives.Count);
            allUsers.AddRange(departmentUsers);

            // Assign managers and create hierarchy
            AssignManagers(allUsers);

            // Set active status
            SetActiveStatus(allUsers, options.ActiveUserPercentage);

            // Generate additional attributes
            if (options.GeneratePhoneNumbers)
                GeneratePhoneNumbers(allUsers);

            if (options.AssignSkills)
                AssignSkills(allUsers);

            if (options.AssignCertifications)
                AssignCertifications(allUsers);

            // Handle specific emails if provided
            if (options.SpecificEmails.Any())
            {
                ApplySpecificEmails(allUsers, options.SpecificEmails);
            }

            // Create groups
            var groups = new List<GeneratedGroup>();
            if (options.CreateDepartmentGroups)
                groups.AddRange(CreateDepartmentGroups(allUsers));

            if (options.CreateLocationGroups)
                groups.AddRange(CreateLocationGroups(allUsers));

            if (options.CreateRoleGroups)
                groups.AddRange(CreateRoleGroups(allUsers));

            // Assign users to groups
            AssignUsersToGroups(allUsers, groups);

            // Create organizational hierarchy
            var hierarchy = CreateOrganizationalHierarchy(allUsers);

            // Generate statistics
            var statistics = GenerateStatistics(allUsers, groups);

            result.Users = allUsers;
            result.Groups = groups;
            result.Hierarchy = hierarchy;
            result.Statistics = statistics;

            return result;
        }

        /// <summary>
        /// Gets the company structure based on options
        /// </summary>
        private CompanyStructure GetCompanyStructure(UserGenerationOptions options)
        {
            if (options.CustomStructure != null)
                return options.CustomStructure;

            return options.CompanyTemplate switch
            {
                "FinancialServices" => CompanyTemplates.FinancialServices(),
                "Healthcare" => CompanyTemplates.Healthcare(),
                _ => CompanyTemplates.TechCompany()
            };
        }

        /// <summary>
        /// Generates executive team members
        /// </summary>
        private List<GeneratedUser> GenerateExecutives()
        {
            var executives = new List<GeneratedUser>();

            // Generate CEO
            var ceo = GenerateUser();
            ceo.Title = _companyStructure.Executive.CEO.Title;
            ceo.Department = "Executive";
            ceo.EmployeeNumber = "EMP" + (_employeeCounter++).ToString("D6");
            executives.Add(ceo);

            // Generate C-level executives
            foreach (var exec in _companyStructure.Executive.CLevelExecutives)
            {
                var user = GenerateUser();
                user.Title = exec.Title;
                user.Department = exec.Department ?? "Executive";
                user.EmployeeNumber = "EMP" + (_employeeCounter++).ToString("D6");
                user.ManagerId = ceo.Id; // Reports to CEO
                executives.Add(user);
            }

            // Generate VPs
            foreach (var vp in _companyStructure.Executive.VicePresidents)
            {
                var user = GenerateUser();
                user.Title = vp.Title;
                user.Department = vp.Department ?? "Executive";
                user.EmployeeNumber = "EMP" + (_employeeCounter++).ToString("D6");
                
                // Find their manager (C-level exec)
                var manager = executives.FirstOrDefault(e => e.Title == vp.ReportsTo);
                if (manager != null)
                    user.ManagerId = manager.Id;
                
                executives.Add(user);
            }

            return executives;
        }

        /// <summary>
        /// Generates department users
        /// </summary>
        private List<GeneratedUser> GenerateDepartmentUsers(int count)
        {
            var users = new List<GeneratedUser>();
            var totalWeight = _companyStructure.Departments.Sum(d => d.SizeWeight);

            foreach (var dept in _companyStructure.Departments)
            {
                var deptUserCount = (int)Math.Round(count * (double)dept.SizeWeight / totalWeight);
                
                for (int i = 0; i < deptUserCount; i++)
                {
                    var user = GenerateUser();
                    user.Department = dept.Name;
                    user.CostCenter = dept.CostCenter;
                    user.EmployeeNumber = "EMP" + (_employeeCounter++).ToString("D6");
                    
                    // Assign a role from typical roles
                    if (dept.TypicalRoles.Any())
                    {
                        user.Title = dept.TypicalRoles[_random.Next(dept.TypicalRoles.Count)];
                    }
                    
                    // Assign location
                    user.Location = AssignLocation();
                    
                    // Set hire date
                    user.HireDate = GenerateHireDate();
                    
                    users.Add(user);
                }
            }

            return users;
        }

        /// <summary>
        /// Generates a single user
        /// </summary>
        private GeneratedUser GenerateUser()
        {
            var firstName = UserGenerationData.FirstNames[_random.Next(UserGenerationData.FirstNames.Length)];
            var lastName = UserGenerationData.LastNames[_random.Next(UserGenerationData.LastNames.Length)];
            
            var user = new GeneratedUser
            {
                FirstName = firstName,
                LastName = lastName,
                DisplayName = $"{firstName} {lastName}",
                Active = true
            };

            // Generate username and email
            user.UserName = GenerateUsername(firstName, lastName);
            user.Email = GenerateEmail(firstName, lastName);

            return user;
        }

        /// <summary>
        /// Generates a username that has not yet been used in this run AND that doesn't
        /// already exist in the target tenant (when the caller seeded _usedUsernames).
        /// Suffixes <c>1, 2, 3…</c> until a free slot is found.
        /// </summary>
        private string GenerateUsername(string firstName, string lastName)
        {
            var baseUsername = $"{firstName.ToLower()}.{lastName.ToLower()}";
            var candidate = baseUsername;
            var counter = 1;
            while (!_usedUsernames.Add(candidate))
            {
                candidate = $"{baseUsername}{counter++}";
            }
            return candidate;
        }

        /// <summary>
        /// Generates an email address that hasn't been used in this run AND isn't already
        /// in the tenant. Walks numeric suffixes on the local-part if the base is taken.
        /// </summary>
        private string GenerateEmail(string firstName, string lastName)
        {
            var domain = UserGenerationData.EmailDomains[_random.Next(UserGenerationData.EmailDomains.Length)];
            var baseLocal = $"{firstName.ToLower()}.{lastName.ToLower()}";
            var candidate = $"{baseLocal}@{domain}";
            var counter = 1;
            while (!_usedEmails.Add(candidate))
            {
                candidate = $"{baseLocal}{counter++}@{domain}";
            }
            return candidate;
        }

        /// <summary>
        /// Assigns a location to a user
        /// </summary>
        private Location AssignLocation()
        {
            // Check if user should be remote
            if (_random.NextDouble() < _companyStructure.RemoteWorkerPercentage)
            {
                return UserGenerationData.Locations.First(l => l.IsRemote);
            }

            // Assign office location
            var officeLocations = UserGenerationData.Locations.Where(l => !l.IsRemote).ToList();
            return officeLocations[_random.Next(officeLocations.Count)];
        }

        /// <summary>
        /// Generates a hire date
        /// </summary>
        private DateTime GenerateHireDate()
        {
            var start = DateTime.Now.AddYears(-10);
            var range = (DateTime.Now - start).Days;
            return start.AddDays(_random.Next(range));
        }

        /// <summary>
        /// Assigns managers to users
        /// </summary>
        private void AssignManagers(List<GeneratedUser> users)
        {
            var usersByDept = users.GroupBy(u => u.Department).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var dept in usersByDept)
            {
                if (dept.Key == "Executive") continue;

                var deptUsers = dept.Value;
                var managers = new List<GeneratedUser>();

                // Determine number of managers needed
                var managerCount = Math.Max(1, (int)(deptUsers.Count * _companyStructure.ManagerPercentage));

                // Select managers based on title seniority
                var seniorTitles = new[] { "Director", "Manager", "VP", "Senior", "Lead", "Principal" };
                managers = deptUsers
                    .Where(u => seniorTitles.Any(t => u.Title.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    .Take(managerCount)
                    .ToList();

                // If not enough senior people, randomly select
                if (managers.Count < managerCount)
                {
                    var remaining = deptUsers.Except(managers).ToList();
                    var needed = managerCount - managers.Count;
                    for (int i = 0; i < needed && remaining.Count > 0; i++)
                    {
                        var idx = _random.Next(remaining.Count);
                        managers.Add(remaining[idx]);
                        remaining.RemoveAt(idx);
                    }
                }

                // Assign reports
                var nonManagers = deptUsers.Except(managers).ToList();
                foreach (var employee in nonManagers)
                {
                    if (managers.Any())
                    {
                        employee.ManagerId = managers[_random.Next(managers.Count)].Id;
                    }
                }

                // Assign managers to department heads or VPs
                var deptHead = users.FirstOrDefault(u => 
                    u.Title.Contains("VP") && u.Department == dept.Key ||
                    u.Title.Contains("Director") && u.Department == dept.Key);

                if (deptHead != null)
                {
                    foreach (var manager in managers.Where(m => m.Id != deptHead.Id))
                    {
                        manager.ManagerId = deptHead.Id;
                    }
                }
            }
        }

        /// <summary>
        /// Sets active status for users
        /// </summary>
        private void SetActiveStatus(List<GeneratedUser> users, double activePercentage)
        {
            var inactiveCount = (int)(users.Count * (1 - activePercentage));
            var indices = Enumerable.Range(0, users.Count).OrderBy(x => _random.Next()).Take(inactiveCount);
            
            foreach (var idx in indices)
            {
                users[idx].Active = false;
            }
        }

        /// <summary>
        /// Generates phone numbers for users
        /// </summary>
        private void GeneratePhoneNumbers(List<GeneratedUser> users)
        {
            foreach (var user in users)
            {
                if (user.Location != null && !user.Location.IsRemote)
                {
                    var prefix = UserGenerationData.PhonePrefixes.GetValueOrDefault(user.Location.Country, "+1");
                    var number = GeneratePhoneNumber();
                    user.PhoneNumber = $"{prefix} {number}";
                }
            }
        }

        /// <summary>
        /// Generates a phone number
        /// </summary>
        private string GeneratePhoneNumber()
        {
            var areaCode = _random.Next(200, 999);
            var prefix = _random.Next(200, 999);
            var suffix = _random.Next(1000, 9999);
            return $"({areaCode}) {prefix}-{suffix}";
        }

        /// <summary>
        /// Assigns skills to users
        /// </summary>
        private void AssignSkills(List<GeneratedUser> users)
        {
            foreach (var user in users)
            {
                if (UserGenerationData.SkillsByDepartment.TryGetValue(user.Department, out var deptSkills))
                {
                    var skillCount = _random.Next(3, 8);
                    var selectedSkills = deptSkills.OrderBy(x => _random.Next()).Take(skillCount).ToList();
                    user.Skills = selectedSkills;
                }
            }
        }

        /// <summary>
        /// Assigns certifications to users
        /// </summary>
        private void AssignCertifications(List<GeneratedUser> users)
        {
            foreach (var user in users)
            {
                // Only some users have certifications
                if (_random.NextDouble() > 0.3) continue;

                if (UserGenerationData.CertificationsByDepartment.TryGetValue(user.Department, out var deptCerts))
                {
                    var certCount = _random.Next(1, 3);
                    var selectedCerts = deptCerts.OrderBy(x => _random.Next()).Take(certCount).ToList();
                    user.Certifications = selectedCerts;
                }
            }
        }

        /// <summary>
        /// Applies specific emails to users
        /// </summary>
        private void ApplySpecificEmails(List<GeneratedUser> users, List<string> specificEmails)
        {
            for (int i = 0; i < specificEmails.Count && i < users.Count; i++)
            {
                users[i].Email = specificEmails[i];
                
                // Update username to match email
                var emailParts = specificEmails[i].Split('@');
                if (emailParts.Length > 0)
                {
                    users[i].UserName = emailParts[0];
                }
            }
        }

        /// <summary>
        /// Creates department groups
        /// </summary>
        private List<GeneratedGroup> CreateDepartmentGroups(List<GeneratedUser> users)
        {
            var groups = new List<GeneratedGroup>();
            var departments = users.GroupBy(u => u.Department);

            foreach (var dept in departments)
            {
                var group = new GeneratedGroup
                {
                    Name = $"{dept.Key} Team",
                    Description = $"All members of the {dept.Key} department",
                    Type = "Department",
                    MemberIds = dept.Select(u => u.Id).ToList()
                };
                groups.Add(group);
            }

            return groups;
        }

        /// <summary>
        /// Creates location groups
        /// </summary>
        private List<GeneratedGroup> CreateLocationGroups(List<GeneratedUser> users)
        {
            var groups = new List<GeneratedGroup>();
            var locations = users.Where(u => u.Location != null).GroupBy(u => u.Location!.FormattedAddress);

            foreach (var loc in locations)
            {
                var group = new GeneratedGroup
                {
                    Name = $"{loc.Key} Office",
                    Description = $"All employees in {loc.Key}",
                    Type = "Location",
                    MemberIds = loc.Select(u => u.Id).ToList()
                };
                groups.Add(group);
            }

            return groups;
        }

        /// <summary>
        /// Creates role-based groups
        /// </summary>
        private List<GeneratedGroup> CreateRoleGroups(List<GeneratedUser> users)
        {
            var groups = new List<GeneratedGroup>();

            // Managers group
            var managers = users.Where(u => users.Any(e => e.ManagerId == u.Id)).ToList();
            if (managers.Any())
            {
                groups.Add(new GeneratedGroup
                {
                    Name = "Managers",
                    Description = "All people managers",
                    Type = "Role",
                    MemberIds = managers.Select(u => u.Id).ToList()
                });
            }

            // Executives group
            var executives = users.Where(u => u.Department == "Executive" || 
                u.Title.Contains("VP") || u.Title.Contains("Chief")).ToList();
            if (executives.Any())
            {
                groups.Add(new GeneratedGroup
                {
                    Name = "Leadership Team",
                    Description = "Executive leadership team",
                    Type = "Role",
                    MemberIds = executives.Select(u => u.Id).ToList()
                });
            }

            // Remote workers group
            var remoteWorkers = users.Where(u => u.Location?.IsRemote == true).ToList();
            if (remoteWorkers.Any())
            {
                groups.Add(new GeneratedGroup
                {
                    Name = "Remote Workers",
                    Description = "All remote employees",
                    Type = "Role",
                    MemberIds = remoteWorkers.Select(u => u.Id).ToList()
                });
            }

            return groups;
        }

        /// <summary>
        /// Assigns users to their groups
        /// </summary>
        private void AssignUsersToGroups(List<GeneratedUser> users, List<GeneratedGroup> groups)
        {
            foreach (var group in groups)
            {
                foreach (var memberId in group.MemberIds)
                {
                    var user = users.FirstOrDefault(u => u.Id == memberId);
                    if (user != null)
                    {
                        user.Groups.Add(group.Id);
                    }
                }
            }
        }

        /// <summary>
        /// Creates organizational hierarchy
        /// </summary>
        private OrganizationalHierarchy CreateOrganizationalHierarchy(List<GeneratedUser> users)
        {
            var hierarchy = new OrganizationalHierarchy();

            // Find CEO
            var ceo = users.FirstOrDefault(u => u.Title.Contains("Chief Executive Officer"));
            if (ceo != null)
            {
                hierarchy.CeoId = ceo.Id;
            }

            // Find executives
            hierarchy.ExecutiveIds = users
                .Where(u => u.Department == "Executive" || u.Title.Contains("Chief") || u.Title.Contains("VP"))
                .Select(u => u.Id)
                .ToList();

            // Build management structure
            var managementStructure = new Dictionary<string, List<string>>();
            foreach (var user in users.Where(u => !string.IsNullOrEmpty(u.ManagerId)))
            {
                if (!managementStructure.ContainsKey(user.ManagerId!))
                {
                    managementStructure[user.ManagerId!] = new List<string>();
                }
                managementStructure[user.ManagerId!].Add(user.Id);
            }
            hierarchy.ManagementStructure = managementStructure;

            // Find department heads
            var deptHeads = new Dictionary<string, string>();
            foreach (var dept in _companyStructure.Departments)
            {
                var head = users
                    .Where(u => u.Department == dept.Name)
                    .OrderByDescending(u => u.Title.Contains("VP") ? 3 : u.Title.Contains("Director") ? 2 : 1)
                    .FirstOrDefault();
                
                if (head != null)
                {
                    deptHeads[dept.Name] = head.Id;
                }
            }
            hierarchy.DepartmentHeads = deptHeads;

            return hierarchy;
        }

        /// <summary>
        /// Generates statistics
        /// </summary>
        private GenerationStatistics GenerateStatistics(List<GeneratedUser> users, List<GeneratedGroup> groups)
        {
            var stats = new GenerationStatistics
            {
                TotalUsers = users.Count,
                TotalGroups = groups.Count,
                ActiveUserCount = users.Count(u => u.Active),
                RemoteWorkerCount = users.Count(u => u.Location?.IsRemote == true),
                ManagerCount = users.Count(u => users.Any(e => e.ManagerId == u.Id))
            };

            // Users by department
            stats.UsersByDepartment = users
                .GroupBy(u => u.Department)
                .ToDictionary(g => g.Key, g => g.Count());

            // Users by location
            stats.UsersByLocation = users
                .Where(u => u.Location != null)
                .GroupBy(u => u.Location!.FormattedAddress)
                .ToDictionary(g => g.Key, g => g.Count());

            return stats;
        }
    }
}