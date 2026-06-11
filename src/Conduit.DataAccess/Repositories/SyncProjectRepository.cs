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
                 SourceCredentialName, SinkCredentialName, SourceTable, SinkTable,
                 CronSchedule, IsEnabled, IsRunning, SkipUnchanged, LastRunAt, LastRunStatus, LastRunId,
                 NextScheduledRunAt, TotalRuns, SuccessfulRuns, FailedRuns, CreatedAt, LastModified)
            VALUES
                (@Id, @WorkspaceId, @Name, @Description, @SourceTenantId, @SinkTenantId, @ObjectClass,
                 @SourceCredentialName, @SinkCredentialName, @SourceTable, @SinkTable,
                 @CronSchedule, @IsEnabled, @IsRunning, @SkipUnchanged, @LastRunAt, @LastRunStatus, @LastRunId,
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
                   SourceTable = @SourceTable,
                   SinkTable = @SinkTable,
                   CronSchedule = @CronSchedule,
                   IsEnabled = @IsEnabled,
                   SkipUnchanged = @SkipUnchanged,
                   LastModified = @LastModified
             WHERE Id = @Id;";
        var rows = await ExecuteAsync(sql, p);
        return rows > 0;
    }

    /// <summary>
    /// Clones a sync project as a brand-new project under <paramref name="newName"/>,
    /// mirroring IC's "Copy Template" action. Deep-copies the config — project row,
    /// project-level scope, project-level (legacy) attribute mappings, AND the full
    /// Phase-7 workflow tree (Workflows → WorkflowSteps → per-step mappings → per-step
    /// scopes) — every row re-keyed with a fresh Guid. The clone is created
    /// <b>Disabled, unscheduled, never-run</b> (run history + counters are NOT copied)
    /// so it cannot fire until the operator reviews it. The whole copy runs in one
    /// transaction so a half-cloned project can never be left behind.
    /// Returns the new project's Id.
    /// </summary>
    public async Task<Guid> CloneAsync(Guid sourceProjectId, string newName)
    {
        using var conn = CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var source = await conn.QuerySingleOrDefaultAsync<SyncProject>(
                "SELECT * FROM SyncProjects WHERE Id = @Id", new { Id = sourceProjectId }, tx);
            if (source is null)
                throw new InvalidOperationException($"SyncProject {sourceProjectId} not found.");

            var now = DateTime.UtcNow;
            var newId = Guid.NewGuid();

            // 1) The project row. New identity; disabled + unscheduled + zeroed run
            //    state so the clone is inert until the operator enables it.
            await conn.ExecuteAsync(@"
                INSERT INTO SyncProjects
                    (Id, WorkspaceId, Name, Description, SourceTenantId, SinkTenantId, ObjectClass,
                     SourceCredentialName, SinkCredentialName, SourceTable, SinkTable,
                     CronSchedule, IsEnabled, IsRunning, SkipUnchanged, LastRunAt, LastRunStatus, LastRunId,
                     NextScheduledRunAt, TotalRuns, SuccessfulRuns, FailedRuns, CreatedAt, LastModified)
                VALUES
                    (@Id, @WorkspaceId, @Name, @Description, @SourceTenantId, @SinkTenantId, @ObjectClass,
                     @SourceCredentialName, @SinkCredentialName, @SourceTable, @SinkTable,
                     NULL, 0, 0, @SkipUnchanged, NULL, NULL, NULL,
                     NULL, 0, 0, 0, @CreatedAt, @LastModified);",
                new
                {
                    Id = newId,
                    source.WorkspaceId,
                    Name = newName,
                    source.Description,
                    source.SourceTenantId,
                    source.SinkTenantId,
                    source.ObjectClass,
                    source.SourceCredentialName,
                    source.SinkCredentialName,
                    source.SourceTable,
                    source.SinkTable,
                    source.SkipUnchanged,
                    CreatedAt = now,
                    LastModified = now
                }, tx);

            // 2) Project-level scope (WorkflowStepId IS NULL).
            var projectScope = await conn.QuerySingleOrDefaultAsync<SyncProjectScope>(
                "SELECT * FROM SyncProjectScopes WHERE SyncProjectId = @Id AND WorkflowStepId IS NULL",
                new { Id = sourceProjectId }, tx);
            if (projectScope is not null)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO SyncProjectScopes
                        (Id, SyncProjectId, WorkflowStepId, BaseDN, IncludedBaseDNs, ExcludedBaseDNs, LdapFilter, QueryExpression, PageSize, MaxObjects, IncludeDeleted, CreatedAt, LastModified)
                    VALUES
                        (@Id, @SyncProjectId, NULL, @BaseDN, @IncludedBaseDNs, @ExcludedBaseDNs, @LdapFilter, @QueryExpression, @PageSize, @MaxObjects, @IncludeDeleted, @CreatedAt, @LastModified);",
                    new
                    {
                        Id = Guid.NewGuid(),
                        SyncProjectId = newId,
                        projectScope.BaseDN,
                        projectScope.IncludedBaseDNs,
                        projectScope.ExcludedBaseDNs,
                        projectScope.LdapFilter,
                        projectScope.QueryExpression,
                        projectScope.PageSize,
                        projectScope.MaxObjects,
                        projectScope.IncludeDeleted,
                        CreatedAt = now,
                        LastModified = now
                    }, tx);
            }

            // 3) Project-level (legacy) mappings (WorkflowStepId IS NULL).
            var projectMappings = (await conn.QueryAsync<AttributeMapping>(
                "SELECT * FROM AttributeMappings WHERE SyncProjectId = @Id AND WorkflowStepId IS NULL ORDER BY SortOrder, SinkAttribute",
                new { Id = sourceProjectId }, tx)).ToList();
            foreach (var m in projectMappings)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO AttributeMappings
                        (Id, SyncProjectId, WorkflowStepId, SourceAttribute, SinkAttribute, TransformExpr, IsRequired, SortOrder)
                    VALUES
                        (@Id, @SyncProjectId, NULL, @SourceAttribute, @SinkAttribute, @TransformExpr, @IsRequired, @SortOrder);",
                    new
                    {
                        Id = Guid.NewGuid(),
                        SyncProjectId = newId,
                        m.SourceAttribute,
                        m.SinkAttribute,
                        m.TransformExpr,
                        m.IsRequired,
                        m.SortOrder
                    }, tx);
            }

            // 4) Workflow tree. Each old WorkflowStep.Id → new id so per-step
            //    mappings + scopes can be re-pointed.
            var workflows = (await conn.QueryAsync<Workflow>(
                "SELECT * FROM Workflows WHERE SyncProjectId = @Id ORDER BY Ordinal, Name",
                new { Id = sourceProjectId }, tx)).ToList();

            foreach (var wf in workflows)
            {
                var newWorkflowId = Guid.NewGuid();
                await conn.ExecuteAsync(@"
                    INSERT INTO Workflows (Id, SyncProjectId, Name, Description, Ordinal, Enabled, CreatedAt, ModifiedAt)
                    VALUES (@Id, @SyncProjectId, @Name, @Description, @Ordinal, @Enabled, @CreatedAt, @ModifiedAt);",
                    new
                    {
                        Id = newWorkflowId,
                        SyncProjectId = newId,
                        wf.Name,
                        wf.Description,
                        wf.Ordinal,
                        wf.Enabled,
                        CreatedAt = now,
                        ModifiedAt = now
                    }, tx);

                var steps = (await conn.QueryAsync<WorkflowStep>(
                    "SELECT * FROM WorkflowSteps WHERE WorkflowId = @Id ORDER BY Ordinal, Name",
                    new { Id = wf.Id }, tx)).ToList();

                foreach (var step in steps)
                {
                    var newStepId = Guid.NewGuid();
                    await conn.ExecuteAsync(@"
                        INSERT INTO WorkflowSteps (Id, WorkflowId, Name, StepType, Ordinal, Enabled, Configuration, CreatedAt, ModifiedAt)
                        VALUES (@Id, @WorkflowId, @Name, @StepType, @Ordinal, @Enabled, @Configuration, @CreatedAt, @ModifiedAt);",
                        new
                        {
                            Id = newStepId,
                            WorkflowId = newWorkflowId,
                            step.Name,
                            step.StepType,
                            step.Ordinal,
                            step.Enabled,
                            step.Configuration,
                            CreatedAt = now,
                            ModifiedAt = now
                        }, tx);

                    // Per-step mappings.
                    var stepMappings = (await conn.QueryAsync<AttributeMapping>(
                        "SELECT * FROM AttributeMappings WHERE WorkflowStepId = @Id ORDER BY SortOrder, SinkAttribute",
                        new { Id = step.Id }, tx)).ToList();
                    foreach (var m in stepMappings)
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO AttributeMappings
                                (Id, SyncProjectId, WorkflowStepId, SourceAttribute, SinkAttribute, TransformExpr, IsRequired, SortOrder)
                            VALUES
                                (@Id, @SyncProjectId, @WorkflowStepId, @SourceAttribute, @SinkAttribute, @TransformExpr, @IsRequired, @SortOrder);",
                            new
                            {
                                Id = Guid.NewGuid(),
                                SyncProjectId = newId,
                                WorkflowStepId = newStepId,
                                m.SourceAttribute,
                                m.SinkAttribute,
                                m.TransformExpr,
                                m.IsRequired,
                                m.SortOrder
                            }, tx);
                    }

                    // Per-step scope.
                    var stepScope = await conn.QuerySingleOrDefaultAsync<SyncProjectScope>(
                        "SELECT TOP 1 * FROM SyncProjectScopes WHERE WorkflowStepId = @Id",
                        new { Id = step.Id }, tx);
                    if (stepScope is not null)
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO SyncProjectScopes
                                (Id, SyncProjectId, WorkflowStepId, BaseDN, IncludedBaseDNs, ExcludedBaseDNs, LdapFilter, QueryExpression, PageSize, MaxObjects, IncludeDeleted, CreatedAt, LastModified)
                            VALUES
                                (@Id, @SyncProjectId, @WorkflowStepId, @BaseDN, @IncludedBaseDNs, @ExcludedBaseDNs, @LdapFilter, @QueryExpression, @PageSize, @MaxObjects, @IncludeDeleted, @CreatedAt, @LastModified);",
                            new
                            {
                                Id = Guid.NewGuid(),
                                SyncProjectId = newId,
                                WorkflowStepId = newStepId,
                                stepScope.BaseDN,
                                stepScope.IncludedBaseDNs,
                                stepScope.ExcludedBaseDNs,
                                stepScope.LdapFilter,
                                stepScope.QueryExpression,
                                stepScope.PageSize,
                                stepScope.MaxObjects,
                                stepScope.IncludeDeleted,
                                CreatedAt = now,
                                LastModified = now
                            }, tx);
                    }
                }
            }

            tx.Commit();
            return newId;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
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

    /// <summary>
    /// Stamps the real SyncRun id onto a project AFTER a pre-claimed CAS. The
    /// pre-claim happens before the run row exists, so callers CAS with a
    /// placeholder id; the orchestrator calls this once the run row is created.
    /// Guarded on IsRunning = 1 so a finished/force-released project is never
    /// retro-stamped.
    /// </summary>
    public Task StampLastRunIdAsync(Guid projectId, Guid runId) =>
        ExecuteAsync(@"
            UPDATE SyncProjects
               SET LastRunId = @RunId
             WHERE Id = @ProjectId
               AND IsRunning = 1;",
            new { ProjectId = projectId, RunId = runId });

    /// <summary>
    /// Releases the <c>IsRunning</c> flag for a project given ONLY its id, with
    /// no run-stats stamping. Used by the orchestrator's early-failure guard
    /// (Worf HIGH-1) when a run row may not exist yet (e.g. GetById returned
    /// null or CreateAsync threw) — in that case there is nothing to stamp, we
    /// just need the project unstuck so the next Run-Now isn't a permanent 409.
    /// </summary>
    public Task ClearRunningAsync(Guid projectId) =>
        ExecuteAsync(@"
            UPDATE SyncProjects
               SET IsRunning = 0,
                   LastModified = SYSUTCDATETIME()
             WHERE Id = @ProjectId;",
            new { ProjectId = projectId });

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

    // Project-level scope ONLY (WorkflowStepId IS NULL). Per-step scope rows live in
    // the same table (Phase 7) and are owned by WorkflowRepository; without this
    // filter a multi-class project with per-step scopes returns multiple rows and
    // QuerySingleOrDefault throws ("Sequence contains more than one element") —
    // which killed the circuit on Manage-open. Mirrors UpsertScopeAsync's MERGE key.
    public Task<SyncProjectScope?> GetScopeAsync(Guid projectId) =>
        QuerySingleOrDefaultAsync<SyncProjectScope>(
            "SELECT * FROM SyncProjectScopes WHERE SyncProjectId = @Id AND WorkflowStepId IS NULL",
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
                           IncludedBaseDNs = @IncludedBaseDNs,
                           ExcludedBaseDNs = @ExcludedBaseDNs,
                           LdapFilter = @LdapFilter,
                           QueryExpression = @QueryExpression,
                           PageSize = @PageSize,
                           MaxObjects = @MaxObjects,
                           IncludeDeleted = @IncludeDeleted,
                           LastModified = @LastModified
            WHEN NOT MATCHED THEN
                INSERT (Id, SyncProjectId, WorkflowStepId, BaseDN, IncludedBaseDNs, ExcludedBaseDNs, LdapFilter, QueryExpression, PageSize, MaxObjects, IncludeDeleted, CreatedAt, LastModified)
                VALUES (@Id, @SyncProjectId, NULL, @BaseDN, @IncludedBaseDNs, @ExcludedBaseDNs, @LdapFilter, @QueryExpression, @PageSize, @MaxObjects, @IncludeDeleted, @CreatedAt, @LastModified);";

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
        // Project-level mappings ONLY (WorkflowStepId IS NULL) — the symmetric read
        // for ReplaceMappingsAsync, which deletes/re-inserts only unattached rows.
        // Without the filter, the edit form loads per-step mappings into the
        // project-level tab and Save re-inserts them as duplicated project rows.
        var rows = await QueryAsync<AttributeMapping>(@"
            SELECT * FROM AttributeMappings
             WHERE SyncProjectId = @Id AND WorkflowStepId IS NULL
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
