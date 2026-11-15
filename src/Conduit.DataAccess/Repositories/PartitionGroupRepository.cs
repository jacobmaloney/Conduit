using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Conduit.DataAccess.Repositories;

/// <summary>How one partition run of a split read ended (schema V38, PartitionRunResults).</summary>
public sealed class PartitionRunResult
{
    public Guid GroupId { get; set; }
    public int PartitionIndex { get; set; }
    public int PartitionCount { get; set; }
    public Guid RunId { get; set; }
    public Guid ProjectId { get; set; }
    public bool Succeeded { get; set; }
    /// <summary>Every delete-detected step of the run read its container completely (no truncation, no incremental read).</summary>
    public bool AllStepsCompleteRead { get; set; }
    /// <summary>The container this partition read, as the plan described it; the group's plan fingerprint is built from these.</summary>
    public string ContainerKey { get; set; } = string.Empty;
    /// <summary>Comma-separated ids (N format) of the steps whose read ids this partition recorded.</summary>
    public string? ArmedStepIds { get; set; }
    public DateTime RecordedAt { get; set; }

    public IReadOnlyList<Guid> ArmedSteps() =>
        (ArmedStepIds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
}

/// <summary>The last complete group of a plan that a step diffs against (schema V38, PartitionStepBaselines).</summary>
public sealed record PartitionStepBaseline(Guid ProjectId, Guid StepId, Guid GroupId, string PlanFingerprint);

/// <summary>
/// UC017-F47: the store behind delete detection across the containers of one split read. A partition records the ids
/// each step read and how the run ended; the group is finalized once (the finalization row's primary key is the claim);
/// each step keeps the ids of its last complete group as the baseline the next group is diffed against. The seam is an
/// interface so the finalizer's gates are tested without a database.
/// </summary>
public interface IPartitionGroupRepository
{
    /// <summary>Add the ids one partition read for one step. Idempotent: a retried partition adds nothing twice.</summary>
    Task RecordSeenAsync(Guid projectId, Guid groupId, Guid stepId, IReadOnlyCollection<string> externalIds);

    /// <summary>The union of the ids every partition of the group read for the step.</summary>
    Task<HashSet<string>> LoadSeenAsync(Guid groupId, Guid stepId);

    Task RecordResultAsync(PartitionRunResult result);

    Task<IReadOnlyList<PartitionRunResult>> GetResultsAsync(Guid groupId);

    /// <summary>Claim the group's finalization. True for exactly one caller; false when the group was already claimed.</summary>
    Task<bool> TryClaimAsync(Guid groupId, Guid projectId, string planFingerprint);

    Task CompleteAsync(Guid groupId, string outcome, string? detailJson);

    Task<PartitionStepBaseline?> GetBaselineAsync(Guid projectId, Guid stepId);

    /// <summary>Make the group the step's baseline and drop the seen ids of the group it replaces.</summary>
    Task AdvanceBaselineAsync(Guid projectId, Guid stepId, Guid groupId, string planFingerprint);

    /// <summary>Drop the group's seen ids except those of the steps it is now the baseline for.</summary>
    Task ReleaseSeenAsync(Guid groupId, IReadOnlyCollection<Guid> keepStepIds);

    /// <summary>
    /// Bounded cleanup: seen ids that are no step's baseline and results recorded before the cutoff, which includes a
    /// group that never finalized (one whose containers ran on another server's database). Returns the rows removed.
    /// </summary>
    Task<int> PurgeStaleAsync(DateTime olderThanUtc);
}

/// <summary>Dapper implementation. Schema managed by DatabaseMigrator V38.</summary>
public class PartitionGroupRepository : BaseRepository, IPartitionGroupRepository
{
    public PartitionGroupRepository(DatabaseConfig config) : base(config) { }

    public async Task RecordSeenAsync(Guid projectId, Guid groupId, Guid stepId, IReadOnlyCollection<string> externalIds)
    {
        var ids = externalIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return;
        const string sql = @"
SET QUOTED_IDENTIFIER ON;
INSERT INTO PartitionSeenIds (ProjectId, GroupId, StepId, ExternalId)
SELECT DISTINCT @ProjectId, @GroupId, @StepId, j.e
  FROM OPENJSON(@Json) WITH (e NVARCHAR(400) '$') AS j
 WHERE NOT EXISTS (SELECT 1 FROM PartitionSeenIds s
                    WHERE s.GroupId = @GroupId AND s.StepId = @StepId AND s.ExternalId = j.e);";
        // CreateConnection() returns an already-open connection.
        using var db = CreateConnection();
        using var tx = db.BeginTransaction();
        try
        {
            foreach (var chunk in ids.Chunk(1000))
                await db.ExecuteAsync(sql, new
                {
                    ProjectId = projectId,
                    GroupId = groupId,
                    StepId = stepId,
                    Json = System.Text.Json.JsonSerializer.Serialize(chunk)
                }, tx, commandTimeout: _config.CommandTimeout);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<HashSet<string>> LoadSeenAsync(Guid groupId, Guid stepId)
    {
        var rows = await QueryAsync<string>(
            "SELECT ExternalId FROM PartitionSeenIds WHERE GroupId = @groupId AND StepId = @stepId;",
            new { groupId, stepId });
        return new HashSet<string>(rows, StringComparer.Ordinal);
    }

    public Task RecordResultAsync(PartitionRunResult result) => ExecuteAsync(@"
INSERT INTO PartitionRunResults
    (GroupId, PartitionIndex, PartitionCount, RunId, ProjectId, Succeeded, AllStepsCompleteRead, ContainerKey, ArmedStepIds)
VALUES
    (@GroupId, @PartitionIndex, @PartitionCount, @RunId, @ProjectId, @Succeeded, @AllStepsCompleteRead, @ContainerKey, @ArmedStepIds);",
        result);

    public async Task<IReadOnlyList<PartitionRunResult>> GetResultsAsync(Guid groupId) =>
        (await QueryAsync<PartitionRunResult>(@"
SELECT GroupId, PartitionIndex, PartitionCount, RunId, ProjectId, Succeeded, AllStepsCompleteRead, ContainerKey, ArmedStepIds, RecordedAt
  FROM PartitionRunResults WHERE GroupId = @groupId ORDER BY PartitionIndex, RecordedAt;", new { groupId })).ToList();

    public async Task<bool> TryClaimAsync(Guid groupId, Guid projectId, string planFingerprint)
    {
        try
        {
            var inserted = await ExecuteAsync(@"
INSERT INTO PartitionGroupFinalizations (GroupId, ProjectId, PlanFingerprint, Outcome)
SELECT @groupId, @projectId, @planFingerprint, N'Claimed'
 WHERE NOT EXISTS (SELECT 1 FROM PartitionGroupFinalizations WITH (UPDLOCK, HOLDLOCK) WHERE GroupId = @groupId);",
                new { groupId, projectId, planFingerprint });
            return inserted == 1;
        }
        // Two finishers racing past the NOT EXISTS: the primary key decides, and losing is not an error.
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            return false;
        }
    }

    public Task CompleteAsync(Guid groupId, string outcome, string? detailJson) => ExecuteAsync(@"
UPDATE PartitionGroupFinalizations SET Outcome = @outcome, DetailJson = @detailJson, FinalizedAt = SYSUTCDATETIME()
 WHERE GroupId = @groupId;", new { groupId, outcome, detailJson });

    public Task<PartitionStepBaseline?> GetBaselineAsync(Guid projectId, Guid stepId) =>
        QuerySingleOrDefaultAsync<PartitionStepBaseline>(@"
SELECT ProjectId, StepId, GroupId, PlanFingerprint FROM PartitionStepBaselines
 WHERE ProjectId = @projectId AND StepId = @stepId;", new { projectId, stepId });

    public async Task AdvanceBaselineAsync(Guid projectId, Guid stepId, Guid groupId, string planFingerprint)
    {
        const string sql = @"
DECLARE @old UNIQUEIDENTIFIER = (SELECT GroupId FROM PartitionStepBaselines WITH (UPDLOCK, HOLDLOCK)
                                  WHERE ProjectId = @projectId AND StepId = @stepId);
IF @old IS NULL
    INSERT INTO PartitionStepBaselines (ProjectId, StepId, GroupId, PlanFingerprint) VALUES (@projectId, @stepId, @groupId, @planFingerprint);
ELSE
    UPDATE PartitionStepBaselines SET GroupId = @groupId, PlanFingerprint = @planFingerprint, UpdatedAt = SYSUTCDATETIME()
     WHERE ProjectId = @projectId AND StepId = @stepId;
IF @old IS NOT NULL AND @old <> @groupId
    DELETE FROM PartitionSeenIds WHERE GroupId = @old AND StepId = @stepId;";
        using var db = CreateConnection();
        using var tx = db.BeginTransaction();
        try
        {
            await db.ExecuteAsync(sql, new { projectId, stepId, groupId, planFingerprint }, tx, commandTimeout: _config.CommandTimeout);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public Task ReleaseSeenAsync(Guid groupId, IReadOnlyCollection<Guid> keepStepIds) =>
        keepStepIds.Count == 0
            ? ExecuteAsync("DELETE FROM PartitionSeenIds WHERE GroupId = @groupId;", new { groupId })
            : ExecuteAsync("DELETE FROM PartitionSeenIds WHERE GroupId = @groupId AND StepId NOT IN @keep;",
                new { groupId, keep = keepStepIds.ToArray() });

    public async Task<int> PurgeStaleAsync(DateTime olderThanUtc)
    {
        // A baseline's seen ids are never stale: they are what the next group of the plan is diffed against.
        var seen = await ExecuteAsync(@"
DELETE s FROM PartitionSeenIds s
 WHERE s.RecordedAt < @olderThanUtc
   AND NOT EXISTS (SELECT 1 FROM PartitionStepBaselines b
                    WHERE b.ProjectId = s.ProjectId AND b.StepId = s.StepId AND b.GroupId = s.GroupId);",
            new { olderThanUtc });
        var results = await ExecuteAsync(
            "DELETE FROM PartitionRunResults WHERE RecordedAt < @olderThanUtc;", new { olderThanUtc });
        return seen + results;
    }
}
