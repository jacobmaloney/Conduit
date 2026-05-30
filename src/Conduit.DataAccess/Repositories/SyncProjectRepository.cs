using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Conduit.Core.SyncModels;

namespace Conduit.DataAccess.Repositories;

/// <summary>
/// CRUD over SyncProjects + the per-project SyncProjectScope. Dapper, matches
/// the Conduit data-access convention (one repo per aggregate, no service layer
/// between repo and caller).
/// </summary>
public class SyncProjectRepository : BaseRepository
{
    public SyncProjectRepository(DatabaseConfig config) : base(config) { }

    public async Task<List<SyncProject>> GetAllAsync()
    {
        var rows = await QueryAsync<SyncProject>("SELECT * FROM SyncProjects ORDER BY Name");
        return rows.ToList();
    }

    public Task<SyncProject?> GetByIdAsync(Guid id) =>
        QuerySingleOrDefaultAsync<SyncProject>("SELECT * FROM SyncProjects WHERE Id = @Id", new { Id = id });

    public async Task<List<SyncProject>> GetEnabledScheduledAsync()
    {
        var rows = await QueryAsync<SyncProject>(@"
            SELECT * FROM SyncProjects
             WHERE IsEnabled = 1
               AND CronSchedule IS NOT NULL
               AND LEN(CronSchedule) > 0");
        return rows.ToList();
    }

    public async Task<SyncProject> CreateAsync(SyncProject p)
    {
        if (p.Id == Guid.Empty) p.Id = Guid.NewGuid();
        p.CreatedAt = DateTime.UtcNow;
        p.LastModified = p.CreatedAt;

        const string sql = @"
            INSERT INTO SyncProjects
                (Id, WorkspaceId, Name, Description, SourceTenantId, SinkTenantId, ObjectClass,
                 SourceCredentialName, SinkCredentialName,
                 CronSchedule, IsEnabled, IsRunning, LastRunAt, LastRunStatus, LastRunId,
                 NextScheduledRunAt, TotalRuns, SuccessfulRuns, FailedRuns, CreatedAt, LastModified)
            VALUES
                (@Id, @WorkspaceId, @Name, @Description, @SourceTenantId, @SinkTenantId, @ObjectClass,
                 @SourceCredentialName, @SinkCredentialName,
                 @CronSchedule, @IsEnabled, @IsRunning, @LastRunAt, @LastRunStatus, @LastRunId,
                 @NextScheduledRunAt, @TotalRuns, @SuccessfulRuns, @FailedRuns, @CreatedAt, @LastModified);";
        await ExecuteAsync(sql, p);
        return p;
    }

    public async Task<bool> UpdateAsync(SyncProject p)
    {
        p.LastModified = DateTime.UtcNow;

        const string sql = @"
            UPDATE SyncProjects
               SET Name = @Name,
                   Description = @Description,
                   SourceTenantId = @SourceTenantId,
                   SinkTenantId = @SinkTenantId,
                   ObjectClass = @ObjectClass,
                   SourceCredentialName = @SourceCredentialName,
                   SinkCredentialName = @SinkCredentialName,
                   CronSchedule = @CronSchedule,
                   IsEnabled = @IsEnabled,
                   LastModified = @LastModified
             WHERE Id = @Id;";
        var rows = await ExecuteAsync(sql, p);
        return rows > 0;
    }

    public Task<int> DeleteAsync(Guid id) =>
        ExecuteAsync(@"
            DELETE FROM SyncProjectScopes WHERE SyncProjectId = @Id;
            DELETE FROM AttributeMappings WHERE SyncProjectId = @Id;
            -- Phase 7: cascade workflow tree.
            DELETE FROM WorkflowSteps     WHERE WorkflowId IN (SELECT Id FROM Workflows WHERE SyncProjectId = @Id);
            DELETE FROM Workflows         WHERE SyncProjectId = @Id;
            DELETE FROM SyncRunLogs       WHERE SyncRunId IN (SELECT Id FROM SyncRuns WHERE SyncProjectId = @Id);
            DELETE FROM SyncRuns          WHERE SyncProjectId = @Id;
            DELETE FROM SyncProjects      WHERE Id = @Id;",
            new { Id = id });

    /// <summary>
    /// Atomic compare-and-swap of <c>IsRunning</c> from 0 → 1. Returns
    /// <c>true</c> when this caller won the swap (row updated), <c>false</c>
    /// when the project was already running. The controller calls this BEFORE
    /// firing the orchestrator to make manual Run-Now race-safe; the
    /// orchestrator also calls it on entry and treats a <c>false</c> return as
    /// a no-op (the row's already in the right state from the controller).
    /// </summary>
    public async Task<bool> SetRunningAsync(Guid projectId, Guid runId)
    {
        var rows = await ExecuteScalarAsync<int>(@"
            UPDATE SyncProjects
               SET IsRunning = 1,
                   LastRunId = @RunId,
                   LastRunAt = SYSUTCDATETIME(),
                   LastRunStatus = 'Running',
                   TotalRuns = TotalRuns + 1,
                   LastModified = SYSUTCDATETIME()
             WHERE Id = @ProjectId
               AND IsRunning = 0;
            SELECT @@ROWCOUNT;",
            new { ProjectId = projectId, RunId = runId });
        return rows > 0;
    }

    /// <summary>Stamps the post-run state on the project.</summary>
    public Task FinishRunAsync(Guid projectId, string status) =>
        ExecuteAsync(@"
            UPDATE SyncProjects
               SET IsRunning = 0,
                   LastRunStatus = @Status,
                   SuccessfulRuns = SuccessfulRuns + CASE WHEN @Status = 'Succeeded' THEN 1 ELSE 0 END,
                   FailedRuns     = FailedRuns     + CASE WHEN @Status = 'Failed'    THEN 1 ELSE 0 END,
                   LastModified = SYSUTCDATETIME()
             WHERE Id = @ProjectId;",
            new { ProjectId = projectId, Status = status });

    // ─── SyncProjectScope ────────────────────────────────────────────────

    public Task<SyncProjectScope?> GetScopeAsync(Guid projectId) =>
        QuerySingleOrDefaultAsync<SyncProjectScope>(
            "SELECT * FROM SyncProjectScopes WHERE SyncProjectId = @Id",
            new { Id = projectId });

    public async Task UpsertScopeAsync(SyncProjectScope scope)
    {
        scope.LastModified = DateTime.UtcNow;

        // SQL Server MERGE on the project-level scope (WorkflowStepId IS NULL).
        // Per-step scopes are owned by WorkflowRepository.UpsertScopeForStepAsync;
        // this method keeps the pre-Phase-7 semantics: one project-scoped row.
        const string sql = @"
            MERGE SyncProjectScopes AS tgt
            USING (SELECT @SyncProjectId AS SyncProjectId) AS src
               ON tgt.SyncProjectId = src.SyncProjectId AND tgt.WorkflowStepId IS NULL
            WHEN MATCHED THEN
                UPDATE SET BaseDN = @BaseDN,
                           LdapFilter = @LdapFilter,
                           QueryExpression = @QueryExpression,
                           PageSize = @PageSize,
                           MaxObjects = @MaxObjects,
                           IncludeDeleted = @IncludeDeleted,
                           LastModified = @LastModified
            WHEN NOT MATCHED THEN
                INSERT (Id, SyncProjectId, WorkflowStepId, BaseDN, LdapFilter, QueryExpression, PageSize, MaxObjects, IncludeDeleted, CreatedAt, LastModified)
                VALUES (@Id, @SyncProjectId, NULL, @BaseDN, @LdapFilter, @QueryExpression, @PageSize, @MaxObjects, @IncludeDeleted, @CreatedAt, @LastModified);";

        if (scope.Id == Guid.Empty) scope.Id = Guid.NewGuid();
        if (scope.CreatedAt == default) scope.CreatedAt = DateTime.UtcNow;
        await ExecuteAsync(sql, scope);
    }

    /// <summary>
    /// Phase 7. Returns the project-level scope (WorkflowStepId IS NULL). Per-step
    /// scopes go through <see cref="WorkflowRepository.GetScopeByStepAsync"/>.
    /// </summary>
    public Task<SyncProjectScope?> GetProjectScopeAsync(Guid projectId) =>
        QuerySingleOrDefaultAsync<SyncProjectScope>(
            "SELECT TOP 1 * FROM SyncProjectScopes WHERE SyncProjectId = @Id AND WorkflowStepId IS NULL",
            new { Id = projectId });

    // ─── AttributeMapping ────────────────────────────────────────────────

    public async Task<List<AttributeMapping>> GetMappingsAsync(Guid projectId)
    {
        var rows = await QueryAsync<AttributeMapping>(@"
            SELECT * FROM AttributeMappings
             WHERE SyncProjectId = @Id
             ORDER BY SortOrder, SinkAttribute",
            new { Id = projectId });
        return rows.ToList();
    }

    public async Task ReplaceMappingsAsync(Guid projectId, IEnumerable<AttributeMapping> mappings)
    {
        using var connection = CreateConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            // Phase 7: project-scoped delete only touches the unattached (legacy)
            // mappings. Per-step mappings live under WorkflowSteps and are owned
            // by WorkflowRepository.ReplaceMappingsForStepAsync.
            await connection.ExecuteAsync(
                "DELETE FROM AttributeMappings WHERE SyncProjectId = @Id AND WorkflowStepId IS NULL",
                new { Id = projectId }, tx);

            int order = 0;
            foreach (var m in mappings)
            {
                if (m.Id == Guid.Empty) m.Id = Guid.NewGuid();
                m.SyncProjectId = projectId;
                m.WorkflowStepId = null;
                if (m.SortOrder == 0) m.SortOrder = order++;
                await connection.ExecuteAsync(@"
                    INSERT INTO AttributeMappings
                        (Id, SyncProjectId, WorkflowStepId, SourceAttribute, SinkAttribute, TransformExpr, IsRequired, SortOrder)
                    VALUES
                        (@Id, @SyncProjectId, @WorkflowStepId, @SourceAttribute, @SinkAttribute, @TransformExpr, @IsRequired, @SortOrder);",
                    m, tx);
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}
