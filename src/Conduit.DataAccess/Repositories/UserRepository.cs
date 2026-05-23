using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Conduit.Core.Models;
using Conduit.Core.Services;

namespace Conduit.DataAccess.Repositories
{
    /// <summary>
    /// Repository for user data access
    /// </summary>
    public class UserRepository : BaseRepository
    {
        private readonly ITenantContext? _tenantContext;

        /// <summary>
        /// Initializes a new instance of the UserRepository class.
        /// When <paramref name="tenantContext"/> is supplied, all queries are scoped
        /// to the active tenant (UI label "Connected System") unless the caller has
        /// Admin scope.
        /// </summary>
        public UserRepository(DatabaseConfig config, ITenantContext? tenantContext = null) : base(config)
        {
            _tenantContext = tenantContext;
        }

        /// <summary>
        /// Returns the active scope tenant id, or null if the caller is Admin or no
        /// context is wired. Repositories use this to gate every query.
        /// </summary>
        private Guid? ScopeTenantId => _tenantContext is { IsAdmin: false } ? _tenantContext.TenantId : null;

        /// <summary>
        /// Tenant id to use when inserting rows. Falls back to the Default tenant
        /// (matches the v8 migration default) when no context is in scope.
        /// </summary>
        private Guid InsertTenantId =>
            _tenantContext?.TenantId
            ?? TenantRepository.DefaultTenantId;

        /// <summary>
        /// Effective tenant filter for a query. An explicit <paramref name="overrideId"/>
        /// (e.g. admin UI selecting "show only this Connected System") wins over the
        /// context-derived scope; null falls back to the context's scope. Returns null
        /// when no filter should be applied (admin scope with no override).
        /// </summary>
        private Guid? EffectiveTenantId(Guid? overrideId) => overrideId ?? ScopeTenantId;

        /// <summary>
        /// Returns " AND alias.TenantId = @_TenantId " when scoping is active, else "".
        /// </summary>
        private string TenantFilter(string alias, Guid? overrideId = null) =>
            EffectiveTenantId(overrideId).HasValue ? $" AND {alias}.TenantId = @_TenantId " : string.Empty;

        /// <summary>
        /// Adds the @_TenantId parameter to a DynamicParameters bag when scoping is active.
        /// </summary>
        private void AddTenantParam(DynamicParameters p, Guid? overrideId = null)
        {
            var id = EffectiveTenantId(overrideId);
            if (id.HasValue) p.Add("_TenantId", id.Value);
        }

        /// <summary>
        /// Gets a user by ID
        /// </summary>
        /// <param name="id">The user ID</param>
        /// <returns>The user if found, null otherwise</returns>
        public async Task<ScimUser?> GetByIdAsync(Guid id)
        {
            var sql = $@"
                SELECT * FROM Users WHERE Id = @UserId {TenantFilter("Users")};
                SELECT * FROM UserEmails WHERE UserId = @UserId;
                SELECT * FROM UserPhoneNumbers WHERE UserId = @UserId;
                SELECT * FROM UserAddresses WHERE UserId = @UserId;
                SELECT g.Id AS Value, g.DisplayName AS Display, 'direct' AS Type
                  FROM GroupMembers gm
                  JOIN Groups g ON gm.GroupId = g.Id
                 WHERE gm.Value = @UserId {TenantFilter("g")};
                SELECT Value, Display, Type, [Primary] FROM UserRoles WHERE UserId = @UserId;
                SELECT * FROM CustomAttributeValues WHERE ResourceId = @UserId;";

            var p = new DynamicParameters();
            p.Add("UserId", id);
            AddTenantParam(p);

            using var connection = CreateConnection();
            using var multi = await connection.QueryMultipleAsync(sql, p);

            var user = await multi.ReadSingleOrDefaultAsync<dynamic>();
            if (user == null) return null;

            var scimUser = MapToScimUser(user);

            // Load related data
            scimUser.Emails = (await multi.ReadAsync<ScimEmail>()).ToList();
            scimUser.PhoneNumbers = (await multi.ReadAsync<ScimPhoneNumber>()).ToList();
            scimUser.Addresses = (await multi.ReadAsync<ScimAddress>()).ToList();
            scimUser.Groups = (await multi.ReadAsync<ScimGroupRef>()).ToList();

            var roles = await multi.ReadAsync<dynamic>();
            scimUser.Roles = roles.Select(r => new ScimRole
            {
                Value = r.Value,
                Display = r.Display,
                Type = r.Type,
                Primary = r.Primary
            }).ToList();

            // Load custom attributes
            var customAttrs = await multi.ReadAsync<dynamic>();
            // Process custom attributes based on schema

            await ResolveManagerDisplayNamesAsync(connection, new List<ScimUser> { scimUser });

            return scimUser;
        }

        /// <summary>
        /// Gets a user by username
        /// </summary>
        /// <param name="userName">The username</param>
        /// <returns>The user if found, null otherwise</returns>
        public async Task<ScimUser?> GetByUserNameAsync(string userName)
        {
            var userMatch = $"(SELECT Id FROM Users WHERE UserName = @UserName {TenantFilter("Users")})";
            var sql = $@"
                SELECT * FROM Users WHERE UserName = @UserName {TenantFilter("Users")};
                SELECT * FROM UserEmails WHERE UserId = {userMatch};
                SELECT * FROM UserPhoneNumbers WHERE UserId = {userMatch};
                SELECT * FROM UserAddresses WHERE UserId = {userMatch};";

            var p = new DynamicParameters();
            p.Add("UserName", userName);
            AddTenantParam(p);

            using var connection = CreateConnection();
            using var multi = await connection.QueryMultipleAsync(sql, p);

            var user = await multi.ReadSingleOrDefaultAsync<dynamic>();
            if (user == null) return null;

            var scimUser = MapToScimUser(user);
            scimUser.Emails = (await multi.ReadAsync<ScimEmail>()).ToList();
            scimUser.PhoneNumbers = (await multi.ReadAsync<ScimPhoneNumber>()).ToList();
            scimUser.Addresses = (await multi.ReadAsync<ScimAddress>()).ToList();

            return scimUser;
        }

        /// <summary>
        /// Gets all users with pagination, optional filter, and batch-loaded related data
        /// </summary>
        /// <param name="options">Query options</param>
        /// <param name="filterSql">Optional WHERE clause from filter parser (without WHERE keyword)</param>
        /// <param name="filterParams">Optional Dapper parameters for the filter</param>
        /// <returns>List of users and total count</returns>
        public async Task<(List<ScimUser> Users, int TotalCount)> GetAllAsync(
            ScimQueryOptions options,
            string? filterSql = null,
            DynamicParameters? filterParams = null,
            Guid? tenantIdOverride = null)
        {
            var whereClause = "WHERE 1=1" + TenantFilter("u", tenantIdOverride);
            if (!string.IsNullOrWhiteSpace(filterSql))
            {
                whereClause += $" AND ({filterSql})";
            }

            // Use alias 'u' for Users table to match filter SQL column references
            var sql = $@"
                SELECT COUNT(*) FROM Users u {whereClause};
                SELECT u.* FROM Users u
                {whereClause}
                ORDER BY u.UserName
                OFFSET @Skip ROWS FETCH NEXT @Count ROWS ONLY;";

            var parameters = new DynamicParameters();
            parameters.Add("Skip", options.Skip);
            parameters.Add("Count", options.Count);
            AddTenantParam(parameters, tenantIdOverride);
            if (filterParams != null)
            {
                parameters.AddDynamicParams(filterParams);
            }

            using var connection = CreateConnection();
            using var multi = await connection.QueryMultipleAsync(sql, parameters);

            var totalCount = await multi.ReadSingleAsync<int>();
            var users = await multi.ReadAsync<dynamic>();

            var scimUsers = users.Select(u => (ScimUser)MapToScimUser(u)).ToList();

            // Batch-load related data instead of N+1 queries
            if (scimUsers.Count > 0)
            {
                var userIds = scimUsers.Select(u => Guid.Parse(u.Id)).ToList();
                await BatchLoadRelatedDataAsync(connection, scimUsers, userIds);
                await ResolveManagerDisplayNamesAsync(connection, scimUsers);
            }

            return (scimUsers, totalCount);
        }

        /// <summary>
        /// Walks the mapped users, collects unique manager Ids, and patches DisplayName
        /// onto each Manager reference in a single round-trip.
        /// </summary>
        private static async Task ResolveManagerDisplayNamesAsync(IDbConnection connection, List<ScimUser> users)
        {
            var managerIds = users
                .Where(u => u.EnterpriseExtension?.Manager?.Value is { } v && Guid.TryParse(v, out _))
                .Select(u => Guid.Parse(u.EnterpriseExtension!.Manager!.Value!))
                .Distinct()
                .ToList();

            if (managerIds.Count == 0) return;

            var rows = await connection.QueryAsync<(Guid Id, string? DisplayName, string UserName)>(
                "SELECT Id, DisplayName, UserName FROM Users WHERE Id IN @Ids",
                new { Ids = managerIds });
            var byId = rows.ToDictionary(r => r.Id, r => r);

            foreach (var u in users)
            {
                var mgr = u.EnterpriseExtension?.Manager;
                if (mgr?.Value is null) continue;
                if (!Guid.TryParse(mgr.Value, out var mid)) continue;
                if (byId.TryGetValue(mid, out var row))
                {
                    mgr.DisplayName = !string.IsNullOrWhiteSpace(row.DisplayName) ? row.DisplayName : row.UserName;
                }
            }
        }

        /// <summary>
        /// Batch-loads emails, phone numbers, and addresses for a list of users
        /// </summary>
        private async Task BatchLoadRelatedDataAsync(IDbConnection connection, List<ScimUser> users, List<Guid> userIds)
        {
            var batchSql = @"
                SELECT * FROM UserEmails WHERE UserId IN @UserIds;
                SELECT * FROM UserPhoneNumbers WHERE UserId IN @UserIds;
                SELECT * FROM UserAddresses WHERE UserId IN @UserIds;";

            using var multi = await connection.QueryMultipleAsync(batchSql, new { UserIds = userIds });

            var allEmails = (await multi.ReadAsync<dynamic>()).GroupBy(e => (Guid)e.UserId).ToDictionary(g => g.Key, g => g.ToList());
            var allPhones = (await multi.ReadAsync<dynamic>()).GroupBy(p => (Guid)p.UserId).ToDictionary(g => g.Key, g => g.ToList());
            var allAddresses = (await multi.ReadAsync<dynamic>()).GroupBy(a => (Guid)a.UserId).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var user in users)
            {
                var uid = Guid.Parse(user.Id);

                if (allEmails.TryGetValue(uid, out var emails))
                {
                    user.Emails = emails.Select(e => new ScimEmail
                    {
                        Value = e.Value,
                        Type = e.Type,
                        Primary = e.Primary,
                        Display = e.Display
                    }).ToList();
                }

                if (allPhones.TryGetValue(uid, out var phones))
                {
                    user.PhoneNumbers = phones.Select(p => new ScimPhoneNumber
                    {
                        Value = p.Value,
                        Type = p.Type,
                        Primary = p.Primary,
                        Display = p.Display
                    }).ToList();
                }

                if (allAddresses.TryGetValue(uid, out var addresses))
                {
                    user.Addresses = addresses.Select(a => new ScimAddress
                    {
                        Formatted = a.Formatted,
                        StreetAddress = a.StreetAddress,
                        Locality = a.Locality,
                        Region = a.Region,
                        PostalCode = a.PostalCode,
                        Country = a.Country,
                        Type = a.Type,
                        Primary = a.Primary
                    }).ToList();
                }
            }
        }

        /// <summary>
        /// Creates a new user under the context's tenant (or the Default tenant when none).
        /// </summary>
        public Task<ScimUser> CreateAsync(ScimUser user) => CreateAsync(user, tenantOverride: null);

        /// <summary>
        /// Creates a new user under an explicit tenant — used by background tasks
        /// (e.g. data generation) that don't have an HTTP-scoped TenantContext.
        /// </summary>
        public async Task<ScimUser> CreateAsync(ScimUser user, Guid? tenantOverride)
        {
            var id = Guid.NewGuid();
            user.Id = id.ToString();
            user.Meta.Created = DateTime.UtcNow;
            user.Meta.LastModified = DateTime.UtcNow;
            user.Meta.Version = "1";
            var insertTenantId = tenantOverride ?? InsertTenantId;

            var sql = @"
                INSERT INTO Users (
                    Id, TenantId, ExternalId, UserName, Active, Created, LastModified, Version,
                    FormattedName, FamilyName, GivenName, MiddleName, HonorificPrefix, HonorificSuffix,
                    DisplayName, NickName, ProfileUrl, Title, UserType, PreferredLanguage, Locale, Timezone,
                    EmployeeNumber, CostCenter, Organization, Division, Department, ManagerId
                ) VALUES (
                    @Id, @TenantId, @ExternalId, @UserName, @Active, @Created, @LastModified, @Version,
                    @FormattedName, @FamilyName, @GivenName, @MiddleName, @HonorificPrefix, @HonorificSuffix,
                    @DisplayName, @NickName, @ProfileUrl, @Title, @UserType, @PreferredLanguage, @Locale, @Timezone,
                    @EmployeeNumber, @CostCenter, @Organization, @Division, @Department, @ManagerId
                );";

            using var connection = CreateConnection();
            using var transaction = connection.BeginTransaction();

            try
            {
                await connection.ExecuteAsync(sql, new
                {
                    Id = id,
                    TenantId = insertTenantId,
                    user.ExternalId,
                    user.UserName,
                    user.Active,
                    user.Meta.Created,
                    user.Meta.LastModified,
                    Version = 1,
                    FormattedName = user.Name?.Formatted,
                    FamilyName = user.Name?.FamilyName,
                    GivenName = user.Name?.GivenName,
                    MiddleName = user.Name?.MiddleName,
                    HonorificPrefix = user.Name?.HonorificPrefix,
                    HonorificSuffix = user.Name?.HonorificSuffix,
                    user.DisplayName,
                    user.NickName,
                    user.ProfileUrl,
                    user.Title,
                    user.UserType,
                    user.PreferredLanguage,
                    user.Locale,
                    user.Timezone,
                    EmployeeNumber = user.EnterpriseExtension?.EmployeeNumber,
                    CostCenter = user.EnterpriseExtension?.CostCenter,
                    Organization = user.EnterpriseExtension?.Organization,
                    Division = user.EnterpriseExtension?.Division,
                    Department = user.EnterpriseExtension?.Department,
                    ManagerId = (user.EnterpriseExtension?.Manager?.Value is { } mv && Guid.TryParse(mv, out var mgid))
                        ? mgid : (Guid?)null
                }, transaction);

                // Insert related data
                await InsertUserEmailsAsync(connection, transaction, id, user.Emails);
                await InsertUserPhoneNumbersAsync(connection, transaction, id, user.PhoneNumbers);
                await InsertUserAddressesAsync(connection, transaction, id, user.Addresses);

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            return await GetByIdAsync(id) ?? user;
        }

        /// <summary>
        /// Updates an existing user
        /// </summary>
        /// <param name="id">The user ID</param>
        /// <param name="user">The updated user data</param>
        /// <returns>The updated user</returns>
        public async Task<ScimUser?> UpdateAsync(Guid id, ScimUser user)
        {
            user.Meta.LastModified = DateTime.UtcNow;

            var sql = $@"
                UPDATE Users SET
                    ExternalId = @ExternalId,
                    UserName = @UserName,
                    Active = @Active,
                    LastModified = @LastModified,
                    Version = Version + 1,
                    FormattedName = @FormattedName,
                    FamilyName = @FamilyName,
                    GivenName = @GivenName,
                    MiddleName = @MiddleName,
                    HonorificPrefix = @HonorificPrefix,
                    HonorificSuffix = @HonorificSuffix,
                    DisplayName = @DisplayName,
                    NickName = @NickName,
                    ProfileUrl = @ProfileUrl,
                    Title = @Title,
                    UserType = @UserType,
                    PreferredLanguage = @PreferredLanguage,
                    Locale = @Locale,
                    Timezone = @Timezone,
                    EmployeeNumber = @EmployeeNumber,
                    CostCenter = @CostCenter,
                    Organization = @Organization,
                    Division = @Division,
                    Department = @Department,
                    ManagerId = @ManagerId
                WHERE Id = @Id {TenantFilter("Users")};";

            var updateParams = new DynamicParameters();
            updateParams.Add("Id", id);
            updateParams.Add("ExternalId", user.ExternalId);
            updateParams.Add("UserName", user.UserName);
            updateParams.Add("Active", user.Active);
            updateParams.Add("LastModified", user.Meta.LastModified);
            updateParams.Add("FormattedName", user.Name?.Formatted);
            updateParams.Add("FamilyName", user.Name?.FamilyName);
            updateParams.Add("GivenName", user.Name?.GivenName);
            updateParams.Add("MiddleName", user.Name?.MiddleName);
            updateParams.Add("HonorificPrefix", user.Name?.HonorificPrefix);
            updateParams.Add("HonorificSuffix", user.Name?.HonorificSuffix);
            updateParams.Add("DisplayName", user.DisplayName);
            updateParams.Add("NickName", user.NickName);
            updateParams.Add("ProfileUrl", user.ProfileUrl);
            updateParams.Add("Title", user.Title);
            updateParams.Add("UserType", user.UserType);
            updateParams.Add("PreferredLanguage", user.PreferredLanguage);
            updateParams.Add("Locale", user.Locale);
            updateParams.Add("Timezone", user.Timezone);
            updateParams.Add("EmployeeNumber", user.EnterpriseExtension?.EmployeeNumber);
            updateParams.Add("CostCenter", user.EnterpriseExtension?.CostCenter);
            updateParams.Add("Organization", user.EnterpriseExtension?.Organization);
            updateParams.Add("Division", user.EnterpriseExtension?.Division);
            updateParams.Add("Department", user.EnterpriseExtension?.Department);
            updateParams.Add("ManagerId",
                (user.EnterpriseExtension?.Manager?.Value is { } mv2 && Guid.TryParse(mv2, out var mgid2))
                    ? mgid2 : (Guid?)null);
            AddTenantParam(updateParams);

            using var connection = CreateConnection();
            using var transaction = connection.BeginTransaction();

            try
            {
                var rowsAffected = await connection.ExecuteAsync(sql, updateParams, transaction);

                if (rowsAffected == 0)
                {
                    transaction.Rollback();
                    return null;
                }

                // Update related data
                await UpdateUserEmailsAsync(connection, transaction, id, user.Emails);
                await UpdateUserPhoneNumbersAsync(connection, transaction, id, user.PhoneNumbers);
                await UpdateUserAddressesAsync(connection, transaction, id, user.Addresses);

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            return await GetByIdAsync(id);
        }

        /// <summary>
        /// Deletes a user
        /// </summary>
        /// <param name="id">The user ID</param>
        /// <returns>True if deleted, false if not found</returns>
        public async Task<bool> DeleteAsync(Guid id)
        {
            // Foreign-key cleanup so the DELETE doesn't trip FK_Groups_Owner / Users.ManagerId
            // self-FK / GroupMembers.Value. Wrapped in a transaction so a mid-step failure
            // doesn't leave the row partially detached. Portal admin accounts live in
            // PortalAdmins (migration v10) so they cannot end up here.
            var p = new DynamicParameters();
            p.Add("Id", id);
            AddTenantParam(p);

            // CreateConnection() already opens the connection — don't call Open() again
            // or SqlConnection throws "connection's current state is open."
            using var connection = CreateConnection();
            using var tx = connection.BeginTransaction();
            try
            {
                // 1. Drop group memberships where this user was a member.
                await connection.ExecuteAsync(
                    "DELETE FROM GroupMembers WHERE Value = @Id",
                    p, tx);

                // 2. Clear group ownership pointing at this user.
                await connection.ExecuteAsync(
                    "UPDATE Groups SET OwnerId = NULL WHERE OwnerId = @Id",
                    p, tx);

                // 3. Clear manager pointers on other users that reported to this one.
                await connection.ExecuteAsync(
                    "UPDATE Users SET ManagerId = NULL WHERE ManagerId = @Id",
                    p, tx);

                // 4. Now safe to delete the user.
                var rowsAffected = await connection.ExecuteAsync(
                    $"DELETE FROM Users WHERE Id = @Id {TenantFilter("Users")}",
                    p, tx);

                tx.Commit();
                return rowsAffected > 0;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Maps database user to SCIM user
        /// </summary>
        private ScimUser MapToScimUser(dynamic user)
        {
            var scimUser = new ScimUser
            {
                Id = user.Id.ToString(),
                ExternalId = user.ExternalId,
                UserName = user.UserName,
                Active = user.Active,
                DisplayName = user.DisplayName,
                NickName = user.NickName,
                ProfileUrl = user.ProfileUrl,
                Title = user.Title,
                UserType = user.UserType,
                PreferredLanguage = user.PreferredLanguage,
                Locale = user.Locale,
                Timezone = user.Timezone
            };

            scimUser.Meta.Created = user.Created;
            scimUser.Meta.LastModified = user.LastModified;
            scimUser.Meta.Version = user.Version?.ToString();

            if (user.GivenName != null || user.FamilyName != null)
            {
                scimUser.Name = new ScimName
                {
                    Formatted = user.FormattedName,
                    FamilyName = user.FamilyName,
                    GivenName = user.GivenName,
                    MiddleName = user.MiddleName,
                    HonorificPrefix = user.HonorificPrefix,
                    HonorificSuffix = user.HonorificSuffix
                };
            }

            if (user.EmployeeNumber != null || user.Department != null || user.ManagerId != null
                || user.CostCenter != null || user.Organization != null || user.Division != null)
            {
                scimUser.EnterpriseExtension = new ScimEnterpriseUser
                {
                    EmployeeNumber = user.EmployeeNumber,
                    CostCenter = user.CostCenter,
                    Organization = user.Organization,
                    Division = user.Division,
                    Department = user.Department
                };

                if (user.ManagerId != null)
                {
                    // DisplayName is filled in by ResolveManagerDisplayNamesAsync after batch mapping.
                    scimUser.EnterpriseExtension.Manager = new ScimManager
                    {
                        Value = user.ManagerId.ToString()
                    };
                }
            }

            return scimUser;
        }

        /// <summary>
        /// Inserts user emails
        /// </summary>
        private async Task InsertUserEmailsAsync(IDbConnection connection, IDbTransaction transaction,
            Guid userId, List<ScimEmail> emails)
        {
            if (emails.Count == 0) return;

            var sql = @"
                INSERT INTO UserEmails (UserId, Value, Type, [Primary], Display)
                VALUES (@UserId, @Value, @Type, @Primary, @Display)";

            foreach (var email in emails)
            {
                await connection.ExecuteAsync(sql, new
                {
                    UserId = userId,
                    email.Value,
                    email.Type,
                    email.Primary,
                    email.Display
                }, transaction);
            }
        }

        /// <summary>
        /// Inserts user phone numbers
        /// </summary>
        private async Task InsertUserPhoneNumbersAsync(IDbConnection connection, IDbTransaction transaction,
            Guid userId, List<ScimPhoneNumber> phoneNumbers)
        {
            if (phoneNumbers.Count == 0) return;

            var sql = @"
                INSERT INTO UserPhoneNumbers (UserId, Value, Type, [Primary], Display)
                VALUES (@UserId, @Value, @Type, @Primary, @Display)";

            foreach (var phone in phoneNumbers)
            {
                await connection.ExecuteAsync(sql, new
                {
                    UserId = userId,
                    phone.Value,
                    phone.Type,
                    phone.Primary,
                    phone.Display
                }, transaction);
            }
        }

        /// <summary>
        /// Inserts user addresses
        /// </summary>
        private async Task InsertUserAddressesAsync(IDbConnection connection, IDbTransaction transaction,
            Guid userId, List<ScimAddress> addresses)
        {
            if (addresses.Count == 0) return;

            var sql = @"
                INSERT INTO UserAddresses (UserId, Type, StreetAddress, Locality, Region, PostalCode, Country, Formatted, [Primary])
                VALUES (@UserId, @Type, @StreetAddress, @Locality, @Region, @PostalCode, @Country, @Formatted, @Primary)";

            foreach (var address in addresses)
            {
                await connection.ExecuteAsync(sql, new
                {
                    UserId = userId,
                    address.Type,
                    address.StreetAddress,
                    address.Locality,
                    address.Region,
                    address.PostalCode,
                    address.Country,
                    address.Formatted,
                    address.Primary
                }, transaction);
            }
        }

        /// <summary>
        /// Updates user emails
        /// </summary>
        private async Task UpdateUserEmailsAsync(IDbConnection connection, IDbTransaction transaction,
            Guid userId, List<ScimEmail> emails)
        {
            // Delete existing emails
            await connection.ExecuteAsync("DELETE FROM UserEmails WHERE UserId = @UserId",
                new { UserId = userId }, transaction);

            // Insert new emails
            await InsertUserEmailsAsync(connection, transaction, userId, emails);
        }

        /// <summary>
        /// Updates user phone numbers
        /// </summary>
        private async Task UpdateUserPhoneNumbersAsync(IDbConnection connection, IDbTransaction transaction,
            Guid userId, List<ScimPhoneNumber> phoneNumbers)
        {
            // Delete existing phone numbers
            await connection.ExecuteAsync("DELETE FROM UserPhoneNumbers WHERE UserId = @UserId",
                new { UserId = userId }, transaction);

            // Insert new phone numbers
            await InsertUserPhoneNumbersAsync(connection, transaction, userId, phoneNumbers);
        }

        /// <summary>
        /// Updates user addresses
        /// </summary>
        private async Task UpdateUserAddressesAsync(IDbConnection connection, IDbTransaction transaction,
            Guid userId, List<ScimAddress> addresses)
        {
            // Delete existing addresses
            await connection.ExecuteAsync("DELETE FROM UserAddresses WHERE UserId = @UserId",
                new { UserId = userId }, transaction);

            // Insert new addresses
            await InsertUserAddressesAsync(connection, transaction, userId, addresses);
        }
    }
}
