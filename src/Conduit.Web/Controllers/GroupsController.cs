using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Newtonsoft.Json.Linq;
using Conduit.Core.Models;
using Conduit.DataAccess.Repositories;

namespace Conduit.Web.Controllers
{
    /// <summary>
    /// SCIM Groups endpoint controller
    /// </summary>
    [Route("scim/v2/[controller]")]
    [Route("scim/v2/t/{slug}/[controller]")]
    [EnableRateLimiting("scim")]
    public class GroupsController : BaseScimController
    {
        private readonly GroupRepository _groupRepository;

        public GroupsController(GroupRepository groupRepository)
        {
            _groupRepository = groupRepository;
        }

        /// <summary>
        /// Gets all groups with optional filtering and pagination
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetGroups(
            [FromQuery] string? filter = null,
            [FromQuery] string? sortBy = null,
            [FromQuery] string? sortOrder = null,
            [FromQuery] int startIndex = 1,
            [FromQuery] int count = 100)
        {
            var options = new ScimQueryOptions
            {
                Filter = filter,
                SortBy = sortBy,
                SortOrder = sortOrder,
                StartIndex = startIndex,
                Count = count
            };

            string? filterSql = null;
            DynamicParameters? filterParams = null;

            if (!string.IsNullOrWhiteSpace(filter))
            {
                try
                {
                    var ast = ScimFilterParser.Parse(filter);
                    (filterSql, filterParams) = SqlFilterBuilder.Build(ast, "Group");
                }
                catch (ScimFilterParseException ex)
                {
                    return ScimBadRequest(ex.Message, ScimErrorType.InvalidFilter);
                }
            }

            var (groups, totalCount) = await _groupRepository.GetAllAsync(options, filterSql, filterParams);

            var response = new ScimListResponse<ScimGroup>
            {
                TotalResults = totalCount,
                ItemsPerPage = groups.Count,
                StartIndex = startIndex,
                Resources = groups
            };

            var baseUrl = GetBaseUrl();
            foreach (var group in groups)
            {
                group.Meta.Location = $"{baseUrl}{ScimPrefix()}/Groups/{group.Id}";
            }

            return Ok(response);
        }

        /// <summary>
        /// Gets a specific group by ID
        /// </summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetGroup(string id)
        {
            if (!Guid.TryParse(id, out var groupId))
            {
                return ScimBadRequest("Invalid group ID format");
            }

            var group = await _groupRepository.GetByIdAsync(groupId);
            if (group == null)
            {
                return ScimNotFound("Group", id);
            }

            group.Meta.Location = $"{GetBaseUrl()}{ScimPrefix()}/Groups/{group.Id}";
            return Ok(group);
        }

        /// <summary>
        /// Creates a new group
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> CreateGroup([FromBody] ScimGroup group)
        {
            if (group == null)
            {
                return ScimBadRequest("Group data is required");
            }

            if (string.IsNullOrWhiteSpace(group.DisplayName))
            {
                return ScimBadRequest("displayName is required", ScimErrorType.InvalidValue);
            }

            try
            {
                var createdGroup = await _groupRepository.CreateAsync(group);
                createdGroup.Meta.Location = $"{GetBaseUrl()}{ScimPrefix()}/Groups/{createdGroup.Id}";

                SetLocationHeader("Groups", createdGroup.Id);
                return Created(createdGroup.Meta.Location, createdGroup);
            }
            catch (Microsoft.Data.SqlClient.SqlException sqlEx) when (sqlEx.Number == 2601 || sqlEx.Number == 2627)
            {
                return ScimConflict($"Group with displayName '{group.DisplayName}' already exists");
            }
            catch (Exception ex)
            {
                return ScimError(500, null, $"Error creating group: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates an existing group (full replace)
        /// </summary>
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateGroup(string id, [FromBody] ScimGroup group)
        {
            if (!Guid.TryParse(id, out var groupId))
            {
                return ScimBadRequest("Invalid group ID format");
            }

            if (group == null)
            {
                return ScimBadRequest("Group data is required");
            }

            if (string.IsNullOrWhiteSpace(group.DisplayName))
            {
                return ScimBadRequest("displayName is required", ScimErrorType.InvalidValue);
            }

            try
            {
                var existing = await _groupRepository.GetByIdAsync(groupId);
                if (existing == null)
                {
                    return ScimNotFound("Group", id);
                }

                var updatedGroup = await _groupRepository.UpdateAsync(groupId, group);
                updatedGroup.Meta.Location = $"{GetBaseUrl()}{ScimPrefix()}/Groups/{updatedGroup.Id}";
                return Ok(updatedGroup);
            }
            catch (Microsoft.Data.SqlClient.SqlException sqlEx) when (sqlEx.Number == 2601 || sqlEx.Number == 2627)
            {
                return ScimConflict($"Group with displayName '{group.DisplayName}' already exists");
            }
            catch (Exception ex)
            {
                return ScimError(500, null, $"Error updating group: {ex.Message}");
            }
        }

        /// <summary>
        /// Partially updates a group
        /// </summary>
        [HttpPatch("{id}")]
        public async Task<IActionResult> PatchGroup(string id, [FromBody] ScimPatchRequest patchRequest)
        {
            if (!Guid.TryParse(id, out var groupId))
            {
                return ScimBadRequest("Invalid group ID format");
            }

            if (patchRequest == null || patchRequest.Operations.Count == 0)
            {
                return ScimBadRequest("Patch operations are required");
            }

            var group = await _groupRepository.GetByIdAsync(groupId);
            if (group == null)
            {
                return ScimNotFound("Group", id);
            }

            try
            {
                foreach (var operation in patchRequest.Operations)
                {
                    ApplyGroupPatchOperation(group, operation);
                }

                var updatedGroup = await _groupRepository.UpdateAsync(groupId, group);
                updatedGroup.Meta.Location = $"{GetBaseUrl()}{ScimPrefix()}/Groups/{updatedGroup.Id}";
                return Ok(updatedGroup);
            }
            catch (Microsoft.Data.SqlClient.SqlException sqlEx) when (sqlEx.Number == 2601 || sqlEx.Number == 2627)
            {
                return ScimConflict($"Group with displayName '{group.DisplayName}' already exists");
            }
            catch (Exception ex)
            {
                return ScimError(500, null, $"Error patching group: {ex.Message}");
            }
        }

        /// <summary>
        /// Deletes a group
        /// </summary>
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteGroup(string id)
        {
            if (!Guid.TryParse(id, out var groupId))
            {
                return ScimBadRequest("Invalid group ID format");
            }

            var deleted = await _groupRepository.DeleteAsync(groupId);
            if (!deleted)
            {
                return ScimNotFound("Group", id);
            }

            return NoContent();
        }

        /// <summary>
        /// Applies a patch operation to a group
        /// </summary>
        private void ApplyGroupPatchOperation(ScimGroup group, ScimPatchOperation operation)
        {
            var path = operation.Path?.ToLowerInvariant();

            switch (operation.Op)
            {
                case ScimPatchOperationType.Add:
                    if (path == "members" || path == null)
                    {
                        AddMembers(group, operation.Value);
                    }
                    else if (path == "displayname")
                    {
                        group.DisplayName = operation.Value?.ToString() ?? group.DisplayName;
                    }
                    break;

                case ScimPatchOperationType.Replace:
                    if (path == "displayname")
                    {
                        group.DisplayName = operation.Value?.ToString() ?? group.DisplayName;
                    }
                    else if (path == "members")
                    {
                        ReplaceMembers(group, operation.Value);
                    }
                    else if (path == null)
                    {
                        // No-path replace: value is an object with attributes
                        ApplyNoPathReplace(group, operation.Value);
                    }
                    break;

                case ScimPatchOperationType.Remove:
                    if (path == "members")
                    {
                        group.Members.Clear();
                    }
                    else if (path != null && path.StartsWith("members[value eq "))
                    {
                        // Parse: members[value eq "guid"]
                        var memberId = ExtractFilterValue(operation.Path!);
                        if (memberId != null)
                        {
                            group.Members.RemoveAll(m =>
                                string.Equals(m.Value, memberId, StringComparison.OrdinalIgnoreCase));
                        }
                    }
                    else if (path == "displayname")
                    {
                        group.DisplayName = string.Empty;
                    }
                    break;
            }
        }

        private void AddMembers(ScimGroup group, object? value)
        {
            if (value == null) return;

            var members = ParseMembersFromValue(value);
            foreach (var member in members)
            {
                if (!group.Members.Any(m =>
                    string.Equals(m.Value, member.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    group.Members.Add(member);
                }
            }
        }

        private void ReplaceMembers(ScimGroup group, object? value)
        {
            if (value == null)
            {
                group.Members.Clear();
                return;
            }

            group.Members = ParseMembersFromValue(value);
        }

        private List<ScimGroupMember> ParseMembersFromValue(object value)
        {
            var members = new List<ScimGroupMember>();

            if (value is JArray arr)
            {
                foreach (var item in arr)
                {
                    var member = item.ToObject<ScimGroupMember>();
                    if (member != null)
                    {
                        members.Add(member);
                    }
                }
            }
            else if (value is JObject obj)
            {
                var member = obj.ToObject<ScimGroupMember>();
                if (member != null)
                {
                    members.Add(member);
                }
            }

            return members;
        }

        private void ApplyNoPathReplace(ScimGroup group, object? value)
        {
            if (value is not JObject obj) return;

            if (obj.TryGetValue("displayName", StringComparison.OrdinalIgnoreCase, out var dn))
            {
                group.DisplayName = dn.ToString();
            }

            if (obj.TryGetValue("members", StringComparison.OrdinalIgnoreCase, out var membersToken))
            {
                ReplaceMembers(group, membersToken);
            }
        }

        /// <summary>
        /// Extracts the value from a SCIM path filter like members[value eq "guid"]
        /// </summary>
        private static string? ExtractFilterValue(string path)
        {
            // members[value eq "some-guid"]
            var startQuote = path.IndexOf('"');
            var endQuote = path.LastIndexOf('"');
            if (startQuote >= 0 && endQuote > startQuote)
            {
                return path.Substring(startQuote + 1, endQuote - startQuote - 1);
            }
            return null;
        }
    }
}
