using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Conduit.Core.SyncModels;

namespace Conduit.DataAccess.Repositories;

/// <summary>
/// Phase 4: SyncRunAsyncJobs persistence. Submissions are append-only at insert;
/// the poller updates State + ErrorMessage + ResultJson + CompletedAt + counters
/// once a row is terminal. Pending rows are selected by State + a stale-poll
/// guard so a fleet of pollers wouldn't dogpile (single-process today; cheap
/// safety for later).
/// </summary>
public class SyncRunAsyncJobRepository : BaseRepository
{
    public SyncRunAsyncJobRepository(DatabaseConfig config) : base(config) { }

    public Task InsertAsync(SyncRunAsyncJob job) =>
        ExecuteAsync(@"
            INSERT INTO SyncRunAsyncJobs
                (SyncRunId, SyncProjectId, TenantId, SystemType, JobType, JobId,
                 ObjectExternalId, State, PayloadJson, SubmittedAt, PollAttempts)
            VALUES
                (@SyncRunId, @SyncProjectId, @TenantId, @SystemType, @JobType, @JobId,
                 @ObjectExternalId, @State, @PayloadJson, @SubmittedAt, 0);",
            job);

    /// <summary>
    /// Pending rows due for the next poll tick. Sorted oldest-first; capped per
    /// tick to keep one poll cycle bounded. We intentionally pull rows of all
    /// SystemTypes — the poller groups them and routes to each adapter.
    /// </summary>
    public async Task<List<SyncRunAsyncJob>> GetPendingAsync(int batchSize)
    {
        var rows = await QueryAsync<SyncRunAsyncJob>(@"
            SELECT TOP (@BatchSize) *
              FROM SyncRunAsyncJobs
             WHERE State = 'Pending'
             ORDER BY SubmittedAt ASC",
            new { BatchSize = batchSize });
        return rows.ToList();
    }

    public Task MarkPolledAsync(long id) =>
        ExecuteAsync(@"
            UPDATE SyncRunAsyncJobs
               SET LastPolledAt = SYSUTCDATETIME(),
                   PollAttempts = PollAttempts + 1
             WHERE Id = @Id;",
            new { Id = id });

    public Task MarkSucceededAsync(long id, string? resultJson) =>
        ExecuteAsync(@"
            UPDATE SyncRunAsyncJobs
               SET State = 'Succeeded',
                   ResultJson = @ResultJson,
                   CompletedAt = SYSUTCDATETIME(),
                   LastPolledAt = SYSUTCDATETIME(),
                   PollAttempts = PollAttempts + 1
             WHERE Id = @Id;",
            new { Id = id, ResultJson = resultJson });

    public Task MarkFailedAsync(long id, string errorMessage) =>
        ExecuteAsync(@"
            UPDATE SyncRunAsyncJobs
               SET State = 'Failed',
                   ErrorMessage = @ErrorMessage,
                   CompletedAt = SYSUTCDATETIME(),
                   LastPolledAt = SYSUTCDATETIME(),
                   PollAttempts = PollAttempts + 1
             WHERE Id = @Id;",
            new { Id = id, ErrorMessage = errorMessage });

    /// <summary>Job rows attached to a run — for the Sync History detail page.</summary>
    public async Task<List<SyncRunAsyncJob>> GetByRunAsync(Guid syncRunId)
    {
        var rows = await QueryAsync<SyncRunAsyncJob>(@"
            SELECT *
              FROM SyncRunAsyncJobs
             WHERE SyncRunId = @RunId
             ORDER BY SubmittedAt ASC",
            new { RunId = syncRunId });
        return rows.ToList();
    }

    /// <summary>
    /// Phase 5 admin surface. Cancel a Pending row so the poller skips it on the
    /// next tick. State guard in SQL prevents a race where the poller just moved
    /// it to terminal — we only flip Pending → Cancelled. Returns true on transition.
    /// </summary>
    public async Task<bool> CancelAsync(long id)
    {
        var affected = await ExecuteAsync(@"
            UPDATE SyncRunAsyncJobs
               SET State = 'Cancelled',
                   CompletedAt = SYSUTCDATETIME()
             WHERE Id = @Id
               AND State = 'Pending';",
            new { Id = id });
        return affected > 0;
    }

    /// <summary>
    /// Phase 5 admin surface. Re-queue a Failed or Cancelled row by flipping
    /// it back to Pending, clearing the error + completion timestamps, and
    /// bumping SubmittedAt so the FIFO order is fresh. Poll attempts kept
    /// for forensic value. Returns true on transition.
    /// </summary>
    public async Task<bool> RetryAsync(long id)
    {
        var affected = await ExecuteAsync(@"
            UPDATE SyncRunAsyncJobs
               SET State = 'Pending',
                   ErrorMessage = NULL,
                   CompletedAt = NULL,
                   SubmittedAt = SYSUTCDATETIME()
             WHERE Id = @Id
               AND State IN ('Failed', 'Cancelled');",
            new { Id = id });
        return affected > 0;
    }

    /// <summary>Single row lookup (used by the admin Cancel/Retry actions).</summary>
    public async Task<SyncRunAsyncJob?> GetByIdAsync(long id)
    {
        var rows = await QueryAsync<SyncRunAsyncJob>(@"
            SELECT TOP 1 *
              FROM SyncRunAsyncJobs
             WHERE Id = @Id",
            new { Id = id });
        return rows.FirstOrDefault();
    }
}
