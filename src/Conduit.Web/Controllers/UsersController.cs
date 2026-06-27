using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Newtonsoft.Json.Linq;
using Conduit.Core.Models;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Web.Authentication;
using Conduit.Web.Services;

namespace Conduit.Web.Controllers
{
    /// <summary>
    /// SCIM Users endpoint controller
    /// </summary>
    [Route("scim/v2/[controller]")]
    [Route("scim/v2/t/{slug}/[controller]")]
    [EnableRateLimiting("scim")]
    public class UsersController : BaseScimController
    {
        private readonly UserRepository _userRepository;
        private readonly TokenService _tokenService;
        private readonly ApplicationLogService _appLog;
        private readonly InboundProxyService _proxy;

        private const string EnterpriseExtensionPrefix = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:";

        /// <summary>
        /// Initializes a new instance of the UsersController class
        /// </summary>
        public UsersController(UserRepository userRepository, TokenService tokenService, ApplicationLogService appLog, InboundProxyService proxy)
        {
            _userRepository = userRepository;
            _tokenService = tokenService;
            _appLog = appLog;
            _proxy = proxy;
        }

        /// <summary>
        /// Gets all users with optional filtering and pagination
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetUsers(
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
                    (filterSql, filterParams) = SqlFilterBuilder.Build(ast, "User");
                }
                catch (ScimFilterParseException ex)
                {
                    return ScimBadRequest(ex.Message, ScimErrorType.InvalidFilter);
                }
            }

            var (users, totalCount) = await _userRepository.GetAllAsync(options, filterSql, filterParams);

            var response = new ScimListResponse<ScimUser>
            {
                TotalResults = totalCount,
                ItemsPerPage = users.Count,
                StartIndex = startIndex,
                Resources = users
            };

            // Set resource locations
            var baseUrl = GetBaseUrl();
            foreach (var user in users)
            {
                user.Meta.Location = $"{baseUrl}{ScimPrefix()}/Users/{user.Id}";
            }

            return Ok(response);
        }

        /// <summary>
        /// Gets a specific user by ID
        /// </summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetUser(string id)
        {
            if (!Guid.TryParse(id, out var userId))
            {
                return ScimBadRequest("Invalid user ID format");
            }

            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null)
            {
                return ScimNotFound("User", id);
            }

            user.Meta.Location = $"{GetBaseUrl()}{ScimPrefix()}/Users/{user.Id}";
            return Ok(user);
        }

        /// <summary>
        /// Creates a new user
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> CreateUser([FromBody] ScimUser user, CancellationToken ct)
        {
            if (user == null)
            {
                return ScimBadRequest("User data is required");
            }

            if (string.IsNullOrWhiteSpace(user.UserName))
            {
                return ScimBadRequest("userName is required", ScimErrorType.InvalidValue);
            }

            // Phase 1 inbound proxy: when this connection is a writable external
            // target (its adapter SupportsCreate), forward the create to that target's
            // sink INSTEAD of writing the local store. Otherwise fall through to the
            // legacy local-store path below (Emulator / slug-less default route).
            var connectorObject = InboundScimMapper.FromScimUser(user);
            var proxy = await _proxy.TryProxyCreateAsync(connectorObject, "User", ct);
            if (proxy.Decision == InboundProxyService.ProxyDecision.Proxied)
            {
                return ScimFromProxy(proxy, user, "Users");
            }

            // Check if username already exists
            var existing = await _userRepository.GetByUserNameAsync(user.UserName);
            if (existing != null)
            {
                return ScimConflict($"User with userName '{user.UserName}' already exists");
            }

            try
            {
                var createdUser = await _userRepository.CreateAsync(user);
                createdUser.Meta.Location = $"{GetBaseUrl()}{ScimPrefix()}/Users/{createdUser.Id}";

                SetLocationHeader("Users", createdUser.Id);
                return Created(createdUser.Meta.Location, createdUser);
            }
            catch (Microsoft.Data.SqlClient.SqlException sqlEx) when (sqlEx.Number == 2601 || sqlEx.Number == 2627)
            {
                // 2601 = unique index violation, 2627 = unique constraint violation.
                // Race between our 'existing' check and the INSERT, or a tenant-scoped
                // unique constraint catching a duplicate userName. Either way, this
                // is the SCIM-defined 409 Conflict.
                await _appLog.LogAsync(ApplicationLogService.LogLevel.Warning, "SCIM/Users/POST",
                    $"CreateUser rejected (unique constraint): {sqlEx.Message}",
                    details: $"userName={user.UserName}");
                return ScimConflict($"User with userName '{user.UserName}' already exists");
            }
            catch (Exception ex)
            {
                await _appLog.LogAsync(ApplicationLogService.LogLevel.Error, "SCIM/Users/POST",
                    $"CreateUser threw {ex.GetType().Name}: {ex.Message}",
                    details: $"userName={user.UserName}  payload={Newtonsoft.Json.JsonConvert.SerializeObject(user)}",
                    exception: ex);
                return ScimError(500, null, $"Error creating user: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates an existing user (full update)
        /// </summary>
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateUser(string id, [FromBody] ScimUser user, CancellationToken ct)
        {
            if (user == null)
            {
                return ScimBadRequest("User data is required");
            }

            if (string.IsNullOrWhiteSpace(user.UserName))
            {
                return ScimBadRequest("userName is required", ScimErrorType.InvalidValue);
            }

            // Phase 2 inbound proxy: when this connection is a writable external
            // target that supports update, forward the PUT (full replace) to that
            // target's sink. The path {id} IS the resource id the caller addressed
            // (for IC the target row GUID). NotSupported / Failed surface honestly;
            // a non-target connection falls through to the local store below.
            var connectorObject = InboundScimMapper.FromScimUser(user);
            var proxy = await _proxy.TryProxyUpdateAsync(id, connectorObject, replace: true, "User", ct);
            if (proxy.Decision == InboundProxyService.ProxyDecision.Proxied)
            {
                return ScimFromProxyUpdate(proxy, user, "Users");
            }

            if (!Guid.TryParse(id, out var userId))
            {
                return ScimBadRequest("Invalid user ID format");
            }

            // Check if changing username to one that already exists
            var existing = await _userRepository.GetByUserNameAsync(user.UserName);
            if (existing != null && existing.Id != id)
            {
                return ScimConflict($"User with userName '{user.UserName}' already exists");
            }

            try
            {
                var updatedUser = await _userRepository.UpdateAsync(userId, user);
                if (updatedUser == null)
                {
                    return ScimNotFound("User", id);
                }

                updatedUser.Meta.Location = $"{GetBaseUrl()}{ScimPrefix()}/Users/{updatedUser.Id}";
                return Ok(updatedUser);
            }
            catch (Microsoft.Data.SqlClient.SqlException sqlEx) when (sqlEx.Number == 2601 || sqlEx.Number == 2627)
            {
                await _appLog.LogAsync(ApplicationLogService.LogLevel.Warning, "SCIM/Users/PUT",
                    $"UpdateUser rejected (unique constraint): {sqlEx.Message}",
                    details: $"id={id} userName={user.UserName}");
                return ScimConflict($"User with userName '{user.UserName}' already exists");
            }
            catch (Exception ex)
            {
                await _appLog.LogAsync(ApplicationLogService.LogLevel.Error, "SCIM/Users/PUT",
                    $"UpdateUser threw {ex.GetType().Name}: {ex.Message}",
                    details: $"id={id} userName={user.UserName}", exception: ex);
                return ScimError(500, null, $"Error updating user: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Partially updates a user
        /// </summary>
        [HttpPatch("{id}")]
        public async Task<IActionResult> PatchUser(string id, [FromBody] ScimPatchRequest patchRequest, CancellationToken ct)
        {
            if (patchRequest == null || patchRequest.Operations.Count == 0)
            {
                return ScimBadRequest("Patch operations are required");
            }

            // Phase 2 inbound proxy: for a writable external target, apply the patch
            // ops onto a fresh ScimUser (we cannot read the external row into our
            // local model) and forward it as a PARTIAL merge (replace=false). Only
            // the attributes the patch touched are sent, so omitted attributes are
            // left untouched on the target — true PATCH semantics. A non-target
            // connection falls through to the local read-modify-write path below.
            {
                var patched = new ScimUser { Id = id };
                foreach (var operation in patchRequest.Operations)
                {
                    ApplyPatchOperation(patched, operation);
                }
                var connectorObject = InboundScimMapper.FromScimUser(patched);
                var proxy = await _proxy.TryProxyUpdateAsync(id, connectorObject, replace: false, "User", ct);
                if (proxy.Decision == InboundProxyService.ProxyDecision.Proxied)
                {
                    return ScimFromProxyUpdate(proxy, patched, "Users");
                }
            }

            if (!Guid.TryParse(id, out var userId))
            {
                return ScimBadRequest("Invalid user ID format");
            }

            // Get existing user
            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null)
            {
                return ScimNotFound("User", id);
            }

            try
            {
                // Apply patch operations
                foreach (var operation in patchRequest.Operations)
                {
                    ApplyPatchOperation(user, operation);
                }

                var updatedUser = await _userRepository.UpdateAsync(userId, user);
                if (updatedUser == null)
                {
                    return ScimNotFound("User", id);
                }

                updatedUser.Meta.Location = $"{GetBaseUrl()}{ScimPrefix()}/Users/{updatedUser.Id}";
                return Ok(updatedUser);
            }
            catch (Microsoft.Data.SqlClient.SqlException sqlEx) when (sqlEx.Number == 2601 || sqlEx.Number == 2627)
            {
                await _appLog.LogAsync(ApplicationLogService.LogLevel.Warning, "SCIM/Users/PATCH",
                    $"PatchUser rejected (unique constraint): {sqlEx.Message}",
                    details: $"id={id} userName={user.UserName}");
                return ScimConflict($"User with userName '{user.UserName}' already exists");
            }
            catch (Exception ex)
            {
                await _appLog.LogAsync(ApplicationLogService.LogLevel.Error, "SCIM/Users/PATCH",
                    $"PatchUser threw {ex.GetType().Name}: {ex.Message}",
                    details: $"id={id}", exception: ex);
                return ScimError(500, null, $"Error patching user: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Deletes a user
        /// </summary>
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteUser(string id)
        {
            if (!Guid.TryParse(id, out var userId))
            {
                return ScimBadRequest("Invalid user ID format");
            }

            try
            {
                var deleted = await _userRepository.DeleteAsync(userId);
                if (!deleted)
                {
                    return ScimNotFound("User", id);
                }
                return NoContent();
            }
            catch (Exception ex)
            {
                await _appLog.LogAsync(ApplicationLogService.LogLevel.Error, "SCIM/Users/DELETE",
                    $"DeleteUser threw {ex.GetType().Name}: {ex.Message}",
                    details: $"id={id}", exception: ex);
                return ScimError(500, null, $"Error deleting user: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies a patch operation to a user, supporting all common SCIM attributes
        /// </summary>
        private void ApplyPatchOperation(ScimUser user, ScimPatchOperation operation)
        {
            var path = operation.Path;

            // Handle no-path pattern: {"op":"replace","value":{"active":false}}
            // Azure AD sends this format
            if (string.IsNullOrEmpty(path))
            {
                if (operation.Op == ScimPatchOperationType.Replace && operation.Value != null)
                {
                    ApplyNoPathReplace(user, operation.Value);
                }
                return;
            }

            var lowerPath = path.ToLowerInvariant();

            switch (operation.Op)
            {
                case ScimPatchOperationType.Add:
                case ScimPatchOperationType.Replace:
                    ApplyAddOrReplace(user, path, lowerPath, operation.Value);
                    break;

                case ScimPatchOperationType.Remove:
                    ApplyRemove(user, lowerPath);
                    break;
            }
        }

        private void ApplyAddOrReplace(ScimUser user, string path, string lowerPath, object? value)
        {
            var stringValue = GetStringValue(value);

            switch (lowerPath)
            {
                // Core scalar attributes
                case "active":
                    user.Active = GetBoolValue(value);
                    break;
                case "username":
                    if (stringValue != null) user.UserName = stringValue;
                    break;
                case "displayname":
                    user.DisplayName = stringValue;
                    break;
                case "nickname":
                    user.NickName = stringValue;
                    break;
                case "title":
                    user.Title = stringValue;
                    break;
                case "usertype":
                    user.UserType = stringValue;
                    break;
                case "preferredlanguage":
                    user.PreferredLanguage = stringValue;
                    break;
                case "locale":
                    user.Locale = stringValue;
                    break;
                case "timezone":
                    user.Timezone = stringValue;
                    break;
                case "externalid":
                    user.ExternalId = stringValue;
                    break;
                case "profileurl":
                    user.ProfileUrl = stringValue;
                    break;

                // Name sub-attributes
                case "name.givenname":
                    EnsureName(user).GivenName = stringValue;
                    break;
                case "name.familyname":
                    EnsureName(user).FamilyName = stringValue;
                    break;
                case "name.formatted":
                    EnsureName(user).Formatted = stringValue;
                    break;
                case "name.middlename":
                    EnsureName(user).MiddleName = stringValue;
                    break;
                case "name.honorificprefix":
                    EnsureName(user).HonorificPrefix = stringValue;
                    break;
                case "name.honorificsuffix":
                    EnsureName(user).HonorificSuffix = stringValue;
                    break;

                // Multi-value attributes
                case "emails":
                    ApplyMultiValueEmails(user, value);
                    break;
                case "phonenumbers":
                    ApplyMultiValuePhoneNumbers(user, value);
                    break;

                default:
                    // Enterprise extension attributes
                    if (lowerPath.StartsWith(EnterpriseExtensionPrefix.ToLowerInvariant()))
                    {
                        ApplyEnterpriseExtension(user, path, stringValue);
                    }
                    break;
            }
        }

        private void ApplyRemove(ScimUser user, string lowerPath)
        {
            switch (lowerPath)
            {
                case "displayname":
                    user.DisplayName = null;
                    break;
                case "nickname":
                    user.NickName = null;
                    break;
                case "title":
                    user.Title = null;
                    break;
                case "usertype":
                    user.UserType = null;
                    break;
                case "preferredlanguage":
                    user.PreferredLanguage = null;
                    break;
                case "locale":
                    user.Locale = null;
                    break;
                case "timezone":
                    user.Timezone = null;
                    break;
                case "externalid":
                    user.ExternalId = null;
                    break;
                case "profileurl":
                    user.ProfileUrl = null;
                    break;
                case "name.givenname":
                    if (user.Name != null) user.Name.GivenName = null;
                    break;
                case "name.familyname":
                    if (user.Name != null) user.Name.FamilyName = null;
                    break;
                case "name.formatted":
                    if (user.Name != null) user.Name.Formatted = null;
                    break;
                case "name.middlename":
                    if (user.Name != null) user.Name.MiddleName = null;
                    break;
                case "name":
                    user.Name = null;
                    break;
                case "emails":
                    user.Emails.Clear();
                    break;
                case "phonenumbers":
                    user.PhoneNumbers.Clear();
                    break;
                default:
                    if (lowerPath.StartsWith(EnterpriseExtensionPrefix.ToLowerInvariant()))
                    {
                        ApplyEnterpriseExtension(user, lowerPath, null);
                    }
                    break;
            }
        }

        /// <summary>
        /// Handles the no-path replace pattern (Azure AD style):
        /// {"op":"replace","value":{"active":false,"displayName":"New Name"}}
        /// </summary>
        private void ApplyNoPathReplace(ScimUser user, object value)
        {
            if (value is not JObject obj) return;

            foreach (var prop in obj.Properties())
            {
                var fakePath = prop.Name;
                var fakeValue = prop.Value;

                // Recurse through ApplyAddOrReplace with each property as a path
                ApplyAddOrReplace(user, fakePath, fakePath.ToLowerInvariant(), fakeValue);
            }
        }

        private void ApplyEnterpriseExtension(ScimUser user, string path, string? value)
        {
            var ext = EnsureEnterpriseExtension(user);
            var attr = path.Substring(path.LastIndexOf(':') + 1).ToLowerInvariant();

            switch (attr)
            {
                case "department":
                    ext.Department = value;
                    break;
                case "employeenumber":
                    ext.EmployeeNumber = value;
                    break;
                case "costcenter":
                    ext.CostCenter = value;
                    break;
                case "organization":
                    ext.Organization = value;
                    break;
                case "division":
                    ext.Division = value;
                    break;
                case "manager":
                    if (value != null)
                    {
                        ext.Manager ??= new ScimManager();
                        ext.Manager.Value = value;
                    }
                    else
                    {
                        ext.Manager = null;
                    }
                    break;
            }
        }

        private void ApplyMultiValueEmails(ScimUser user, object? value)
        {
            if (value == null) return;

            if (value is JArray arr)
            {
                var emails = arr.ToObject<List<ScimEmail>>();
                if (emails != null) user.Emails = emails;
            }
            else if (value is JObject obj)
            {
                var email = obj.ToObject<ScimEmail>();
                if (email != null)
                {
                    if (!user.Emails.Any(e => string.Equals(e.Value, email.Value, StringComparison.OrdinalIgnoreCase)))
                    {
                        user.Emails.Add(email);
                    }
                }
            }
        }

        private void ApplyMultiValuePhoneNumbers(ScimUser user, object? value)
        {
            if (value == null) return;

            if (value is JArray arr)
            {
                var phones = arr.ToObject<List<ScimPhoneNumber>>();
                if (phones != null) user.PhoneNumbers = phones;
            }
            else if (value is JObject obj)
            {
                var phone = obj.ToObject<ScimPhoneNumber>();
                if (phone != null)
                {
                    if (!user.PhoneNumbers.Any(p => string.Equals(p.Value, phone.Value, StringComparison.OrdinalIgnoreCase)))
                    {
                        user.PhoneNumbers.Add(phone);
                    }
                }
            }
        }

        private static ScimName EnsureName(ScimUser user)
        {
            user.Name ??= new ScimName();
            return user.Name;
        }

        private static ScimEnterpriseUser EnsureEnterpriseExtension(ScimUser user)
        {
            user.EnterpriseExtension ??= new ScimEnterpriseUser();
            if (!user.Schemas.Contains("urn:ietf:params:scim:schemas:extension:enterprise:2.0:User"))
            {
                user.Schemas.Add("urn:ietf:params:scim:schemas:extension:enterprise:2.0:User");
            }
            return user.EnterpriseExtension;
        }

        private static string? GetStringValue(object? value)
        {
            if (value == null) return null;
            if (value is JValue jv) return jv.Value?.ToString();
            return value.ToString();
        }

        private static bool GetBoolValue(object? value)
        {
            if (value is bool b) return b;
            if (value is JValue jv)
            {
                if (jv.Type == JTokenType.Boolean) return jv.Value<bool>();
                return jv.Value?.ToString()?.ToLowerInvariant() == "true";
            }
            return value?.ToString()?.ToLowerInvariant() == "true";
        }

        /// <summary>
        /// Translate a proxied provision outcome into a SCIM-compliant response:
        ///   Success/Accepted → 201 Created, the resource echoed back with the TARGET's
        ///                       assigned id + a round-trippable Meta.Location + Location header.
        ///   NotSupported     → 501 (the target can't create) as a SCIM error.
        ///   Failed           → 502 as a SCIM error (the downstream target rejected it),
        ///                       preserving the target's message.
        /// The 201 body is the SCIM resource the caller sent, re-stamped with the
        /// target id so downstream SCIM clients can address the object by that id.
        /// </summary>
        private IActionResult ScimFromProxy(InboundProxyService.ProxyResult proxy, ScimUser user, string resourceType)
        {
            switch (proxy.Outcome)
            {
                case ProvisionOutcome.Success:
                case ProvisionOutcome.Accepted:
                    var targetId = string.IsNullOrEmpty(proxy.ExternalId) ? user.Id : proxy.ExternalId!;
                    user.Id = targetId;
                    user.Meta.Location = $"{GetBaseUrl()}{ScimPrefix()}/{resourceType}/{targetId}";
                    SetLocationHeader(resourceType, targetId);
                    return Created(user.Meta.Location, user);

                case ProvisionOutcome.NotSupported:
                    return ScimError(501, ScimErrorType.InvalidValue,
                        proxy.ErrorMessage ?? $"Connector '{proxy.SystemType}' does not support inbound create.");

                default: // Failed
                    return ScimError(502, null,
                        proxy.ErrorMessage ?? $"Target connector '{proxy.SystemType}' rejected the create.");
            }
        }

        /// <summary>
        /// Translate a proxied UPDATE outcome into a SCIM response. Mirrors
        /// <see cref="ScimFromProxy"/> but returns 200 OK (not 201 Created) for a
        /// successful update, re-stamping the resource with the target's id +
        /// round-trippable Location. NotSupported → 501; Failed → 502.
        /// </summary>
        private IActionResult ScimFromProxyUpdate(InboundProxyService.ProxyResult proxy, ScimUser user, string resourceType)
        {
            switch (proxy.Outcome)
            {
                case ProvisionOutcome.Success:
                case ProvisionOutcome.Accepted:
                    var targetId = string.IsNullOrEmpty(proxy.ExternalId) ? user.Id : proxy.ExternalId!;
                    user.Id = targetId;
                    user.Meta.Location = $"{GetBaseUrl()}{ScimPrefix()}/{resourceType}/{targetId}";
                    return Ok(user);

                case ProvisionOutcome.NotSupported:
                    return ScimError(501, ScimErrorType.InvalidValue,
                        proxy.ErrorMessage ?? $"Connector '{proxy.SystemType}' does not support inbound update.");

                default: // Failed
                    return ScimError(502, null,
                        proxy.ErrorMessage ?? $"Target connector '{proxy.SystemType}' rejected the update.");
            }
        }
    }
}
