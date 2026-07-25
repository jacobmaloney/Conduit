using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Conduit.DataAccess.Repositories;

/// <summary>One permitted AD creation base DN for a source connection (schema V33).</summary>
public sealed class CreationBaseDnEntry
{
    public Guid Id { get; set; }
    public string SourceConnectionName { get; set; } = string.Empty;
    public string BaseDn { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>
/// Customer-owned, agent-side store of the permitted AD creation base DNs, keyed by source-connection
/// name — the persistence side of the deny-all containment control. Authoring lives on the Conduit
/// host (Conduit UI + this DB), NEVER pushed from IdentityCenter: whoever controls IC must not be able
/// to widen containment.
/// </summary>
public interface ICreationBaseDnAllowListRepository
{
    /// <summary>The permitted base DNs configured for a connection (empty when none).</summary>
    Task<List<string>> GetBaseDnsAsync(string sourceConnectionName);

    /// <summary>Every configured entry (for the settings UI), ordered by connection then DN.</summary>
    Task<List<CreationBaseDnEntry>> GetAllAsync();

    /// <summary>
    /// Add a permitted base DN for a connection. Idempotent — a duplicate (same name + DN) is a no-op.
    /// Returns true when a row was inserted, false when it already existed.
    /// </summary>
    Task<bool> AddAsync(string sourceConnectionName, string baseDn, string? createdBy);

    /// <summary>Remove a permitted base DN by id. Returns true when a row was deleted.</summary>
    Task<bool> DeleteAsync(Guid id);

    /// <summary>Load a single entry (for the audit trail on delete).</summary>
    Task<CreationBaseDnEntry?> GetByIdAsync(Guid id);
}

/// <summary>Dapper implementation. Schema managed by DatabaseMigrator V33.</summary>
public class CreationBaseDnAllowListRepository : BaseRepository, ICreationBaseDnAllowListRepository
{
    public CreationBaseDnAllowListRepository(DatabaseConfig config) : base(config) { }

    public async Task<List<string>> GetBaseDnsAsync(string sourceConnectionName)
    {
        if (string.IsNullOrWhiteSpace(sourceConnectionName))
            return new List<string>();
        var rows = await QueryAsync<string>(@"
            SELECT BaseDn FROM ProvisioningCreationBaseDns
             WHERE SourceConnectionName = @Name
             ORDER BY BaseDn",
            new { Name = sourceConnectionName });
        return rows.ToList();
    }

    public async Task<List<CreationBaseDnEntry>> GetAllAsync()
    {
        var rows = await QueryAsync<CreationBaseDnEntry>(@"
            SELECT Id, SourceConnectionName, BaseDn, CreatedAt, CreatedBy
              FROM ProvisioningCreationBaseDns
             ORDER BY SourceConnectionName, BaseDn");
        return rows.ToList();
    }

    public async Task<bool> AddAsync(string sourceConnectionName, string baseDn, string? createdBy)
    {
        if (string.IsNullOrWhiteSpace(sourceConnectionName))
            throw new ArgumentException("sourceConnectionName is required.", nameof(sourceConnectionName));
        if (string.IsNullOrWhiteSpace(baseDn))
            throw new ArgumentException("baseDn is required.", nameof(baseDn));

        // Idempotent insert — the (name, DN) pair is unique by intent but the column is too long for a
        // UNIQUE index key, so dedupe here with INSERT..WHERE NOT EXISTS.
        var affected = await ExecuteAsync(@"
            INSERT INTO ProvisioningCreationBaseDns (Id, SourceConnectionName, BaseDn, CreatedAt, CreatedBy)
            SELECT @Id, @Name, @BaseDn, SYSUTCDATETIME(), @CreatedBy
            WHERE NOT EXISTS (
                SELECT 1 FROM ProvisioningCreationBaseDns
                 WHERE SourceConnectionName = @Name AND BaseDn = @BaseDn)",
            new { Id = Guid.NewGuid(), Name = sourceConnectionName.Trim(), BaseDn = baseDn.Trim(), CreatedBy = createdBy });
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var affected = await ExecuteAsync(
            "DELETE FROM ProvisioningCreationBaseDns WHERE Id = @Id",
            new { Id = id });
        return affected > 0;
    }

    public Task<CreationBaseDnEntry?> GetByIdAsync(Guid id) =>
        QuerySingleOrDefaultAsync<CreationBaseDnEntry>(@"
            SELECT Id, SourceConnectionName, BaseDn, CreatedAt, CreatedBy
              FROM ProvisioningCreationBaseDns
             WHERE Id = @Id",
            new { Id = id });
}
