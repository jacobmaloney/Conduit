-- Disposable proof of the Task-1 per-class migration, run on a CLONE of a real flat
-- project so Jacob's actual data is never touched. Clones certification-center (12-step,
-- 1 workflow) into "[MIG-TEST] ..." then re-groups its steps into per-class workflows
-- exactly as IcParityImportService.MigrateProjectToPerClassAsync does, then prints the
-- before/after shape. The clone is deleted at the end (self-cleaning).
SET NOCOUNT ON; SET XACT_ABORT ON; SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON;

DECLARE @srcId uniqueidentifier = 'B8DB3CAB-25E9-4CD6-B2E2-8E751BD0AA16';   -- the 12-step flat project
DECLARE @cloneId uniqueidentifier = NEWID();
DECLARE @cloneWfId uniqueidentifier = NEWID();

BEGIN TRAN;

-- Clone project (tagged, disabled).
INSERT INTO SyncProjects (Id, Name, Description, SourceTenantId, SinkTenantId, ObjectClass,
    IsEnabled, IsRunning, TotalRuns, SuccessfulRuns, FailedRuns, CreatedAt, LastModified, SkipUnchanged)
SELECT @cloneId, N'[MIG-TEST] flat clone', N'disposable', SourceTenantId, SinkTenantId, ObjectClass,
    0,0,0,0,0, SYSUTCDATETIME(), SYSUTCDATETIME(), SkipUnchanged
FROM SyncProjects WHERE Id=@srcId;

-- Clone the single workflow.
INSERT INTO Workflows (Id, SyncProjectId, Name, Description, Ordinal, Enabled, CreatedAt, ModifiedAt)
SELECT @cloneWfId, @cloneId, Name, Description, Ordinal, Enabled, SYSUTCDATETIME(), SYSUTCDATETIME()
FROM Workflows WHERE SyncProjectId=@srcId;

-- Clone its steps (new ids), preserving ordinal/type/class.
DECLARE @stepClone TABLE (NewId uniqueidentifier, Ordinal int, ObjectClass nvarchar(50));
INSERT INTO WorkflowSteps (Id, WorkflowId, Name, StepType, ObjectClass, Ordinal, Enabled, CreatedAt, ModifiedAt)
OUTPUT inserted.Id, inserted.Ordinal, inserted.ObjectClass INTO @stepClone
SELECT NEWID(), @cloneWfId, s.Name, s.StepType, s.ObjectClass, s.Ordinal, s.Enabled, SYSUTCDATETIME(), SYSUTCDATETIME()
FROM WorkflowSteps s JOIN Workflows w ON s.WorkflowId=w.Id WHERE w.SyncProjectId=@srcId;

PRINT '--- BEFORE (flat: 1 workflow / N steps) ---';
SELECT (SELECT COUNT(*) FROM Workflows WHERE SyncProjectId=@cloneId) AS Workflows,
       (SELECT COUNT(*) FROM WorkflowSteps st JOIN Workflows w ON st.WorkflowId=w.Id WHERE w.SyncProjectId=@cloneId) AS Steps;

-- ── MIGRATE: one workflow per distinct object class, re-parent steps ──────────
-- First-seen class order.
DECLARE @classOrder TABLE (Cls nvarchar(50), Seq int);
INSERT INTO @classOrder (Cls, Seq)
SELECT Cls, ROW_NUMBER() OVER (ORDER BY MinOrd) - 1
FROM (SELECT ObjectClass Cls, MIN(Ordinal) MinOrd FROM @stepClone GROUP BY ObjectClass) z;

-- Re-use the original workflow as the FIRST per-class workflow.
DECLARE @firstCls nvarchar(50) = (SELECT Cls FROM @classOrder WHERE Seq=0);
UPDATE Workflows SET Name = @firstCls + N' Upsert Sync', Ordinal = 0,
    Description = N'[PER-CLASS-MIGRATION] origWf=' + CONVERT(nvarchar(36),@cloneWfId) + N';origName=Full sync;origOrd=0'
WHERE Id=@cloneWfId;

-- Create the remaining per-class workflows.
DECLARE @wfForClass TABLE (Cls nvarchar(50), WfId uniqueidentifier);
INSERT INTO @wfForClass (Cls, WfId) SELECT @firstCls, @cloneWfId;

INSERT INTO Workflows (Id, SyncProjectId, Name, Description, Ordinal, Enabled, CreatedAt, ModifiedAt)
OUTPUT inserted.Id, inserted.Ordinal INTO @wfForClass (WfId, Cls)  -- (Cls col reused as ord temporarily; corrected below)
SELECT NEWID(), @cloneId, c.Cls + N' Upsert Sync', N'[PER-CLASS-MIGRATION] origWf=' + CONVERT(nvarchar(36),@cloneWfId) + N';origName=Full sync;origOrd=0',
       c.Seq, 1, SYSUTCDATETIME(), SYSUTCDATETIME()
FROM @classOrder c WHERE c.Seq > 0;

-- The OUTPUT above can't easily map class names; rebuild @wfForClass cleanly by name.
DELETE FROM @wfForClass;
INSERT INTO @wfForClass (Cls, WfId)
SELECT LEFT(Name, LEN(Name) - LEN(N' Upsert Sync')), Id FROM Workflows WHERE SyncProjectId=@cloneId;

-- Re-parent each step under its class workflow, re-ordinal within class.
;WITH ranked AS (
    SELECT sc.NewId, sc.ObjectClass,
           ROW_NUMBER() OVER (PARTITION BY sc.ObjectClass ORDER BY sc.Ordinal) - 1 AS NewOrd
    FROM @stepClone sc
)
UPDATE st SET st.WorkflowId = wf.WfId, st.Ordinal = r.NewOrd
FROM WorkflowSteps st
JOIN ranked r ON r.NewId = st.Id
JOIN @wfForClass wf ON wf.Cls = r.ObjectClass;

PRINT '--- AFTER (per-class: N workflows) ---';
SELECT w.Ordinal WO, w.Name WF, COUNT(s.Id) Steps
FROM Workflows w LEFT JOIN WorkflowSteps s ON s.WorkflowId=w.Id
WHERE w.SyncProjectId=@cloneId GROUP BY w.Ordinal, w.Name ORDER BY w.Ordinal;

-- ── self-clean: delete the disposable clone entirely ─────────────────────────
DELETE FROM AttributeMappings WHERE SyncProjectId=@cloneId;
DELETE FROM SyncProjectScopes WHERE SyncProjectId=@cloneId;
DELETE st FROM WorkflowSteps st JOIN Workflows w ON st.WorkflowId=w.Id WHERE w.SyncProjectId=@cloneId;
DELETE FROM Workflows WHERE SyncProjectId=@cloneId;
DELETE FROM SyncProjects WHERE Id=@cloneId;
PRINT '--- clone deleted (Jacob data untouched) ---';

COMMIT TRAN;
