using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace Conduit.DataAccess.Repositories;

/// <summary>
/// Per-(project, sink, externalId) content-hash cache backing the orchestrator's
/// sink-side skip-unchanged path. NOT an Objects lake — it stores only a SHA-256
/// fingerprint of the last successfully written sink payload so an incremental
/// run can avoid re-pushing records that map to a byte-identical payload.
///
/// Load-once: the orchestrator pulls the whole (project, sink) map at run start
/// into a dictionary and reuses it across the hot loop — no per-record SELECT.
/// </summary>
public class SinkRecordHashRepository : BaseRepository
{
    public SinkRecordHashRepository(DatabaseConfig config) : base(config) { }

    /// <summary>
    /// Load every known (ExternalId → ContentHash) for one project+sink. Ordinal
    /// comparer because ExternalIds are case-sensitive opaque keys (DN, GUID, UPN).
    /// </summary>
    public async Task<Dictionary<string, string>> LoadMapAsync(Guid syncProjectId, Guid sinkTenantId)
    {
        const string sql = @"
SELECT ExternalId, ContentHash FROM SinkRecordHashes
 WHERE SyncProjectId = @syncProjectId AND SinkTenantId = @sinkTenantId;";
        var rows = await QueryAsync<(string ExternalId, string ContentHash)>(
            sql, new { syncProjectId, sinkTenantId });
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rows) map[r.ExternalId] = r.ContentHash;
        return map;
    }

    /// <summary>
    /// Upsert a batch of (ExternalId, ContentHash) for one project+sink. MERGE
    /// row-by-row inside a single connection + transaction (no TVP dependency).
    /// Called only for records actually written this run, so the volume is bounded
    /// by the change set, not the full population.
    /// </summary>
    public async Task UpsertManyAsync(
        Guid syncProjectId,
        Guid sinkTenantId,
        IReadOnlyCollection<KeyValuePair<string, string>> hashes)
    {
        if (hashes.Count == 0) return;

        const string sql = @"
MERGE SinkRecordHashes AS t
USING (SELECT @SyncProjectId AS SyncProjectId, @SinkTenantId AS SinkTenantId, @ExternalId AS ExternalId) AS s
    ON t.SyncProjectId = s.SyncProjectId AND t.SinkTenantId = s.SinkTenantId AND t.ExternalId = s.ExternalId
WHEN MATCHED THEN
    UPDATE SET ContentHash = @ContentHash, UpdatedAt = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (Id, SyncProjectId, SinkTenantId, ExternalId, ContentHash, UpdatedAt)
    VALUES (NEWID(), @SyncProjectId, @SinkTenantId, @ExternalId, @ContentHash, SYSUTCDATETIME());";

        var rows = hashes.Select(h => new
        {
            SyncProjectId = syncProjectId,
            SinkTenantId = sinkTenantId,
            ExternalId = h.Key,
            ContentHash = h.Value
        });

        using var db = (System.Data.Common.DbConnection)CreateConnection();
        await db.OpenAsync();
        using var tx = db.BeginTransaction();
        try
        {
            await db.ExecuteAsync(sql, rows, tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Drop hash rows for objects deleted from the sink, so a later recreate with
    /// the same ExternalId is treated as a genuine change.
    /// </summary>
    public Task DeleteManyAsync(Guid syncProjectId, Guid sinkTenantId, IReadOnlyCollection<string> externalIds)
    {
        if (externalIds.Count == 0) return Task.CompletedTask;
        const string sql = @"
DELETE FROM SinkRecordHashes
 WHERE SyncProjectId = @syncProjectId AND SinkTenantId = @sinkTenantId AND ExternalId IN @externalIds;";
        return ExecuteAsync(sql, new { syncProjectId, sinkTenantId, externalIds });
    }
}
