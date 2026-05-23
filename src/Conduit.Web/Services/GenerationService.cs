using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Conduit.Core.Generation;
using Conduit.Core.Models;
using Conduit.Core.Services;
using Conduit.DataAccess.Repositories;

namespace Conduit.Web.Services
{
    /// <summary>
    /// Service for managing background generation of users and groups
    /// </summary>
    public class GenerationService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly UserGenerationService _userGenerationService;
        private readonly ApplicationLogService _logger;
        
        // Track generation status
        private readonly ConcurrentDictionary<string, GenerationStatus> _activeGenerations = new();
        
        public GenerationService(IServiceProvider serviceProvider, ApplicationLogService logger)
        {
            _serviceProvider = serviceProvider;
            _userGenerationService = new UserGenerationService();
            _logger = logger;
        }
        
        /// <summary>
        /// Start generating users in the background
        /// </summary>
        public string StartUserGeneration(UserGenerationOptions options)
        {
            var generationId = Guid.NewGuid().ToString();
            var status = new GenerationStatus
            {
                Id = generationId,
                Type = "Users",
                TotalItems = options.NumberOfUsers,
                StartTime = DateTime.UtcNow,
                Status = "Running"
            };
            
            _activeGenerations[generationId] = status;
            
            // Run generation in background
            _ = Task.Run(async () => await GenerateUsersAsync(generationId, options));
            
            return generationId;
        }
        
        /// <summary>
        /// Start generating groups in the background
        /// </summary>
        public string StartGroupGeneration(int numberOfGroups, GroupGenerationOptions options)
        {
            var generationId = Guid.NewGuid().ToString();
            var status = new GenerationStatus
            {
                Id = generationId,
                Type = "Groups",
                TotalItems = numberOfGroups,
                StartTime = DateTime.UtcNow,
                Status = "Running"
            };
            
            _activeGenerations[generationId] = status;
            
            // Run generation in background
            _ = Task.Run(async () => await GenerateGroupsAsync(generationId, numberOfGroups, options));
            
            return generationId;
        }
        
        /// <summary>
        /// Get status of a generation job
        /// </summary>
        public GenerationStatus? GetGenerationStatus(string generationId)
        {
            return _activeGenerations.TryGetValue(generationId, out var status) ? status : null;
        }
        
        /// <summary>
        /// Get all active generation jobs
        /// </summary>
        public IEnumerable<GenerationStatus> GetActiveGenerations()
        {
            return _activeGenerations.Values.Where(s => s.Status == "Running");
        }
        
        /// <summary>
        /// Clear completed generations older than specified minutes
        /// </summary>
        public void ClearOldGenerations(int olderThanMinutes = 30)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-olderThanMinutes);
            var toRemove = _activeGenerations
                .Where(kvp => kvp.Value.Status != "Running" && kvp.Value.StartTime < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();
                
            foreach (var key in toRemove)
            {
                _activeGenerations.TryRemove(key, out _);
            }
        }
        
        private async Task GenerateUsersAsync(string generationId, UserGenerationOptions options)
        {
            var status = _activeGenerations[generationId];
            
            try
            {
                // Create a scope for this background task
                using var scope = _serviceProvider.CreateScope();
                var userRepository = scope.ServiceProvider.GetRequiredService<UserRepository>();
                var groupRepository = scope.ServiceProvider.GetRequiredService<GroupRepository>();
                
                // Pre-load the tenant's existing usernames/emails so the generator avoids
                // creating duplicates on re-runs (SCIM POST rejects with 409 uniqueness).
                var existingUsernames = new List<string>();
                var existingEmails = new List<string>();
                try
                {
                    var (existing, _) = await userRepository.GetAllAsync(
                        new ScimQueryOptions { StartIndex = 1, Count = 100000 },
                        tenantIdOverride: options.TenantId);
                    existingUsernames = existing.Select(u => u.UserName).Where(n => !string.IsNullOrEmpty(n)).ToList()!;
                    existingEmails = existing
                        .SelectMany(u => u.Emails ?? new List<Conduit.Core.Models.ScimEmail>())
                        .Select(e => e.Value)
                        .Where(v => !string.IsNullOrEmpty(v))
                        .ToList()!;
                }
                catch (Exception ex)
                {
                    status.Errors.Add($"Could not pre-load existing usernames (continuing without dedupe): {ex.Message}");
                }

                // Generate users
                var result = _userGenerationService.GenerateUsers(options, existingUsernames, existingEmails);

                // Save users to database
                var userIdMapping = new Dictionary<string, string>();
                
                foreach (var genUser in result.Users)
                {
                    try
                    {
                        var scimUser = new ScimUser
                        {
                            UserName = genUser.UserName,
                            Active = genUser.Active,
                            Name = new ScimName
                            {
                                GivenName = genUser.FirstName,
                                FamilyName = genUser.LastName,
                                Formatted = genUser.DisplayName
                            },
                            DisplayName = genUser.DisplayName,
                            Title = genUser.Title,
                            Emails = new List<ScimEmail>
                            {
                                new ScimEmail { Value = genUser.Email, Type = "work", Primary = true }
                            },
                            PhoneNumbers = new List<ScimPhoneNumber>(),
                            EnterpriseExtension = new ScimEnterpriseUser
                            {
                                EmployeeNumber = genUser.EmployeeNumber,
                                Department = genUser.Department,
                                Manager = null,
                                CostCenter = genUser.CostCenter
                            }
                        };
                        
                        if (!string.IsNullOrEmpty(genUser.PhoneNumber))
                        {
                            scimUser.PhoneNumbers.Add(new ScimPhoneNumber { Value = genUser.PhoneNumber, Type = "work" });
                        }
                        
                        var createdUser = await userRepository.CreateAsync(scimUser, options.TenantId);
                        userIdMapping[genUser.Id] = createdUser.Id;
                        
                        status.CompletedItems++;
                        status.UpdateProgress();
                    }
                    catch (Exception ex)
                    {
                        status.Errors.Add($"Error creating user {genUser.UserName}: {ex.Message}");
                    }
                }
                
                // Second pass: persist manager hierarchy. The generator wires up ManagerId
                // using its own temp string IDs; map them through userIdMapping to the real
                // persisted GUIDs and PATCH each user with its real manager.
                foreach (var genUser in result.Users.Where(u => !string.IsNullOrEmpty(u.ManagerId)))
                {
                    if (!userIdMapping.TryGetValue(genUser.Id, out var persistedId)) continue;
                    if (!userIdMapping.TryGetValue(genUser.ManagerId!, out var persistedManagerId)) continue;

                    try
                    {
                        var existing = await userRepository.GetByIdAsync(Guid.Parse(persistedId));
                        if (existing is null) continue;
                        existing.EnterpriseExtension ??= new ScimEnterpriseUser();
                        existing.EnterpriseExtension.Manager = new ScimManager { Value = persistedManagerId };
                        await userRepository.UpdateAsync(Guid.Parse(persistedId), existing);
                    }
                    catch (Exception ex)
                    {
                        status.Errors.Add($"Error setting manager for {genUser.UserName}: {ex.Message}");
                    }
                }

                // Save groups if any were generated
                foreach (var genGroup in result.Groups)
                {
                    try
                    {
                        var scimGroup = new ScimGroup
                        {
                            DisplayName = genGroup.DisplayName,
                            Description = genGroup.Description,
                            Type = genGroup.Type,
                            Members = new List<ScimGroupMember>()
                        };
                        
                        await groupRepository.CreateAsync(scimGroup, options.TenantId);
                    }
                    catch (Exception ex)
                    {
                        status.Errors.Add($"Error creating group {genGroup.DisplayName}: {ex.Message}");
                    }
                }
                
                status.Status = "Completed";
                status.EndTime = DateTime.UtcNow;
                status.Message = $"Successfully generated {status.CompletedItems} users";
                await scope.ServiceProvider.GetRequiredService<DataChangeNotifier>().NotifyAsync(DataChangeKind.Both);
            }
            catch (Exception ex)
            {
                status.Status = "Failed";
                status.EndTime = DateTime.UtcNow;
                status.Message = $"Generation failed: {ex.Message}";
                status.Errors.Add(ex.ToString());
            }
        }

        private async Task GenerateGroupsAsync(string generationId, int numberOfGroups, GroupGenerationOptions options)
        {
            var status = _activeGenerations[generationId];
            var random = new Random();
            
            try
            {
                // Create a scope for this background task
                using var scope = _serviceProvider.CreateScope();
                var userRepository = scope.ServiceProvider.GetRequiredService<UserRepository>();
                var groupRepository = scope.ServiceProvider.GetRequiredService<GroupRepository>();
                
                // Get existing users for member/owner assignment
                var (users, _) = await userRepository.GetAllAsync(new ScimQueryOptions { StartIndex = 1, Count = 1000 });
                var userIds = users.Select(u => u.Id).ToList();
                await _logger.LogInfoAsync("GroupGeneration", $"Found {userIds.Count} users for group member assignment");
                
                // Get existing groups to avoid duplicates
                var existingGroups = new HashSet<string>();
                var (allGroups, _) = await groupRepository.GetAllAsync(new ScimQueryOptions { StartIndex = 1, Count = 10000 });
                foreach (var g in allGroups)
                {
                    existingGroups.Add(g.DisplayName.ToLower());
                }
                await _logger.LogInfoAsync("GroupGeneration", $"Found {existingGroups.Count} existing groups, generating {numberOfGroups} new groups");
                await _logger.LogInfoAsync("GroupGeneration", $"Group generation options: Type={options.GroupType}, Prefix={options.NamePrefix}");
                
                for (int i = 0; i < numberOfGroups; i++)
                {
                    try
                    {
                        var groupName = options.GenerateGroupName(existingGroups);
                        await _logger.LogDebugAsync("GroupGeneration", $"Generated group name: {groupName}");
                        
                        var group = new ScimGroup
                        {
                            DisplayName = groupName,
                            Type = options.GroupType,
                            Members = new List<ScimGroupMember>()
                        };
                        
                        if (options.AddDescriptions)
                        {
                            group.Description = $"Group for {groupName} team members and collaborators";
                        }
                        
                        // Assign random owner
                        if (options.AssignRandomOwners && userIds.Any())
                        {
                            var ownerId = userIds[random.Next(userIds.Count)];
                            group.Owner = new ScimGroupMember
                            {
                                Value = ownerId.ToString(),
                                Type = "User"
                            };
                        }
                        
                        // Add random members
                        if (options.AddRandomMembers && userIds.Any())
                        {
                            var memberCount = random.Next(options.MinMembers, Math.Min(options.MaxMembers + 1, userIds.Count));
                            var selectedMembers = new HashSet<string>();
                            
                            if (group.Owner != null)
                            {
                                selectedMembers.Add(group.Owner.Value);
                            }
                            
                            for (int j = 0; j < memberCount; j++)
                            {
                                var userId = userIds[random.Next(userIds.Count)];
                                if (selectedMembers.Add(userId))
                                {
                                    group.Members.Add(new ScimGroupMember
                                    {
                                        Value = userId.ToString(),
                                        Type = "User"
                                    });
                                }
                            }
                        }
                        
                        var createdGroup = await groupRepository.CreateAsync(group, options.TenantId);
                        await _logger.LogInfoAsync("GroupGeneration", $"Successfully created group: {groupName} with ID: {createdGroup.Id}");
                        status.CompletedItems++;
                        status.UpdateProgress();
                        existingGroups.Add(groupName.ToLower());
                    }
                    catch (Exception ex)
                    {
                        status.Errors.Add($"Error creating group: {ex.Message}");
                        await _logger.LogErrorAsync("GroupGeneration", $"Failed to create group {i + 1}/{numberOfGroups}", ex);
                    }
                }
                
                status.Status = "Completed";
                status.EndTime = DateTime.UtcNow;
                status.Message = $"Successfully generated {status.CompletedItems} groups";
                await _logger.LogInfoAsync("GroupGeneration", status.Message);
                await scope.ServiceProvider.GetRequiredService<DataChangeNotifier>().NotifyAsync(DataChangeKind.Groups);
            }
            catch (Exception ex)
            {
                status.Status = "Failed";
                status.EndTime = DateTime.UtcNow;
                status.Message = $"Generation failed: {ex.Message}";
                status.Errors.Add(ex.ToString());
                await _logger.LogErrorAsync("GroupGeneration", "Group generation failed completely", ex);
            }
        }
    }
    
    /// <summary>
    /// Status of a generation job
    /// </summary>
    public class GenerationStatus
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public int TotalItems { get; set; }
        public int CompletedItems { get; set; }
        public double ProgressPercentage { get; set; }
        public string Message { get; set; } = "";
        public List<string> Errors { get; set; } = new();
        
        public void UpdateProgress()
        {
            ProgressPercentage = TotalItems > 0 ? (CompletedItems * 100.0 / TotalItems) : 0;
        }
        
        public string GetElapsedTime()
        {
            var elapsed = (EndTime ?? DateTime.UtcNow) - StartTime;
            return $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }
    }
    
    /// <summary>
    /// Options for group generation
    /// </summary>
    public class GroupGenerationOptions
    {
        /// <summary>
        /// Target Connected System (Tenant) the generated groups belong to.
        /// Required at the UI level; persistence layer falls back to Default if null.
        /// </summary>
        public Guid? TenantId { get; set; }

        public string GroupType { get; set; } = "mixed";
        public string NamePrefix { get; set; } = "";
        public bool AddRandomMembers { get; set; } = true;
        public bool AssignRandomOwners { get; set; } = true;
        public bool AddDescriptions { get; set; } = true;
        public int MinMembers { get; set; } = 5;
        public int MaxMembers { get; set; } = 20;
        
        private readonly Random _random = new Random();
        
        private readonly Dictionary<string, List<string>> _groupTemplates = new()
        {
            ["department"] = new() { "Engineering", "Sales", "Marketing", "HR", "Finance", "Operations", "IT", "Support", "Product", "Design", "Legal", "Research" },
            ["team"] = new() { "Alpha Team", "Beta Squad", "Gamma Force", "Delta Unit", "Echo Group", "Fox Team", "Tiger Squad", "Eagle Unit", "Wolf Pack", "Lion Pride" },
            ["project"] = new() { "Project Phoenix", "Initiative Aurora", "Operation Falcon", "Mission Apollo", "Project Nexus", "Initiative Quantum", "Operation Storm", "Project Titan" },
            ["role"] = new() { "Administrators", "Developers", "Managers", "Analysts", "Engineers", "Consultants", "Specialists", "Coordinators", "Directors", "Supervisors" },
            ["location"] = new() { "New York Office", "London Branch", "Tokyo Division", "Sydney Team", "Paris Office", "Berlin Hub", "Singapore Branch", "Toronto Office", "Mumbai Team" }
        };
        
        public string GenerateGroupName(HashSet<string> existingGroups)
        {
            string baseName;
            var templates = GroupType == "mixed" 
                ? _groupTemplates.Values.SelectMany(x => x).ToList() 
                : _groupTemplates.ContainsKey(GroupType) ? _groupTemplates[GroupType] : _groupTemplates["department"];
                
            for (int attempt = 0; attempt < 100; attempt++)
            {
                baseName = templates[_random.Next(templates.Count)];
                
                if (!string.IsNullOrEmpty(NamePrefix))
                {
                    baseName = NamePrefix + baseName;
                }
                
                if (!existingGroups.Contains(baseName.ToLower()))
                {
                    return baseName;
                }
                
                for (int i = 2; i < 1000; i++)
                {
                    var numberedName = $"{baseName} {i}";
                    if (!existingGroups.Contains(numberedName.ToLower()))
                    {
                        return numberedName;
                    }
                }
            }
            
            return $"{NamePrefix}Group-{Guid.NewGuid().ToString().Substring(0, 8)}";
        }
    }
}