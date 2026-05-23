using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Conduit.Core.Models;
using Conduit.Core.Services;
using Microsoft.Data.SqlClient;

namespace Conduit.DataAccess.Repositories
{
    /// <summary>
    /// Repository for managing SCIM groups
    /// </summary>
    public class GroupRepository : BaseRepository
    {
        private readonly ITenantContext? _tenantContext;

        /// <summary>
        /// Initializes a new instance of the GroupRepository class. Tenant scoping
        /// (UI label: "Connected Systems") is applied automatically when
        /// <paramref name="tenantContext"/> is supplied and the caller is non-Admin.
        /// </summary>
        public GroupRepository(DatabaseConfig databaseConfig, ITenantContext? tenantContext = null)
            : base(databaseConfig)
        {
            _tenantContext = tenantContext;
        }

        private Guid? ScopeTenantId => _tenantContext is { IsAdmin: false } ? _tenantContext.TenantId : null;
        private Guid InsertTenantId => _tenantContext?.TenantId ?? TenantRepository.DefaultTenantId;
        private Guid? EffectiveTenantId(Guid? overrideId) => overrideId ?? ScopeTenantId;
        private string TenantFilter(string alias, Guid? overrideId = null) =>
            EffectiveTenantId(overrideId).HasValue ? $" AND {alias}.TenantId = @_TenantId " : string.Empty;
        private void AddTenantParam(DynamicParameters p, Guid? overrideId = null)
        {
            var id = EffectiveTenantId(overrideId);
            if (id.HasValue) p.Add("_TenantId", id.Value);
        }

        /// <summary>
        /// Gets a group by ID
        /// </summary>
        /// <param name="id">The group ID</param>
        /// <returns>The group if found, null otherwise</returns>
        public async Task<ScimGroup?> GetByIdAsync(Guid id)
        {
            var sql = $@"
                SELECT g.*, u.UserName as OwnerDisplay
                FROM Groups g
                LEFT JOIN Users u ON g.OwnerId = u.Id
                WHERE g.Id = @Id {TenantFilter("g")};

                SELECT gm.*, u.UserName as Display
                FROM GroupMembers gm
                LEFT JOIN Users u ON gm.Value = u.Id
                WHERE gm.GroupId = @Id;";

            var p = new DynamicParameters();
            p.Add("Id", id);
            AddTenantParam(p);

            using var connection = CreateConnection();
            using var multi = await connection.QueryMultipleAsync(sql, p);

            var group = await multi.ReadSingleOrDefaultAsync<dynamic>();
            if (group == null) return null;

            var scimGroup = MapToScimGroup(group);
            var members = await multi.ReadAsync<dynamic>();

            scimGroup.Members = members.Select(m => new ScimGroupMember
            {
                Value = m.Value.ToString(),
                Display = m.Display,
                Type = m.Type
            }).ToList();

            return scimGroup;
        }

        /// <summary>
        /// Gets all groups with pagination, optional filter, and batch-loaded members
        /// </summary>
        /// <param name="options">Query options</param>
        /// <param name="filterSql">Optional WHERE clause from filter parser</param>
        /// <param name="filterParams">Optional Dapper parameters for the filter</param>
        /// <returns>List of groups and total count</returns>
        public async Task<(List<ScimGroup> Groups, int TotalCount)> GetAllAsync(
            ScimQueryOptions options,
            string? filterSql = null,
            DynamicParameters? filterParams = null,
            Guid? tenantIdOverride = null)
        {
            var whereClause = "WHERE 1=1" + TenantFilter("g", tenantIdOverride);
            if (!string.IsNullOrWhiteSpace(filterSql))
            {
                whereClause += $" AND ({filterSql})";
            }

            var sql = $@"
                SELECT COUNT(*) FROM Groups g {whereClause};
                SELECT g.*, u.UserName as OwnerDisplay
                FROM Groups g
                LEFT JOIN Users u ON g.OwnerId = u.Id
                {whereClause}
                ORDER BY g.DisplayName
                OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;";

            var parameters = new DynamicParameters();
            parameters.Add("Offset", options.StartIndex - 1);
            parameters.Add("Limit", options.Count);
            AddTenantParam(parameters, tenantIdOverride);
            if (filterParams != null)
            {
                parameters.AddDynamicParams(filterParams);
            }

            using var connection = CreateConnection();
            using var multi = await connection.QueryMultipleAsync(sql, parameters);

            var totalCount = await multi.ReadSingleAsync<int>();
            var groups = await multi.ReadAsync<dynamic>();

            List<ScimGroup> scimGroups = groups.Select<dynamic, ScimGroup>(g => MapToScimGroup(g)).ToList();

            // Batch-load members for all groups instead of N+1
            if (scimGroups.Count > 0)
            {
                var groupIds = scimGroups.Select(g => Guid.Parse(g.Id)).ToList();
                var membersSql = @"
                    SELECT gm.GroupId, gm.Value, gm.Type, u.UserName as Display
                    FROM GroupMembers gm
                    LEFT JOIN Users u ON gm.Value = u.Id
                    WHERE gm.GroupId IN @GroupIds;";

                var allMembers = await connection.QueryAsync<dynamic>(membersSql, new { GroupIds = groupIds });
                var membersByGroup = allMembers.GroupBy(m => (Guid)m.GroupId).ToDictionary(g => g.Key, g => g.ToList());

                foreach (var group in scimGroups)
                {
                    var gid = Guid.Parse(group.Id);
                    if (membersByGroup.TryGetValue(gid, out var members))
                    {
                        group.Members = members.Select(m => new ScimGroupMember
                        {
                            Value = m.Value.ToString(),
                            Display = m.Display,
                            Type = m.Type
                        }).ToList();
                    }
                }
            }

            return (scimGroups, totalCount);
        }

        /// <summary>
        /// Creates a new group under the context's tenant (or Default when none).
        /// </summary>
        public Task<ScimGroup> CreateAsync(ScimGroup group) => CreateAsync(group, tenantOverride: null);

        /// <summary>
        /// Creates a new group under an explicit tenant — used by background tasks
        /// (e.g. group generation) that don't have an HTTP-scoped TenantContext.
        /// </summary>
        public async Task<ScimGroup> CreateAsync(ScimGroup group, Guid? tenantOverride)
        {
            var id = Guid.NewGuid();
            group.Id = id.ToString();
            group.Meta.Created = DateTime.UtcNow;
            group.Meta.LastModified = DateTime.UtcNow;
            group.Meta.Version = "1";
            var insertTenantId = tenantOverride ?? InsertTenantId;

            var sql = @"
                INSERT INTO Groups (
                    Id, TenantId, DisplayName, Description, Type, OwnerId, Created, LastModified, Version
                ) VALUES (
                    @Id, @TenantId, @DisplayName, @Description, @Type, @OwnerId, @Created, @LastModified, @Version
                );";

            using var connection = CreateConnection();
            using var transaction = connection.BeginTransaction();

            try
            {
                await connection.ExecuteAsync(sql, new
                {
                    Id = id,
                    TenantId = insertTenantId,
                    DisplayName = group.DisplayName,
                    Description = group.Description,
                    Type = group.Type,
                    OwnerId = group.Owner != null ? Guid.Parse(group.Owner.Value) : (Guid?)null,
                    Created = group.Meta.Created,
                    LastModified = group.Meta.LastModified,
                    Version = group.Meta.Version
                }, transaction);

                // Insert members
                if (group.Members?.Any() == true)
                {
                    var membersSql = @"
                        INSERT INTO GroupMembers (GroupId, Value, Type, [Primary])
                        VALUES (@GroupId, @Value, @Type, @Primary);";

                    foreach (var member in group.Members)
                    {
                        await connection.ExecuteAsync(membersSql, new
                        {
                            GroupId = id,
                            Value = Guid.Parse(member.Value),
                            Type = member.Type ?? "User",
                            Primary = false
                        }, transaction);
                    }
                }

                transaction.Commit();
                group.Meta.ResourceType = "Group";
                group.Meta.Location = $"/scim/v2/Groups/{id}";
                return group;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Updates an existing group
        /// </summary>
        /// <param name="id">The group ID</param>
        /// <param name="group">The updated group data</param>
        /// <returns>The updated group</returns>
        public async Task<ScimGroup> UpdateAsync(Guid id, ScimGroup group)
        {
            group.Id = id.ToString();
            group.Meta.LastModified = DateTime.UtcNow;
            var newVersion = (int.Parse(group.Meta.Version ?? "1") + 1).ToString();
            group.Meta.Version = newVersion;

            var sql = $@"
                UPDATE Groups SET
                    DisplayName = @DisplayName,
                    Description = @Description,
                    Type = @Type,
                    OwnerId = @OwnerId,
                    LastModified = @LastModified,
                    Version = @Version
                WHERE Id = @Id {TenantFilter("Groups")};";

            var updateParams = new DynamicParameters();
            updateParams.Add("Id", id);
            updateParams.Add("DisplayName", group.DisplayName);
            updateParams.Add("Description", group.Description);
            updateParams.Add("Type", group.Type);
            updateParams.Add("OwnerId", group.Owner != null ? Guid.Parse(group.Owner.Value) : (Guid?)null);
            updateParams.Add("LastModified", group.Meta.LastModified);
            updateParams.Add("Version", newVersion);
            AddTenantParam(updateParams);

            using var connection = CreateConnection();
            using var transaction = connection.BeginTransaction();

            try
            {
                await connection.ExecuteAsync(sql, updateParams, transaction);

                // Update members - delete all and re-insert
                await connection.ExecuteAsync("DELETE FROM GroupMembers WHERE GroupId = @GroupId",
                    new { GroupId = id }, transaction);

                if (group.Members?.Any() == true)
                {
                    var membersSql = @"
                        INSERT INTO GroupMembers (GroupId, Value, Type, [Primary])
                        VALUES (@GroupId, @Value, @Type, @Primary);";

                    foreach (var member in group.Members)
                    {
                        await connection.ExecuteAsync(membersSql, new
                        {
                            GroupId = id,
                            Value = Guid.Parse(member.Value),
                            Type = member.Type ?? "User",
                            Primary = false
                        }, transaction);
                    }
                }

                transaction.Commit();
                return group;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Deletes a group
        /// </summary>
        /// <param name="id">The group ID</param>
        /// <returns>True if deleted, false if not found</returns>
        public async Task<bool> DeleteAsync(Guid id)
        {
            // The GroupMembers delete is unconditional (no TenantId there) but is gated
            // by the Groups DELETE which IS tenant-scoped — if no group row matches,
            // nothing was removed even if we touched orphan rows in GroupMembers.
            var sql = $@"
                DELETE FROM GroupMembers WHERE GroupId = @Id;
                DELETE FROM Groups WHERE Id = @Id {TenantFilter("Groups")};";

            var p = new DynamicParameters();
            p.Add("Id", id);
            AddTenantParam(p);

            using var connection = CreateConnection();
            var affected = await connection.ExecuteAsync(sql, p);
            return affected > 0;
        }

        /// <summary>
        /// Maps database record to ScimGroup
        /// </summary>
        private ScimGroup MapToScimGroup(dynamic record)
        {
            var group = new ScimGroup
            {
                Id = record.Id.ToString(),
                DisplayName = record.DisplayName,
                Description = record.Description,
                Type = record.Type,
                Members = new List<ScimGroupMember>(),
                Meta = new ScimMetadata
                {
                    ResourceType = "Group",
                    Created = record.Created,
                    LastModified = record.LastModified,
                    Version = record.Version?.ToString(),
                    Location = $"/scim/v2/Groups/{record.Id}"
                }
            };

            // Map owner if present
            if (record.OwnerId != null)
            {
                group.Owner = new ScimGroupMember
                {
                    Value = record.OwnerId.ToString(),
                    Display = record.OwnerDisplay,
                    Type = "User"
                };
            }

            return group;
        }
    }
}
