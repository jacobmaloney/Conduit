-- ════════════════════════════════════════════════════════════════════════════
--  IC → Conduit PARITY-PROOF IMPORT  (one-off, REMOVABLE, REVERSIBLE)
--  Mirrors IcParityImportService.ImportFromIdentityCenterAsync exactly, in SQL,
--  so the proof runs without launching the app. Reads the REAL IdentityCenter
--  "Domain.local" project graph (read-only) and writes it into Conduit as the
--  sentinel project "[IC-IMPORT] Domain.local" with NEW GUIDs.
--
--  Both DBs are on the same instance (192.168.1.56), so this is a cross-DB query.
--  IC database is read with three-part names; ONLY the Conduit DB is written.
--
--  REMOVAL (exact) — note ESCAPE '\': the literal '[' is a LIKE wildcard set opener,
--  so an unescaped LIKE '[IC-IMPORT]%' matches the WRONG rows. Always escape it:
--    DELETE m FROM Conduit.dbo.AttributeMappings m JOIN Conduit.dbo.SyncProjects p ON m.SyncProjectId=p.Id WHERE p.Name LIKE '\[IC-IMPORT\]%' ESCAPE '\';
--    DELETE s FROM Conduit.dbo.SyncProjectScopes s JOIN Conduit.dbo.SyncProjects p ON s.SyncProjectId=p.Id WHERE p.Name LIKE '\[IC-IMPORT\]%' ESCAPE '\';
--    DELETE st FROM Conduit.dbo.WorkflowSteps st JOIN Conduit.dbo.Workflows w ON st.WorkflowId=w.Id JOIN Conduit.dbo.SyncProjects p ON w.SyncProjectId=p.Id WHERE p.Name LIKE '\[IC-IMPORT\]%' ESCAPE '\';
--    DELETE w FROM Conduit.dbo.Workflows w JOIN Conduit.dbo.SyncProjects p ON w.SyncProjectId=p.Id WHERE p.Name LIKE '\[IC-IMPORT\]%' ESCAPE '\';
--    DELETE FROM Conduit.dbo.SyncProjects WHERE Name LIKE '\[IC-IMPORT\]%' ESCAPE '\';
-- ════════════════════════════════════════════════════════════════════════════
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;   -- required: SyncProjects carries a filtered UNIQUE index
SET ANSI_NULLS ON;

DECLARE @icName        nvarchar(200) = N'Domain.local';
DECLARE @sentinelName  nvarchar(200) = N'[IC-IMPORT] ' + @icName;
DECLARE @placeholderTenant uniqueidentifier = '11111111-1111-1111-1111-111111111111';

-- Idempotency: bail if already imported.
IF EXISTS (SELECT 1 FROM Conduit.dbo.SyncProjects WHERE Name = @sentinelName)
BEGIN
    PRINT 'Already imported: ' + @sentinelName + ' (skipped).';
    RETURN;
END

DECLARE @icProjectId uniqueidentifier =
    (SELECT TOP 1 Id FROM IdentityCenter15.dbo.SyncProjects WHERE Name = @icName);
IF @icProjectId IS NULL
BEGIN
    RAISERROR('IC project not found: %s', 16, 1, @icName);
    RETURN;
END

BEGIN TRAN;

-- ── 1) Project ───────────────────────────────────────────────────────────────
DECLARE @newProjectId uniqueidentifier = NEWID();
INSERT INTO Conduit.dbo.SyncProjects
    (Id, Name, Description, SourceTenantId, SinkTenantId, ObjectClass,
     IsEnabled, IsRunning, TotalRuns, SuccessfulRuns, FailedRuns,
     CreatedAt, LastModified, SkipUnchanged)
SELECT
    @newProjectId,
    @sentinelName,
    N'REMOVABLE 1:1 import of IdentityCenter project ''' + @icName + N'''. Proves IC<->Conduit table+page parity.',
    @placeholderTenant, @placeholderTenant,
    ISNULL((SELECT TOP 1 ObjectClass FROM IdentityCenter15.dbo.SyncWorkflows WHERE SyncProjectId=@icProjectId ORDER BY ExecutionOrder), 'user'),
    0, 0, 0, 0, 0, SYSUTCDATETIME(), SYSUTCDATETIME(), 1;

-- ── 2) Workflows (IC SyncWorkflows → Conduit Workflows) ──────────────────────
DECLARE @wfMap TABLE (IcId uniqueidentifier, NewId uniqueidentifier, Ordinal int);
INSERT INTO @wfMap (IcId, NewId, Ordinal)
SELECT Id, NEWID(), ROW_NUMBER() OVER (ORDER BY ExecutionOrder) - 1
FROM IdentityCenter15.dbo.SyncWorkflows WHERE SyncProjectId = @icProjectId;

INSERT INTO Conduit.dbo.Workflows (Id, SyncProjectId, Name, Description, Ordinal, Enabled, CreatedAt, ModifiedAt)
SELECT m.NewId, @newProjectId, w.Name, w.Description, m.Ordinal, w.IsEnabled, SYSUTCDATETIME(), SYSUTCDATETIME()
FROM IdentityCenter15.dbo.SyncWorkflows w JOIN @wfMap m ON m.IcId = w.Id;

-- ── 3) Steps (IC SyncSteps → Conduit WorkflowSteps; "Upsert" → "Mapping") ────
DECLARE @stepMap TABLE (IcId uniqueidentifier, NewId uniqueidentifier, NewWfId uniqueidentifier, Ordinal int);
INSERT INTO @stepMap (IcId, NewId, NewWfId, Ordinal)
SELECT s.Id, NEWID(), m.NewId,
       ROW_NUMBER() OVER (PARTITION BY s.SyncWorkflowId ORDER BY s.ExecutionOrder) - 1
FROM IdentityCenter15.dbo.SyncSteps s JOIN @wfMap m ON m.IcId = s.SyncWorkflowId;

INSERT INTO Conduit.dbo.WorkflowSteps (Id, WorkflowId, Name, StepType, ObjectClass, Ordinal, Enabled, CreatedAt, ModifiedAt)
SELECT sm.NewId, sm.NewWfId, s.Name,
       CASE s.StepType WHEN 'Upsert' THEN 'Mapping' ELSE ISNULL(s.StepType,'Mapping') END,
       s.ObjectClass, sm.Ordinal, s.IsEnabled, SYSUTCDATETIME(), SYSUTCDATETIME()
FROM IdentityCenter15.dbo.SyncSteps s JOIN @stepMap sm ON sm.IcId = s.Id;

-- ── 4) Scopes (IC step columns → Conduit SyncProjectScopes row) ──────────────
INSERT INTO Conduit.dbo.SyncProjectScopes
    (Id, SyncProjectId, WorkflowStepId, BaseDN, LdapFilter, IncludedBaseDNs, ExcludedBaseDNs, PageSize, IncludeDeleted, CreatedAt, LastModified)
SELECT NEWID(), @newProjectId, sm.NewId, s.SearchBase, s.LdapFilter, s.SearchBases, s.ExcludedSearchBases,
       ISNULL(s.LdapPageSize, 1000), 0, SYSUTCDATETIME(), SYSUTCDATETIME()
FROM IdentityCenter15.dbo.SyncSteps s JOIN @stepMap sm ON sm.IcId = s.Id;

-- ── 5) Mappings (IC AttributeMappings → Conduit AttributeMappings) ───────────
--      Target→Sink rename; TransformationType 'Direct' → NULL (plain passthrough),
--      else carry the expression (preferred) or the type name.
INSERT INTO Conduit.dbo.AttributeMappings
    (Id, SyncProjectId, WorkflowStepId, SourceAttribute, SinkAttribute, TransformExpr, IsRequired, SortOrder)
SELECT NEWID(), @newProjectId, sm.NewId, m.SourceAttribute, m.TargetAttribute,
       CASE
           WHEN m.TransformationExpression IS NOT NULL AND LEN(LTRIM(m.TransformationExpression)) > 0
                THEN m.TransformationExpression
           WHEN m.TransformationType IS NULL OR m.TransformationType = 'Direct' THEN NULL
           ELSE m.TransformationType
       END,
       m.IsRequired, m.ExecutionOrder
FROM IdentityCenter15.dbo.AttributeMappings m JOIN @stepMap sm ON sm.IcId = m.SyncStepId;

COMMIT TRAN;
PRINT 'Imported: ' + @sentinelName + '  (project ' + CONVERT(varchar(36),@newProjectId) + ')';
