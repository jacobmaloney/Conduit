using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Conduit.Core.SyncModels;

namespace Conduit.DataAccess.Repositories;

/// <summary>
/// SyncRuns + SyncRunLogs. Runs are append-only at create time; updates only
/// flip Status + counters at the end. Logs are append-only forever.
/// </summary>
public class SyncRunRepository : BaseRepository
{
    public SyncRunRepository(DatabaseConfig config) : base(config) { }

    public Task<SyncRun> CreateAsync(SyncRun run)
    {
        if (run.Id == Guid.Empty) run.Id = Guid.NewGuid();
        return InsertAsync(run);
    }

    private async Task<SyncRun> InsertAsync(SyncRun run)
    {
        const string sql = @"
            INSERT INTO SyncRuns
                (Id, WorkspaceId, SyncProjectId, Status, TriggeredBy, StartedAt, CompletedAt,
                 DurationMs, ObjectsRead, ObjectsCreated, ObjectsUpdated, ObjectsSkipped,
                 ObjectsFailed, ErrorMessage, [Cursor], IsIncremental)
            VALUES
                (@Id, @WorkspaceId, @SyncProjectId, @Status, @TriggeredBy, @StartedAt, @CompletedAt,
                 @DurationMs, @ObjectsRead, @ObjectsCreated, @ObjectsUpdated, @ObjectsSkipped,
                 @ObjectsFailed, @ErrorMessage, @Cursor, @IsIncremental);";
        await ExecuteAsync(sql, run);
        return run;
    }

    /// <summary>
    /// Phase 2: most-recent successful run for a project. The orchestrator reads
    /// this at the start of the next run to recover the source cursor.
    /// </summary>
    public Task<SyncRun?> GetLastSuccessfulAsync(Guid projectId) =>
        QuerySingleOrDefaultAsync<SyncRun>(@"
            SELECT TOP 1 * FROM SyncRuns
             WHERE SyncProjectId = @ProjectId
               AND Status = 'Succeeded'
             ORDER BY StartedAt DESC",
            new { ProjectId = projectId });

    /// <summary>
    /// Phase 6. Given a candidate set of TenantIds (typically all IC tenants),
    /// returns the one whose latest SyncRun (as Source OR Sink) started most
    /// recently. Used by SyncProjects' "default sink to IC" pick when there's
    /// more than one IC tenant — beats alphabetical-first because the operator
    /// is almost certainly working in the IC they last touched. Returns null
    /// when none of the candidates ever participated in a run.
    /// </summary>
    public Task<Guid?> GetMostRecentlyActiveTenantIdAsync(IEnumerable<Guid> candidateTenantIds) =>
        QuerySingleOrDefaultAsync<Guid?>(@"
            SELECT TOP 1 t.TenantId
              FROM (
                    SELECT sp.SourceTenantId AS TenantId, sr.StartedAt
                      FROM SyncRuns sr
                      JOIN SyncProjects sp ON sr.SyncProjectId = sp.Id
                     WHERE sp.SourceTenantId IN @Ids
                    UNION ALL
                    SELECT sp.SinkTenantId AS TenantId, sr.StartedAt
                      FROM SyncRuns sr
                      JOIN SyncProjects sp ON sr.SyncProjectId = sp.Id
                     WHERE sp.SinkTenantId IN @Ids
                   ) t
             ORDER BY t.StartedAt DESC",
            new { Ids = candidateTenantIds.ToArray() });

    /// <summary>Persist the post-enumeration cursor + incremental flag.</summary>
    public Task SetCursorAsync(Guid runId, string? cursor, bool isIncremental) =>
        ExecuteAsync(@"
            UPDATE SyncRuns
               SET [Cursor] = @Cursor,
                   IsIncremental = @IsIncremental
             WHERE Id = @RunId;",
            new { RunId = runId, Cursor = cursor, IsIncremental = isIncremental });

    public Task<SyncRun?> GetByIdAsync(Guid id) =>
        QuerySingleOrDefaultAsync<SyncRun>(
            "SELECT * FROM SyncRuns WHERE Id = @Id", new { Id = id });

    public async Task<List<SyncRun>> GetByProjectAsync(Guid projectId, int take = 50)
    {
        var rows = await QueryAsync<SyncRun>(@"
            SELECT TOP (@Take) * FROM SyncRuns
             WHERE SyncProjectId = @ProjectId
             ORDER BY StartedAt DESC",
            new { ProjectId = projectId, Take = take });
        return rows.ToList();
    }

    public async Task<List<SyncRun>> GetRecentAsync(int take = 200, Guid? projectId = null, string? status = null)
    {
        var sql = @"
            SELECT TOP (@Take) * FROM SyncRuns
             WHERE (@ProjectId IS NULL OR SyncProjectId = @ProjectId)
               AND (@Status IS NULL OR Status = @Status)
             ORDER BY StartedAt DESC";
        var rows = await QueryAsync<SyncRun>(sql, new { Take = take, ProjectId = projectId, Status = status });
        return rows.ToList();
    }

    public Task UpdateCountersAsync(Guid runId, int read, int created, int updated, int skipped, int failed) =>
        ExecuteAsync(@"
            UPDATE SyncRuns
               SET ObjectsRead = @Read,
                   ObjectsCreated = @Created,
                   ObjectsUpdated = @Updated,
                   ObjectsSkipped = @Skipped,
                   ObjectsFailed = @Failed
             WHERE Id = @RunId;",
            new { RunId = runId, Read = read, Created = created, Updated = updated, Skipped = skipped, Failed = failed });

    public Task FinishAsync(Guid runId, string status, string? errorMessage, long durationMs) =>
        ExecuteAsync(@"
            UPDATE SyncRuns
               SET Status = @Status,
                   CompletedAt = SYSUTCDATETIME(),
                   DurationMs = @DurationMs,
                   ErrorMessage = @ErrorMessage
             WHERE Id = @RunId;",
            new { RunId = runId, Status = status, DurationMs = durationMs, ErrorMessage = errorMessage });

    // ─── Logs ─────────────────────────────────────────────────────────────

    public Task AppendLogAsync(Guid runId, string level, string message) =>
        ExecuteAsync(@"
            INSERT INTO SyncRunLogs (SyncRunId, Level, Message, Timestamp)
            VALUES (@RunId, @Level, @Message, SYSUTCDATETIME());",
            new { RunId = runId, Level = level, Message = message });

    public async Task<List<SyncRunLog>> GetLogsAsync(Guid runId, int take = 1000)
    {
        var rows = await QueryAsync<SyncRunLog>(@"
            SELECT TOP (@Take) Id, SyncRunId, Level, Message, Timestamp
              FROM SyncRunLogs
             WHERE SyncRunId = @RunId
             ORDER BY Id ASC",
            new { RunId = runId, Take = take });
        return rows.ToList();
    }
}
