using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Newtonsoft.Json.Linq;
using System.Threading;
using Conduit.Core.Models;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Web.Services;

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
        private readonly InboundProxyService _proxy;

        public GroupsController(GroupRepository groupRepository, InboundProxyService proxy)
        {
            _groupRepository = groupRepository;
            _proxy = proxy;
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
        public async Task<IActionResult> CreateGroup([FromBody] ScimGroup group, CancellationToken ct)
        {
            if (group == null)
            {
                return ScimBadRequest("Group data is required");
            }

            if (string.IsNullOrWhiteSpace(group.DisplayName))
            {
                return ScimBadRequest("displayName is required", ScimErrorType.InvalidValue);
            }

            // Phase 1 inbound proxy: forward to a writable external target's sink when
            // this connection supports create; else fall through to the local store.
            var connectorObject = InboundScimMapper.FromScimGroup(group);
            var proxy = await _proxy.TryProxyCreateAsync(connectorObject, "Group", ct);
            if (proxy.Decision == InboundProxyService.ProxyDecision.Proxied)
            {
                return ScimFromProxyGroup(proxy, group);
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
        public async Task<IActionResult> UpdateGroup(string id, [FromBody] ScimGroup group, CancellationToken ct)
        {
            if (group == null)
            {
                return ScimBadRequest("Group data is required");
            }

            if (string.IsNullOrWhiteSpace(group.DisplayName))
            {
                return ScimBadRequest("displayName is required", ScimErrorType.InvalidValue);
            }

            // Phase 2 inbound proxy: forward the PUT to a writable external target's
            // sink when it supports update; else fall through to the local store.
            var connectorObject = InboundScimMapper.FromScimGroup(group);
            var proxy = await _proxy.TryProxyUpdateAsync(id, connectorObject, replace: true, "Group", ct);
            if (proxy.Decision == InboundProxyService.ProxyDecision.Proxied)
            {
                return ScimFromProxyGroupUpdate(proxy, group);
            }

            if (!Guid.TryParse(id, out var groupId))
            {
                return ScimBadRequest("Invalid group ID format");
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
        public async Task<IActionResult> PatchGroup(string id, [FromBody] ScimPatchRequest patchRequest, CancellationToken ct)
        {
            if (patchRequest == null || patchRequest.Operations.Count == 0)
            {
                return ScimBadRequest("Patch operations are required");
            }

            // Phase 2 inbound proxy: apply the patch ops onto a fresh ScimGroup and
            // forward as a PARTIAL merge to a writable external target; else local.
            {
                var patched = new ScimGroup { Id = id };
                foreach (var operation in patchRequest.Operations)
                {
                    ApplyGroupPatchOperation(patched, operation);
                }
                var connectorObject = InboundScimMapper.FromScimGroup(patched);
                var proxy = await _proxy.TryProxyUpdateAsync(id, connectorObject, replace: false, "Group", ct);
                if (proxy.Decision == InboundProxyService.ProxyDecision.Proxied)
                {
                    return ScimFromProxyGroupUpdate(proxy, patched);
                }
            }

            if (!Guid.TryParse(id, out var groupId))
            {
                return ScimBadRequest("Invalid group ID format");
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
        public async Task<IActionResult> DeleteGroup(string id, CancellationToken ct)
        {
            // Phase 2 inbound proxy: deprovision on a writable external target that
            // supports delete; else fall through to the local store delete below.
            var proxy = await _proxy.TryProxyDeleteAsync(id, "Group", ct);
            if (proxy.Decision == InboundProxyService.ProxyDecision.Proxied)
            {
                return ScimFromProxyDelete(proxy);
            }

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

        /// <summary>
        /// Translate a proxied provision outcome into a SCIM-compliant Group response.
        /// Mirrors UsersController.ScimFromProxy: Success → 201 with the target's
        /// assigned id + round-trippable Location; NotSupported → 501; Failed → 502.
        /// </summary>
        private IActionResult ScimFromProxyGroup(InboundProxyService.ProxyResult proxy, ScimGroup group)
        {
            switch (proxy.Outcome)
            {
                case ProvisionOutcome.Success:
                case ProvisionOutcome.Accepted:
                    var targetId = string.IsNullOrEmpty(proxy.ExternalId) ? group.Id : proxy.ExternalId!;
                    group.Id = targetId;
                    group.Meta.Location = $"{GetBaseUrl()}{ScimPrefix()}/Groups/{targetId}";
                    SetLocationHeader("Groups", targetId);
                    return Created(group.Meta.Location, group);

                case ProvisionOutcome.NotSupported:
                    return ScimError(501, ScimErrorType.InvalidValue,
                        proxy.ErrorMessage ?? $"Connector '{proxy.SystemType}' does not support inbound create.");

                default: // Failed
                    return ScimError(502, null,
                        proxy.ErrorMessage ?? $"Target connector '{proxy.SystemType}' rejected the create.");
            }
        }

        /// <summary>
        /// Translate a proxied Group UPDATE outcome into a SCIM response: 200 OK on
        /// success (re-stamped with the target id + Location), 501 NotSupported, 502
        /// Failed. Mirrors UsersController.ScimFromProxyUpdate.
        /// </summary>
        private IActionResult ScimFromProxyGroupUpdate(InboundProxyService.ProxyResult proxy, ScimGroup group)
        {
            switch (proxy.Outcome)
            {
                case ProvisionOutcome.Success:
                case ProvisionOutcome.Accepted:
                    var targetId = string.IsNullOrEmpty(proxy.ExternalId) ? group.Id : proxy.ExternalId!;
                    group.Id = targetId;
                    group.Meta.Location = $"{GetBaseUrl()}{ScimPrefix()}/Groups/{targetId}";
                    return Ok(group);

                case ProvisionOutcome.NotSupported:
                    return ScimError(501, ScimErrorType.InvalidValue,
                        proxy.ErrorMessage ?? $"Connector '{proxy.SystemType}' does not support inbound update.");

                default: // Failed
                    return ScimError(502, null,
                        proxy.ErrorMessage ?? $"Target connector '{proxy.SystemType}' rejected the update.");
            }
        }

        /// <summary>
        /// Translate a proxied Group DELETE outcome: 204 success, 501 NotSupported,
        /// 502 Failed. Mirrors UsersController.ScimFromProxyDelete.
        /// </summary>
        private IActionResult ScimFromProxyDelete(InboundProxyService.ProxyResult proxy)
        {
            switch (proxy.Outcome)
            {
                case ProvisionOutcome.Success:
                case ProvisionOutcome.Accepted:
                    return NoContent();

                case ProvisionOutcome.NotSupported:
                    return ScimError(501, ScimErrorType.InvalidValue,
                        proxy.ErrorMessage ?? $"Connector '{proxy.SystemType}' does not support inbound delete.");

                default: // Failed
                    return ScimError(502, null,
                        proxy.ErrorMessage ?? $"Target connector '{proxy.SystemType}' rejected the delete.");
            }
        }
    }
}
