using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Conduit.Core.SyncModels;

namespace Conduit.DataAccess.Repositories;

/// <summary>
/// Phase 7. CRUD over the Workflows + WorkflowSteps tree under a SyncProject.
/// Sits beside <see cref="SyncProjectRepository"/> rather than inside it so
/// per-project workflow operations stay legible.
/// </summary>
public class WorkflowRepository : BaseRepository
{
    public WorkflowRepository(DatabaseConfig config) : base(config) { }

    // ─── Workflows ───────────────────────────────────────────────────────────

    public async Task<List<Workflow>> GetByProjectAsync(Guid syncProjectId)
    {
        var rows = await QueryAsync<Workflow>(
            @"SELECT * FROM Workflows WHERE SyncProjectId = @Id ORDER BY Ordinal, Name",
            new { Id = syncProjectId });
        return rows.ToList();
    }

    public Task<Workflow?> GetByIdAsync(Guid id) =>
        QuerySingleOrDefaultAsync<Workflow>(
            "SELECT * FROM Workflows WHERE Id = @Id", new { Id = id });

    public async Task<Workflow> CreateAsync(Workflow w)
    {
        if (w.Id == Guid.Empty) w.Id = Guid.NewGuid();
        w.CreatedAt = DateTime.UtcNow;
        w.ModifiedAt = w.CreatedAt;
        await ExecuteAsync(@"
            INSERT INTO Workflows (Id, SyncProjectId, Name, Description, Ordinal, Enabled, CreatedAt, ModifiedAt)
            VALUES (@Id, @SyncProjectId, @Name, @Description, @Ordinal, @Enabled, @CreatedAt, @ModifiedAt);", w);
        return w;
    }

    public async Task<bool> UpdateAsync(Workflow w)
    {
        w.ModifiedAt = DateTime.UtcNow;
        var rows = await ExecuteAsync(@"
            UPDATE Workflows
               SET Name = @Name,
                   Description = @Description,
                   Ordinal = @Ordinal,
                   Enabled = @Enabled,
                   ModifiedAt = @ModifiedAt
             WHERE Id = @Id;", w);
        return rows > 0;
    }

    /// <summary>
    /// Cascades step-level data: deletes mappings + scopes that referenced any
    /// of the workflow's steps, then deletes the steps, then the workflow.
    /// </summary>
    public Task<int> DeleteAsync(Guid id) =>
        ExecuteAsync(@"
            DELETE FROM AttributeMappings  WHERE WorkflowStepId IN (SELECT Id FROM WorkflowSteps WHERE WorkflowId = @Id);
            DELETE FROM SyncProjectScopes  WHERE WorkflowStepId IN (SELECT Id FROM WorkflowSteps WHERE WorkflowId = @Id);
            DELETE FROM WorkflowSteps      WHERE WorkflowId = @Id;
            DELETE FROM Workflows          WHERE Id = @Id;",
            new { Id = id });

    // ─── WorkflowSteps ───────────────────────────────────────────────────────

    public async Task<List<WorkflowStep>> GetStepsAsync(Guid workflowId)
    {
        var rows = await QueryAsync<WorkflowStep>(
            @"SELECT * FROM WorkflowSteps WHERE WorkflowId = @Id ORDER BY Ordinal, Name",
            new { Id = workflowId });
        return rows.ToList();
    }

    public Task<WorkflowStep?> GetStepByIdAsync(Guid id) =>
        QuerySingleOrDefaultAsync<WorkflowStep>(
            "SELECT * FROM WorkflowSteps WHERE Id = @Id", new { Id = id });

    public async Task<WorkflowStep> CreateStepAsync(WorkflowStep s)
    {
        if (s.Id == Guid.Empty) s.Id = Guid.NewGuid();
        s.CreatedAt = DateTime.UtcNow;
        s.ModifiedAt = s.CreatedAt;
        await ExecuteAsync(@"
            INSERT INTO WorkflowSteps (Id, WorkflowId, Name, StepType, Ordinal, Enabled, Configuration, CreatedAt, ModifiedAt)
            VALUES (@Id, @WorkflowId, @Name, @StepType, @Ordinal, @Enabled, @Configuration, @CreatedAt, @ModifiedAt);", s);
        return s;
    }

    public async Task<bool> UpdateStepAsync(WorkflowStep s)
    {
        s.ModifiedAt = DateTime.UtcNow;
        var rows = await ExecuteAsync(@"
            UPDATE WorkflowSteps
               SET Name = @Name,
                   StepType = @StepType,
                   Ordinal = @Ordinal,
                   Enabled = @Enabled,
                   Configuration = @Configuration,
                   ModifiedAt = @ModifiedAt
             WHERE Id = @Id;", s);
        return rows > 0;
    }

    public Task<int> DeleteStepAsync(Guid id) =>
        ExecuteAsync(@"
            DELETE FROM AttributeMappings  WHERE WorkflowStepId = @Id;
            DELETE FROM SyncProjectScopes  WHERE WorkflowStepId = @Id;
            DELETE FROM WorkflowSteps      WHERE Id = @Id;",
            new { Id = id });

    /// <summary>
    /// Bulk reorder for a workflow's steps. Caller supplies the desired order;
    /// repository writes the resulting ordinals in one round-trip.
    /// </summary>
    public async Task ReorderStepsAsync(Guid workflowId, IReadOnlyList<Guid> orderedIds)
    {
        using var conn = CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            for (var i = 0; i < orderedIds.Count; i++)
            {
                await conn.ExecuteAsync(
                    @"UPDATE WorkflowSteps SET Ordinal = @Ordinal, ModifiedAt = SYSUTCDATETIME()
                       WHERE Id = @Id AND WorkflowId = @WorkflowId;",
                    new { Ordinal = i, Id = orderedIds[i], WorkflowId = workflowId },
                    tx);
            }
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }
    }

    public async Task ReorderWorkflowsAsync(Guid syncProjectId, IReadOnlyList<Guid> orderedIds)
    {
        using var conn = CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            for (var i = 0; i < orderedIds.Count; i++)
            {
                await conn.ExecuteAsync(
                    @"UPDATE Workflows SET Ordinal = @Ordinal, ModifiedAt = SYSUTCDATETIME()
                       WHERE Id = @Id AND SyncProjectId = @ProjectId;",
                    new { Ordinal = i, Id = orderedIds[i], ProjectId = syncProjectId },
                    tx);
            }
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }
    }

    // ─── Step-scoped mapping + scope read paths ──────────────────────────────

    /// <summary>Mappings for one specific step. Used by the orchestrator's Mapping-step branch.</summary>
    public async Task<List<AttributeMapping>> GetMappingsByStepAsync(Guid workflowStepId)
    {
        var rows = await QueryAsync<AttributeMapping>(
            @"SELECT * FROM AttributeMappings WHERE WorkflowStepId = @Id ORDER BY SortOrder, SinkAttribute",
            new { Id = workflowStepId });
        return rows.ToList();
    }

    public async Task ReplaceMappingsForStepAsync(Guid syncProjectId, Guid workflowStepId, IEnumerable<AttributeMapping> mappings)
    {
        using var conn = CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            await conn.ExecuteAsync(
                "DELETE FROM AttributeMappings WHERE WorkflowStepId = @Id",
                new { Id = workflowStepId }, tx);

            var order = 0;
            foreach (var m in mappings)
            {
                if (m.Id == Guid.Empty) m.Id = Guid.NewGuid();
                m.SyncProjectId = syncProjectId;
                m.WorkflowStepId = workflowStepId;
                if (m.SortOrder == 0) m.SortOrder = order++;
                await conn.ExecuteAsync(@"
                    INSERT INTO AttributeMappings
                        (Id, SyncProjectId, WorkflowStepId, SourceAttribute, SinkAttribute, TransformExpr, IsRequired, SortOrder)
                    VALUES
                        (@Id, @SyncProjectId, @WorkflowStepId, @SourceAttribute, @SinkAttribute, @TransformExpr, @IsRequired, @SortOrder);",
                    m, tx);
            }
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }
    }

    /// <summary>Optional per-step scope (Mapping steps only).</summary>
    public Task<SyncProjectScope?> GetScopeByStepAsync(Guid workflowStepId) =>
        QuerySingleOrDefaultAsync<SyncProjectScope>(
            "SELECT TOP 1 * FROM SyncProjectScopes WHERE WorkflowStepId = @Id",
            new { Id = workflowStepId });

    public async Task UpsertScopeForStepAsync(Guid syncProjectId, Guid workflowStepId, SyncProjectScope scope)
    {
        scope.SyncProjectId = syncProjectId;
        scope.WorkflowStepId = workflowStepId;
        scope.LastModified = DateTime.UtcNow;
        if (scope.Id == Guid.Empty) scope.Id = Guid.NewGuid();
        if (scope.CreatedAt == default) scope.CreatedAt = DateTime.UtcNow;

        const string sql = @"
            MERGE SyncProjectScopes AS tgt
            USING (SELECT @WorkflowStepId AS WorkflowStepId) AS src
               ON tgt.WorkflowStepId = src.WorkflowStepId
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
                VALUES (@Id, @SyncProjectId, @WorkflowStepId, @BaseDN, @IncludedBaseDNs, @ExcludedBaseDNs, @LdapFilter, @QueryExpression, @PageSize, @MaxObjects, @IncludeDeleted, @CreatedAt, @LastModified);";
        await ExecuteAsync(sql, scope);
    }
}
