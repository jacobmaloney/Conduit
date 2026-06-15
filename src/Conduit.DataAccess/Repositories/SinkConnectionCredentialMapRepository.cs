using System;
using System.Threading.Tasks;

namespace Conduit.DataAccess.Repositories;

/// <summary>
/// Thrown when a SinkConnectionCredentialMap upsert would re-point an existing
/// source-connection name at a DIFFERENT Conduit tenant. One source name MUST
/// resolve to exactly one tenant; silently overwriting it would mis-route a
/// privileged write-back credential. The caller logs this and refuses the change.
/// </summary>
public sealed class SinkConnectionCredentialMapCollisionException : Exception
{
    public string SourceConnectionName { get; }
    public Guid ExistingTenantId { get; }
    public Guid AttemptedTenantId { get; }

    public SinkConnectionCredentialMapCollisionException(
        string sourceConnectionName, Guid existingTenantId, Guid attemptedTenantId)
        : base($"Source connection name '{sourceConnectionName}' is already mapped to tenant {existingTenantId}; refusing to re-point it to {attemptedTenantId}.")
    {
        SourceConnectionName = sourceConnectionName;
        ExistingTenantId = existingTenantId;
        AttemptedTenantId = attemptedTenantId;
    }
}

/// <summary>
/// Maps an IdentityCenter source-connection name (the sanitized value IC stores as
/// DirectoryConnections.Name) to the Conduit source Tenant whose credential backs
/// form-driven write-back. The orchestrator is the ONLY writer — it self-populates
/// this from its own trusted SourceTenant whenever a sync runs to an IC sink. The
/// AD/SQL agent-write executors resolve name → ConduitTenantId here, then read the
/// credential by that tenant id exactly as before.
/// </summary>
public class SinkConnectionCredentialMapRepository : BaseRepository
{
    public SinkConnectionCredentialMapRepository(DatabaseConfig config) : base(config) { }

    public sealed class MapRow
    {
        public Guid Id { get; set; }
        public string SourceConnectionName { get; set; } = string.Empty;
        public Guid ConduitTenantId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastModified { get; set; }
    }

    /// <summary>
    /// Register or refresh the mapping for <paramref name="sourceConnectionName"/>.
    /// If a row already exists for the name pointing at a DIFFERENT tenant, this is a
    /// COLLISION: it throws <see cref="SinkConnectionCredentialMapCollisionException"/>
    /// rather than silently re-pointing the credential. Same tenant → bump LastModified.
    /// Absent → insert.
    /// </summary>
    public async Task UpsertAsync(string sourceConnectionName, Guid conduitTenantId)
    {
        if (string.IsNullOrWhiteSpace(sourceConnectionName))
            throw new ArgumentException("sourceConnectionName is required.", nameof(sourceConnectionName));
        if (conduitTenantId == Guid.Empty)
            throw new ArgumentException("conduitTenantId is required.", nameof(conduitTenantId));

        var existing = await QuerySingleOrDefaultAsync<MapRow>(@"
            SELECT * FROM SinkConnectionCredentialMap
             WHERE SourceConnectionName = @Name",
            new { Name = sourceConnectionName });

        if (existing is not null)
        {
            if (existing.ConduitTenantId != conduitTenantId)
                throw new SinkConnectionCredentialMapCollisionException(
                    sourceConnectionName, existing.ConduitTenantId, conduitTenantId);

            await ExecuteAsync(@"
                UPDATE SinkConnectionCredentialMap
                   SET LastModified = @Now
                 WHERE SourceConnectionName = @Name",
                new { Name = sourceConnectionName, Now = DateTime.UtcNow });
            return;
        }

        await ExecuteAsync(@"
            INSERT INTO SinkConnectionCredentialMap (Id, SourceConnectionName, ConduitTenantId, CreatedAt, LastModified)
            VALUES (@Id, @Name, @TenantId, @Now, @Now)",
            new { Id = Guid.NewGuid(), Name = sourceConnectionName, TenantId = conduitTenantId, Now = DateTime.UtcNow });
    }

    /// <summary>
    /// Resolve a source-connection name to its Conduit tenant id, or null when no
    /// mapping exists (the executor fails closed on null).
    /// </summary>
    public Task<Guid?> GetTenantIdByNameAsync(string sourceConnectionName) =>
        QuerySingleOrDefaultAsync<Guid?>(@"
            SELECT ConduitTenantId FROM SinkConnectionCredentialMap
             WHERE SourceConnectionName = @Name",
            new { Name = sourceConnectionName });

    /// <summary>Revocation: drop every mapping pointing at a tenant (e.g. tenant deleted).</summary>
    public Task DeleteByTenantAsync(Guid conduitTenantId) =>
        ExecuteAsync(@"
            DELETE FROM SinkConnectionCredentialMap
             WHERE ConduitTenantId = @TenantId",
            new { TenantId = conduitTenantId });
}
