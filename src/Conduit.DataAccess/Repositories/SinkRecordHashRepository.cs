using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace Conduit.DataAccess.Repositories;

/// <summary>
/// Per-(project, sink, objectClass, externalId) content-hash cache backing the
/// orchestrator's sink-side skip-unchanged path. NOT an Objects lake — it stores
/// only a SHA-256 fingerprint of the last successfully written sink payload so an
/// incremental run can avoid re-pushing records that map to a byte-identical
/// payload.
///
/// V26 per-CLASS scope: one project now pumps N per-class Mapping steps, so every
/// read/write here carries the step's object class. Rows with a NULL ObjectClass
/// are LEGACY (written before V26): they match ANY class on read and are rewritten
/// with the real class on their next upsert, so the registry converges without a
/// destructive reset.
///
/// Load-once: the orchestrator pulls the whole (project, sink, class) map at run
/// start into a dictionary and reuses it across the hot loop — no per-record SELECT.
/// </summary>
/// <summary>
/// One prior-run registry entry: the stored content hash plus when the record
/// was last actually accepted by the sink (UpdatedAt, UTC). The timestamp backs
/// the orchestrator's bounded-refresh TTL for volatile-hash sources: a record
/// whose hash is excluded from volatile freshness attributes (SQL Discovery's
/// sqlLastScannedAt etc.) can stay hash-stable forever, so without a periodic
/// forced re-ingest the sink-side freshness column ages and falsely flips Stale.
/// </summary>
public readonly record struct SinkHashEntry(string ContentHash, DateTime UpdatedAt);

public class SinkRecordHashRepository : BaseRepository
{
    public SinkRecordHashRepository(DatabaseConfig config) : base(config) { }

    /// <summary>
    /// Canonical class key: lowercase native class name ("user", "group"). Null or
    /// whitespace collapses to null = legacy/unscoped.
    /// </summary>
    private static string? Canon(string? objectClass) =>
        string.IsNullOrWhiteSpace(objectClass) ? null : objectClass.Trim().ToLowerInvariant();

    /// <summary>
    /// Load every known (ExternalId → ContentHash + UpdatedAt) for one
    /// project+sink+class. UpdatedAt rides along so the orchestrator can apply the
    /// bounded-refresh TTL (see <see cref="SinkHashEntry"/>) without a second query.
    /// Legacy NULL-class rows are included (matched by any class) until rewritten.
    /// Ordinal comparer because ExternalIds are case-sensitive opaque keys
    /// (DN, GUID, UPN).
    /// </summary>
    public async Task<Dictionary<string, SinkHashEntry>> LoadMapAsync(Guid syncProjectId, Guid sinkTenantId, string? objectClass)
    {
        const string sql = @"
SELECT ExternalId, ContentHash, UpdatedAt FROM SinkRecordHashes
 WHERE SyncProjectId = @syncProjectId AND SinkTenantId = @sinkTenantId
   AND (ObjectClass = @objectClass OR ObjectClass IS NULL OR @objectClass IS NULL);";
        var rows = await QueryAsync<(string ExternalId, string ContentHash, DateTime UpdatedAt)>(
            sql, new { syncProjectId, sinkTenantId, objectClass = Canon(objectClass) });
        var map = new Dictionary<string, SinkHashEntry>(StringComparer.Ordinal);
        foreach (var r in rows) map[r.ExternalId] = new SinkHashEntry(r.ContentHash, r.UpdatedAt);
        return map;
    }

    /// <summary>
    /// Wire shape for the OPENJSON set-based upsert below. Short property names
    /// keep the serialized payload small (ExternalIds can be 400-char DNs).
    /// </summary>
    private sealed class HashRow
    {
        public string e { get; set; } = string.Empty;
        public string h { get; set; } = string.Empty;
    }

    /// <summary>
    /// Upsert a batch of (ExternalId, ContentHash) for one project+sink+class.
    /// Set-based: one MERGE per chunk of 1000 rows over an OPENJSON-parsed
    /// parameter (replaces the old per-row MERGE round-trips), inside a single
    /// connection + transaction. Matches the row by (project, sink, ExternalId)
    /// where the stored class equals this class OR is legacy NULL — the update
    /// stamps the real class so legacy rows converge. Called only for records
    /// actually written this run, so the volume is bounded by the change set,
    /// not the full population.
    /// </summary>
    public async Task UpsertManyAsync(
        Guid syncProjectId,
        Guid sinkTenantId,
        string? objectClass,
        IReadOnlyCollection<KeyValuePair<string, string>> hashes)
    {
        if (hashes.Count == 0) return;

        // Collapse duplicate ExternalIds (last-write-wins) BEFORE the MERGE. A single
        // run can surface the same SourceId across two flush batches (paging overlap,
        // or two object classes routed to the same sink/ExternalId scope), so the
        // accumulated set may contain the same key twice. MERGE treats duplicate source
        // keys that are both NOT MATCHED as two INSERTs of the same row and trips
        // UX_SinkRecordHashes_Scope ("Cannot insert duplicate key"). De-duping here
        // keeps the upsert idempotent regardless of the caller's batch shape.
        var deduped = new Dictionary<string, string>(hashes.Count, StringComparer.Ordinal);
        foreach (var kv in hashes)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            deduped[kv.Key] = kv.Value;
        }
        if (deduped.Count == 0) return;

        const string sql = @"
SET QUOTED_IDENTIFIER ON;
MERGE SinkRecordHashes AS t
USING (SELECT j.e AS ExternalId, j.h AS ContentHash
         FROM OPENJSON(@Json) WITH (e NVARCHAR(400) '$.e', h CHAR(44) '$.h') AS j) AS s
    ON t.SyncProjectId = @SyncProjectId
   AND t.SinkTenantId = @SinkTenantId
   AND (t.ObjectClass = @ObjectClass OR t.ObjectClass IS NULL OR @ObjectClass IS NULL)
   AND t.ExternalId = s.ExternalId
WHEN MATCHED THEN
    UPDATE SET ContentHash = s.ContentHash,
               ObjectClass = COALESCE(@ObjectClass, t.ObjectClass),
               UpdatedAt = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (Id, SyncProjectId, SinkTenantId, ObjectClass, ExternalId, ContentHash, UpdatedAt)
    VALUES (NEWID(), @SyncProjectId, @SinkTenantId, @ObjectClass, s.ExternalId, s.ContentHash, SYSUTCDATETIME());";

        var canonClass = Canon(objectClass);

        // CreateConnection() returns an ALREADY-OPEN connection — do not Open() again
        // (double-open throws "The connection was not closed" and no hashes persist).
        using var db = CreateConnection();
        using var tx = db.BeginTransaction();
        try
        {
            foreach (var chunk in Chunk(deduped, 1000))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(
                    chunk.Select(p => new HashRow { e = p.Key, h = p.Value }));
                await db.ExecuteAsync(sql, new
                {
                    Json = json,
                    SyncProjectId = syncProjectId,
                    SinkTenantId = sinkTenantId,
                    ObjectClass = canonClass
                }, tx);
            }
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
    /// the same ExternalId is treated as a genuine change. Scoped to this class
    /// (plus legacy NULL-class rows).
    /// </summary>
    public Task DeleteManyAsync(Guid syncProjectId, Guid sinkTenantId, string? objectClass, IReadOnlyCollection<string> externalIds)
    {
        if (externalIds.Count == 0) return Task.CompletedTask;
        const string sql = @"
DELETE FROM SinkRecordHashes
 WHERE SyncProjectId = @syncProjectId AND SinkTenantId = @sinkTenantId
   AND (ObjectClass = @objectClass OR ObjectClass IS NULL OR @objectClass IS NULL)
   AND ExternalId IN @externalIds;";
        return ExecuteAsync(sql, new { syncProjectId, sinkTenantId, objectClass = Canon(objectClass), externalIds });
    }

    private static IEnumerable<List<KeyValuePair<string, string>>> Chunk(
        IReadOnlyCollection<KeyValuePair<string, string>> source, int size)
    {
        var bucket = new List<KeyValuePair<string, string>>(Math.Min(size, source.Count));
        foreach (var item in source)
        {
            bucket.Add(item);
            if (bucket.Count == size)
            {
                yield return bucket;
                bucket = new List<KeyValuePair<string, string>>(size);
            }
        }
        if (bucket.Count > 0) yield return bucket;
    }
}
