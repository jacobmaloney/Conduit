> ⚠️ **RETIRED / SUPERSEDED (2026-06-04).** The full structural-parity plan below (making Conduit hold IC's entire sync-project model — run-history table, the ~20 governance columns, IC's governance step-types) is **NOT being executed.** Jacob locked a simpler architecture: **Conduit is a standalone free PUMP; IdentityCenter is just one target connector type that lands rows into IC's `Objects` or `Identities` table; ALL governance (incl. object↔identity correlation / PersonMatch) stays in IC.** Conduit keeps its own pump model (cursors, skip-unchanged, tombstones). The ONLY connector work that remains: the IC connector exposes its tables (`Objects`, `Identities`) as selectable source AND target endpoints (so `IC/Identities → IC/Objects` table-to-table is a normal sync; fuzzy correlation still = IC). This document is kept for the IC-side reference mapping (§1–§2) only; the parity plan (§4) is historical. See memory `project_conduit_standalone_product_split_2026_06_03.md`.

# Sync Architecture Blueprint — IdentityCenter (gold standard) vs Conduit (parity target)

**Status:** Analysis only. No code changed. Jacob to validate §2 against intent.
**Authored:** 2026-06-04 (audit of both repos at current working trees).
**Scope:** the sync-project subsystem only — data model, orchestrator behavior, and the precise gap Conduit must close to *hold and run* an IC-style sync project.

Citations are `file:line` against:
- IC = `C:/Users/jacob/source/repos/IdentityCenter/Software/`
- Conduit = `C:/Users/jacob/source/repos/Conduit/`

> **One-line conclusion up front.** Conduit is NOT a thin copy of IC's sync engine — it is a *second, slimmer engine sharing DNA*. The Phase-7 work (V17) already gave Conduit IC's project→workflow→step→mapping/scope **shape**, and the multi-select scope port (V21) matched IC's 4-state OU model. The remaining gap is (a) Conduit's run-history is **flat** (no per-step run table), (b) Conduit's `SyncProjects` is **missing ~20 governance/behavior columns** IC routes on, and (c) Conduit's orchestrator runs **6 step types** vs IC's **~9 + 4 ProjectType modes**, several of which are Objects-coupled governance that should NOT be ported. Closing the gap is mostly *schema breadth + per-step run history*, not a rewrite.

---

## 1. IC sync — THE DATA MODEL (gold standard)

All DDL below is from `DataAccessLibrary/Migrations/Scripts/V004__InitialSchema.sql` (the 105-table baseline) plus the column-add migrations noted. IC migrations are Dapper SQL scripts, applied + recorded by `DatabaseMigrationService` (checksums recorded, never validated — old scripts are safe to read but never edit on an existing DB).

### 1.1 The FK graph (the spine)

```
DirectoryConnections
   ▲  ▲
   │  └──────────────── TargetConnectionId (nullable; null = write to Objects store)
   │
SyncProjects ──1:N──► SyncWorkflows ──1:N──► SyncSteps ──1:N──► AttributeMappings
   │  (V004:1383)        (V004:1994)           (V004:2237)         (V004:2323)
   │                                              │
   │                                              ├─ scope columns (SearchBases / ExcludedSearchBases / SearchScope)
   │                                              ├─ SyncStepScripts (pre/post script hooks, V004:2384)
   │                                              └─ SyncStepTags ──► Tags
   │
   ├──1:N──► SyncProjectRuns (V004:1963) ──1:N──► SyncStepRuns (V004:2352)
   │                              │
   │                              └──1:N──► PostSyncTasks (V004:2215)
   │
   ├──1:N──► SyncProjectChains (self-referential project→project chaining, V004:1942)
   └── SourceSyncProjectId (self-FK, for template-derived projects)

-- LEGACY PARALLEL MODEL (pre-workflow, still in schema, mostly dormant):
SyncProjects ──1:N──► InternalSyncSteps (V004:1919)  [project-level steps, NOT under a workflow]
InternalSyncRuns / InternalSyncSteps / InternalSyncStepMappings / InternalSyncStepRuns
```

**Key reading:** The *live* model is `SyncProjects → SyncWorkflows → SyncSteps → AttributeMappings`. The `InternalSync*` family is an older project-level step model that predates workflows; it is still created/seeded for some internal projects (V045 seeds default internal sync projects) but the orchestrator's main path walks the workflow tree. Treat `InternalSync*` as **legacy, do-not-port** unless a specific internal project depends on it.

### 1.2 `SyncProjects` — the project header (V004:1383, +V011, +V127 defaults)

| Column | Type | Meaning / why it matters |
|---|---|---|
| `Id` | uniqueidentifier PK | |
| `Name`, `Description` | nvarchar | |
| `SourceConnectionId` | uniqueidentifier NULL → DirectoryConnections | the read side |
| `TargetConnectionId` | uniqueidentifier NULL → DirectoryConnections | **null = write to internal Objects store**; non-null = external sink (see §2 sink factory) |
| `ProjectType` | nvarchar(50) NOT NULL | **THE primary router.** Values: `ObjectSync`, `PersonMatch`, `PersonCreate`, `HRImport`. Default `ObjectSync` (V127:101). |
| `SyncDirection` | nvarchar(50) NULL (V011) | `Inbound`/`Outbound` — added late; `Inbound` default |
| `IsTemplateMode` | bit | project is a reusable template |
| `IdentityMatchingStrategy` | nvarchar(50) | how source objects match to Identities |
| `ConflictResolutionStrategy` | nvarchar(50) NOT NULL | `SourceWins` default (V127:71) |
| `AutoCreateIdentities` | bit | create a Person/Identity when none matches |
| `EnableManagerAssignment` | bit | run manager-resolution post step |
| `SourceSyncProjectId` | uniqueidentifier NULL self-FK | template lineage |
| `IsBuiltIn`, `IsReadOnly` | bit | seeded/system projects can't be edited |
| `MinMatchConfidenceThreshold` | int (default 75) | person-match gating |
| `PauseOnError`, `MaxErrorsBeforePause` | bit/int | error-budget circuit breaker |
| `Priority` | int (default 5) | scheduler ordering |
| `LogLevel` | nvarchar(20) | per-project trace verbosity (drives the trace-log buffer) |
| `CronSchedule` | nvarchar(100) | Quartz schedule |
| `IsEnabled`, `IsRunning` | bit | lifecycle flags |
| `EnablePreSyncIndexing` | bit (V004:3570 ALTER) | rebuild SQL indexes/stats before a big run |
| run-stats: `LastSuccessfulRunAt`,`LastRunAt`,`NextScheduledRunAt`,`TotalExecutions`,`SuccessfulExecutions`,`FailedExecutions` | | denormalized counters shown in UI |
| audit: `CreatedAt/By`,`ModifiedAt/By` | | |

### 1.3 `SyncWorkflows` — workflow per object-class within a project (V004:1994)

| Column | Type | Meaning |
|---|---|---|
| `Id` PK, `SyncProjectId` FK (CASCADE) | | |
| `Name`,`Description` | | |
| `ObjectClass` | nvarchar(100) NOT NULL | the workflow's primary object class (user/group/computer/…) |
| `WorkflowType` | nvarchar(50) | `ObjectSync` default (V128:239) |
| `ExecutionOrder` | int | **workflows run in ascending order** |
| `IsEnabled`,`ContinueOnError` | bit | |
| `MaxExecutionTimeMinutes` | int | per-workflow timeout |

### 1.4 `SyncSteps` — the unit of work (V004:2237) — **the richest table**

| Column | Type | Meaning |
|---|---|---|
| `Id` PK, `SyncWorkflowId` FK (CASCADE) | | |
| `Name`,`Description`,`ExecutionOrder` | | **steps run in ascending order within a workflow** |
| `ObjectClass` | nvarchar(100) NOT NULL | step's object class; also a *routing discriminator* — `"GroupMembership"`, `"ManagerLookup"`, `"GroupOwnerLookup"` route to specialized processors (see §2) |
| `StepType` | nvarchar(50) NULL | routing discriminator: `Lookup`, `LicenseSync`, `SignInLogSync`, `UsageReportSync`, `AppRoleSync`, `HRImport`, `IdentityManagerLookup`, (null/blank = normal upsert) |
| `MarkAsType` | nvarchar(100) | tag objects as a derived type after sync |
| **`LdapFilter`** | nvarchar(max) | the per-step source filter |
| **`SearchBase`** | nvarchar(2000) | legacy single base |
| **`SearchBases`** | nvarchar(4000) | **multi-select included subtrees (JSON-ish list)** — the 4-state model's *Included* set |
| **`ExcludedSearchBases`** | nvarchar(4000) | **multi-select blocked subtrees** — the *Blocked* set |
| **`SearchScope`** | nvarchar(20) NOT NULL | `Subtree` default (V128:207) |
| `IsEnabled`,`ContinueOnError`,`MaxExecutionTimeMinutes` | | |
| `DependsOnStepIds` | nvarchar(1000) | intra-workflow step dependencies |
| `ProcessDeletions` | bit | enable delete/disable handling for this step |
| `UpdateExisting` | bit | update vs insert-only |
| `BatchSize` | int (default 100) | sink write batching |
| `LdapPageSize` | int (default 1000) | source paged-read size |
| `Configuration` | nvarchar(max) | free-form JSON for step-type-specific config (e.g. lookup `ManagerSourceId→ManagerObjectId`) |
| `EnableIdentityMatching`,`IdentityMatchingAttribute` | bit/nvarchar | object↔Identity matching |
| `InheritWorkflowTags` | bit | inherit workflow's Tag set |
| `SkipPersonMatching`,`EnablePersonMatching`,`CreatePersonIfNotFound` | bit | person-creation behavior per step |
| `CreatedAt`,`ModifiedAt` | | |

The 4-state scope (Included / Inherited / Blocked / NotSelected) is *derived* from `SearchBases` + `ExcludedSearchBases`; Inherited is computed in the UI, never stored. `SyncStep.GetSearchBaseList()` (model helper) parses `SearchBases`.

### 1.5 `AttributeMappings` — per-step mapping (V004:2323)

Per-**step** (`SyncStepId` FK, CASCADE). Fields: `SourceAttribute`, `SourceDisplayName`, `DataType`, `TargetType`, `TargetAttribute`, `TransformationType`, `TransformationExpression`, `DefaultValue`, `IsRequired`, **matching fields** (`UseForMatching`, `MatchWeight`, `UseFuzzyMatch`, `FuzzyMatchThreshold`, `FuzzyMatchAlgorithm`), `ExecutionOrder`, `IsEnabled`. The fuzzy-match columns make a mapping double as a person-match rule — IC mappings are richer than pure rename.

### 1.6 Run history — two-level (the per-step granularity the UI shows)

- **`SyncProjectRuns`** (V004:1963): one row per run. `TriggerType`, `TriggeredBy`, `StartedAt/CompletedAt`, `Status`, `ProgressPercentage`, `CurrentStep`, `TotalSteps/CompletedSteps/FailedSteps/SkippedSteps`, `TotalObjectsProcessed/Created/Updated/Deleted`, `TotalErrors`, `TotalPersonsCreated`, `ErrorMessage`, `ExecutionLog`, `DurationSeconds`.
- **`SyncStepRuns`** (V004:2352): **one row per step per run** (FK to both SyncProjectRuns and SyncSteps). `StepName`, `ObjectClass`, timing, `Status`, and per-step metrics: `ObjectsQueried/Processed/Created/Updated/Deleted/Skipped`, `ErrorCount`, `PersonsMatched/Created`, `PersonMatchingSkipped`, `AvgProcessingTimeMs`. **This is what powers the drill-down "per step: queried/created/updated/errors" UI.**
- **`PostSyncTasks`** (V004:2215): post-run async tasks (index optimization, notifications) tracked separately.

### 1.7 The object-class catalog & auto-generation

`WebPortal/Services/AutoSyncProjectGenerator.cs` defines the tiered class catalog per connector:
- AD: **Core(4)** = `user, group, computer, contact` (`:74`); **Infrastructure(20)** (`:80`); **Full(24)** = Core+Infra (`:91`).
- Entra: Core(2)/Security(7)/Full(10); SharePoint Core/Collaboration/Full; SCIM/Okta/Google/AWS/LDAP/Database each have Core/Full lists (`:95`–`:163`).
- `ResolveObjectClassesForTemplate(connectionType, mode)` (`:200`) picks the list; `GenerateAutoSyncProjectCoreAsync` builds a project with one **workflow per object class**, each with an upsert step + auto-mappings.
`WebPortal/Services/AutoAttributeMappingService.cs` supplies the per-class mapping templates.

---

## 2. IC sync — BEHAVIOR (capture the INTENT — Jacob to confirm)

Orchestrator: `DataAccessLibrary/Services/SyncProjectOrchestrator.cs` (~4600 lines). Entry: `ExecuteSyncProjectAsync(syncProjectId, triggerType, triggeredBy, ct, selectedWorkflowIds?)` (`:188`).

### 2.1 Lifecycle of one run, in plain language

1. **Load the whole tree** with Dapper (`:204`–`:267`): the project, its source `DirectoryConnection`, all enabled workflows ordered by `ExecutionOrder`, each workflow's steps ordered by `ExecutionOrder`, each step's `AttributeMappings` (ordered) and `StepTags` (+Tag detail).
2. **Resolve the write SINK fail-fast** (`:299`) via `SyncSinkFactory.ResolveSinkAsync(project)` BEFORE flipping `IsRunning` or creating a run row. `TargetConnectionId == null` → `IdentityStoreSink` (write to the `Objects` table — the historical behavior). Non-null external target → IC has **no external sink implemented**, so the factory throws here and the run fails clean with no partial work. (This is the IC↔Conduit seam: IC writes INTO its Objects store; an external push is Conduit's job.)
3. **Guards** (`:279`–`:289`): disabled project throws; already-`IsRunning` throws (no concurrent runs).
4. **Mark running + create the run row** (`:308`–`:336`), reset thread-safe counters, **start the trace-log buffer** (`:345`) keyed by run id (real-time streaming, persisted only on error).
5. **PROJECT-TYPE ROUTING** (`:354`):
   - `PersonMatch` / `PersonCreate` → delegate the entire run to `PersonMatchOrchestrator.ExecuteAsync` (`:365`); roll its result into run metrics; chain; return.
   - `HRImport` → delegate to `HRImportOrchestrator` (`:421`); the HR steps (`HRImport`, then `IdentityManagerLookup`) run inside that orchestrator.
   - otherwise (`ObjectSync`) → fall through to the workflow walk.
6. **Workflow walk** (`ExecuteWorkflowAsync`, `:1039`): for each enabled workflow (honoring `selectedWorkflowIds` for selective runs), iterate enabled steps in `ExecutionOrder`. Per-step error handling respects `step.ContinueOnError`/`workflow.ContinueOnError`; otherwise it rethrows and aborts the workflow. Progress % updated after each step.
7. **Step routing** (`ExecuteStepAsync`, `:1101`) — a dispatch ladder on `ObjectClass` then `StepType`:
   - `ObjectClass == "GroupMembership"` → `ProcessGroupMembershipStepAsync` (`:1119`) — resolves and bulk-upserts `ObjectGroupMemberships`.
   - `StepType == "Lookup"` or `ObjectClass == "ManagerLookup"` → if the workflow's object class is `group` or the step name mentions "Group Owner"/"Resolve Group", route to `ProcessGroupOwnerLookupStepAsync` (`:1142`); else `ProcessLookupStepAsync` (`:1145`). The lookup step's job is **reference resolution** — e.g. resolve each object's `ManagerSourceId` (a DN) into the local `ManagerObjectId` (a Guid), and write manager-resolution stats; config carries the source→target field pair (`:2268`+).
   - `ObjectClass == "GroupOwnerLookup"` → group owner lookup (`:1150`).
   - `StepType == "LicenseSync"` → `ProcessLicenseSyncStepAsync` (`:1157`) — pulls M365/Entra license data.
   - `StepType == "SignInLogSync"` → sign-in logs (`:1164`).
   - `StepType == "UsageReportSync"` → usage reports (`:1171`).
   - `StepType == "AppRoleSync"` → app-role assignments (`:1178`).
   - default (null/blank StepType, a normal object class) → the **upsert path**: query the directory with the step's `SearchBases`/`ExcludedSearchBases`/`SearchScope`/`LdapFilter`/`LdapPageSize`, apply `AttributeMappings`, bulk-upsert into the Objects store via the sink, optionally person-match / create Identity, honoring `UpdateExisting`/`ProcessDeletions`/`BatchSize`.
8. **Scope application** is per-step: `step.GetSearchBaseList()` yields the Included subtrees (each read as a Subtree base); `ExcludedSearchBases` prune any object at/under a blocked DN even when it falls under an Included base. This is the **4-state model in action**.
9. **Pre/post hooks**: `EnablePreSyncIndexing` → `ExecutePreSyncOptimizationAsync` (`:4425`); `SyncStepScripts` → pre/post PowerShell-style processing scripts (`:4173`/`:4252`, recorded in `SyncScriptExecutions`); after the run, `PostSyncNotificationAsync` (`:4623`) and `ExecuteProjectChainsAsync` (`:4537`) which fires downstream chained projects per `SyncProjectChains.TriggerCondition`.
10. **Finalize** (try/finally guarantees `IsRunning` cleanup): write `SyncStepRuns` rows per step, roll up totals into `SyncProjectRuns`, update project counters, persist the trace log on error.

### 2.2 The intent in one paragraph (Jacob: confirm or correct)

> An IC sync project is a **governance-aware ingest**: it reads one directory (scoped by a 4-state OU model per step), maps attributes (with fuzzy-match-capable mappings), writes objects into IC's own `Objects` store, and then does the *identity-governance* work that makes IC valuable — matching objects to People/Identities, creating Identities when configured, resolving managers and group owners into local GUIDs, pulling licenses/sign-ins/usage/app-roles, running pre/post scripts, and chaining to downstream projects. The `ProjectType` decides the *mode* (object sync vs person-match vs HR import); the step ladder decides the *kind of work* per step. The sink is almost always IC's own Objects store; pushing to an external system was deliberately left unimplemented in IC.

---

## 3. CONDUIT sync — current model + behavior

### 3.1 Conduit's tables (the C# migrator, `Conduit.DataAccess/DatabaseMigrator.cs`)

Conduit's schema is built by `GetRequiredMigrations()` — versioned C# `SchemaMigration` objects, recorded in `SchemaVersion`, applied transactionally (`:1066`). **There is no V19** (jumps V18→V20). Sync-relevant migrations:

- **V14** "V002 sync metadata" (`:580`): the symmetric-router core — `SyncProjects`, `SyncRuns`, `SyncRunLogs`, `AttributeMappings`, `ConnectionCredentials`, `SyncProjectScopes`.
- **V15** (`:729`): `SyncRuns.Cursor`+`IsIncremental` (delta sync); `SyncProjects.SourceCredentialName`/`SinkCredentialName`.
- **V16** (`:759`): `SyncRunAsyncJobs` (async-job framework for sinks like AWS SSO).
- **V17** "Phase 7 workflows + steps tree" (`:810`): adds **`Workflows`** + **`WorkflowSteps`**, optional `WorkflowStepId` FK on `AttributeMappings` + `SyncProjectScopes`, and **backfills one Default workflow + one Default Mapping step per existing project** (cursor at `:879`). This is the IC-shape port.
- **V20** (`:950`): `SinkRecordHashes` + `SyncProjects.SkipUnchanged` (sink-side skip-unchanged — a perf feature IC does NOT have).
- **V21** (`:1012`): IC-parity multi-select scope — `SyncProjectScopes.IncludedBaseDNs`+`ExcludedBaseDNs` (JSON DN arrays, mirroring IC `SearchBases`/`ExcludedSearchBases`), drops the project-only unique on scopes and replaces it with filtered uniques so **multiple scope rows per project** (one per step) are allowed.

Conduit `SyncProjects` columns (V14+V15+V20): `Id, WorkspaceId, Name, Description, SourceTenantId, SinkTenantId, ObjectClass, CronSchedule, IsEnabled, IsRunning, LastRunAt, LastRunStatus, LastRunId, NextScheduledRunAt, TotalRuns, SuccessfulRuns, FailedRuns, CreatedAt, LastModified, SourceCredentialName, SinkCredentialName, SkipUnchanged`. Model: `Conduit.Core/SyncModels/SyncProject.cs`.

Conduit `Workflows` (V17): `Id, SyncProjectId, Name, Description, Ordinal, Enabled, CreatedAt, ModifiedAt`.
Conduit `WorkflowSteps` (V17): `Id, WorkflowId, Name, StepType, Ordinal, Enabled, Configuration, CreatedAt, ModifiedAt`. StepType values in `WorkflowStepTypes` (`SyncProject.cs:263`): **Mapping, PersonMatch, PersonCreate, AssignManager, AssignGroupOwner, Custom**.
Conduit `AttributeMappings` (V14+V17): `Id, SyncProjectId, SourceAttribute, SinkAttribute, TransformExpr, IsRequired, SortOrder, WorkflowStepId`.
Conduit `SyncProjectScopes` (V14+V17+V21): `Id, SyncProjectId, WorkflowStepId, BaseDN, IncludedBaseDNs, ExcludedBaseDNs, LdapFilter, QueryExpression, PageSize, MaxObjects, IncludeDeleted, CreatedAt, LastModified`.

### 3.2 Conduit's FK graph

```
Tenants (= "Connected Systems")
   ▲  ▲ Source/SinkTenantId
SyncProjects ──1:N──► Workflows ──1:N──► WorkflowSteps
   │                                         │ (WorkflowStepId, nullable)
   │                                         ├──► AttributeMappings (or project-level when WorkflowStepId NULL)
   │                                         └──► SyncProjectScopes  (one per step; or project-level)
   │
   ├──1:N──► SyncRuns ──1:N──► SyncRunLogs   ◄── FLAT. No per-step run table.
   │            └──1:N──► SyncRunAsyncJobs
   └── SinkRecordHashes (per project+sink+externalId content/registry hash)
```

### 3.3 Conduit's behavior

Orchestrator: `Conduit.Sync/Orchestration/SyncProjectOrchestrator.cs` (1219 lines — bigger than the "947" in the brief because of Phase 2.2 tombstoning). Entry: `ExecuteAsync(projectId, triggeredBy, ct)` (`:82`).

1. Create a `SyncRun` row, claim `IsRunning` (CAS), register cancellation token (`:82`–`:136`). Robust IsRunning release on every path (Worf HIGH-1 fix).
2. `RunCoreAsync` (`:169`): resolve source+sink tenants → adapters via `ConnectorRegistry` → `CreateSource`/`CreateSink`. Push credential-name overrides onto `CredentialNameContext`.
3. Load enabled `Workflows` ordered by `Ordinal` (`:218`). If none, **legacy single-pass fallback** using project-level mappings+scope (`:227`). Else walk each workflow, each enabled step in `Ordinal` order (`:267`).
4. **Step routing** (`:275`) — a `switch` on `StepType`:
   - `Mapping` → `ExecuteMappingStepAsync` (`:474`) → `PumpAsync` (the real engine): recover incremental cursor, enumerate source with the step's (or project's) `SyncProjectScope`, `ApplyMappings` (rename + `AttributeTransformer` transform), batch-flush to the sink (bulk if `SupportsBulk`), optional skip-unchanged, optional **tombstone/delete-detection** (Phase 2.2, gated on a complete read + 50% cap on the IC sink).
   - `PersonMatch` → `ExecutePersonMatchStepAsync` (`:508`): calls `sink.MatchPersonAsync` per object **from the previous step's emitted batch** (inter-step state passed in-memory via `lastBatch`/`lastMatches`). Requires the sink to advertise `SupportsPersonMatch`.
   - `PersonCreate` → `ExecutePersonCreateStepAsync` (`:551`): `sink.CreatePersonAsync` on the misses from the preceding PersonMatch.
   - `AssignManager` → `ExecuteAssignManagerStepAsync` (`:601`): reads a `manager`/`managerDN`/`managerUpn` attribute off each object and calls `sink.AssignManagerAsync`.
   - `AssignGroupOwner` → (`:660`): same shape with `managedBy`/`owner`.
   - `Custom` → no-op placeholder (`:717`).
5. **Run-status rollup** (`:343`): derives Succeeded/PartialSuccess/Failed from in-memory step outcomes (Conduit has no `SyncStepRuns` to read back). Notable: a complete Mapping pass that reads **0 objects** is treated as **Failed** (anti-false-success sentinel, `:353`). Finalize stamps `SyncRuns` + `SyncProjects` counters.

**Crucial behavioral truths:**
- Conduit's person-aware steps (`PersonMatch`/`PersonCreate`/`AssignManager`/`AssignGroupOwner`) are **sink-capability calls**, not local governance — they only do anything if the SINK adapter implements them (today: the IdentityCenter sink). So in Conduit→IC topology these steps push the governance work *back into IC*.
- Inter-step data flow is **in-memory only** (`lastBatch`). There's no equivalent of IC's `DependsOnStepIds` or persisted intermediate state.
- Conduit's `ObjectClass` lives on the **project**, not the step (IC has it on both workflow and step). Conduit runs one object class per project.

---

## 4. THE GAP + PARITY PLAN

### 4.1 Table / column gap (what Conduit lacks to *hold* an IC project)

**A. `SyncProjects` — missing columns (pure-sync behavior, SHOULD port):**

| IC column | Conduit has? | Port? | Note |
|---|---|---|---|
| `ProjectType` (ObjectSync/PersonMatch/PersonCreate/HRImport) | ❌ | **Partial** | Conduit encodes mode via *step types*, not a project mode. For round-tripping IC projects, store it as metadata even if Conduit routes on steps. |
| `SyncDirection` | ❌ (implicit via source/sink) | metadata-only | Conduit's symmetric router makes direction = which tenant is source. Keep as a stored label for fidelity. |
| `ConflictResolutionStrategy` | ❌ | **Yes** | real sink behavior (SourceWins etc.). |
| `IdentityMatchingStrategy` | ❌ | governance | only meaningful when sink = IC; store as metadata. |
| `AutoCreateIdentities`, `EnableManagerAssignment` | ❌ | governance-as-steps | Conduit models these as PersonCreate/AssignManager steps already. Store flags for import fidelity. |
| `MinMatchConfidenceThreshold` | ❌ | governance | person-match gating; sink-side (IC). |
| `PauseOnError`, `MaxErrorsBeforePause` | ❌ | **Yes** | error-budget circuit breaker is pure-sync. |
| `Priority` | ❌ | **Yes** | scheduler ordering. |
| `LogLevel` | ❌ | **Yes** | Conduit logs at a fixed level; per-project verbosity is a real gap. |
| `IsTemplateMode`, `IsBuiltIn`, `IsReadOnly`, `SourceSyncProjectId` | ❌ | **Yes** | template lineage + system-project protection. |
| `EnablePreSyncIndexing` | ❌ | no | IC-internal SQL optimization of the Objects store; N/A to Conduit. |
| `MinMatchConfidenceThreshold`, person-match knobs | ❌ | governance | sink-side. |

**B. `SyncWorkflows` / `Workflows` — column gap:**

| IC `SyncWorkflows` | Conduit `Workflows` | Port? |
|---|---|---|
| `ObjectClass` | ❌ (object class is project-level) | **Yes** — to hold IC's per-workflow object class, Conduit needs object class on the workflow (or step). Today Conduit = one class per project. |
| `WorkflowType` | ❌ | metadata |
| `ContinueOnError` | ❌ | **Yes** — Conduit aborts the workflow on a thrown step; IC's continue-on-error is per workflow + per step. |
| `MaxExecutionTimeMinutes` | ❌ | **Yes** (timeout). |
| `ExecutionOrder` | ✅ `Ordinal` | ok |

**C. `SyncSteps` / `WorkflowSteps` — the biggest column gap:**

Conduit `WorkflowSteps` has only `Name, StepType, Ordinal, Enabled, Configuration`. IC `SyncSteps` carries **~25 columns**. Missing and **should port (pure-sync)**:
- `ObjectClass` on the step (Conduit has it on project only).
- `LdapFilter` — Conduit keeps the filter on the **scope** (`SyncProjectScopes.LdapFilter`), IC on the step. Functionally covered but located differently — a translation concern, not a hard gap.
- `BatchSize`, `LdapPageSize` — Conduit derives batch from sink capability + `scope.PageSize`. Per-step `BatchSize`/page-size knobs are missing.
- `ProcessDeletions` — Conduit gates deletes on sink capability (tombstone), not a per-step flag. To hold IC's per-step intent, add the flag.
- `UpdateExisting` — Conduit always upserts; IC can insert-only. Gap.
- `ContinueOnError` on the step. Gap.
- `DependsOnStepIds` — no Conduit equivalent (inter-step is in-memory order only). Gap if IC projects use dependencies.
- `MarkAsType`, `EnableIdentityMatching`/`IdentityMatchingAttribute`, `InheritWorkflowTags`, person-match flags, `SkipPersonMatching` — **governance, mostly sink-side**; store in `Configuration` JSON for fidelity rather than as columns.
- `MaxExecutionTimeMinutes` on step.

**D. `AttributeMappings` — column gap (pure-sync, SHOULD port):**

| IC | Conduit | Port? |
|---|---|---|
| `SourceAttribute` ✅, `Sink/TargetAttribute` ✅, transform ✅ (`TransformExpr` vs `TransformationType`+`TransformationExpression`), `IsRequired` ✅, order ✅ | | shape matches |
| `SourceDisplayName`, `DataType`, `TargetType` | ❌ | **Yes** (UI + typing fidelity) |
| `DefaultValue` | ❌ | **Yes** (IC `DefaultValue` vs Conduit's required-null behavior) |
| `UseForMatching`, `MatchWeight`, `UseFuzzyMatch`, `FuzzyMatchThreshold`, `FuzzyMatchAlgorithm` | ❌ | **governance** — these make an IC mapping a person-match rule. Sink-side (IC). Store for fidelity; Conduit doesn't fuzzy-match locally. |
| `IsEnabled`, `ModifiedAt` | ❌ | **Yes** (per-mapping enable). |

**E. Run history — the structural gap (pure-sync, MUST port):**

- IC has **`SyncStepRuns`** (per-step-per-run metrics). Conduit has **no per-step run table** — `SyncRuns` is flat and step outcomes live only in memory + free-text `SyncRunLogs`. **Conduit cannot reproduce IC's per-step drill-down UI.** This is the single most important schema gap to close.
- IC `SyncProjectRuns` has `CurrentStep`, `TotalSteps/Completed/Failed/Skipped`, `TotalObjectsDeleted`, `TotalPersonsCreated`, `ProgressPercentage`. Conduit `SyncRuns` has `ObjectsRead/Created/Updated/Skipped/Failed` + status/duration/cursor — **no Deleted, no PersonsCreated, no per-step counts, no progress %, no CurrentStep**.
- IC `PostSyncTasks` — no Conduit equivalent (Conduit's async work is `SyncRunAsyncJobs`, a different concept).

**F. Project chaining:** IC `SyncProjectChains` (project→project, trigger conditions) — **no Conduit equivalent.** Pure-sync orchestration; port if IC projects chain.

**G. Tables Conduit has that IC does NOT (do not remove, they're Conduit's edge):** `SinkRecordHashes` (skip-unchanged + delete-detection registry), `SyncRunAsyncJobs`, `ConnectionCredentials`, `SyncProjects.SkipUnchanged`, `SyncRuns.Cursor/IsIncremental`. These are *better-than-IC* and stay.

### 4.2 Behavioral gap

| Capability | IC | Conduit | Verdict |
|---|---|---|---|
| Project→Workflow→Step→Mapping tree | ✅ | ✅ (V17) | **parity** |
| Multi-select 4-state scope | ✅ (step columns) | ✅ (V21 scope JSON) | **parity** |
| Per-step object class | ✅ | ❌ (project-level) | **gap** |
| Step types | GroupMembership, Lookup(manager), GroupOwnerLookup, LicenseSync, SignInLogSync, UsageReportSync, AppRoleSync, HRImport, IdentityManagerLookup, upsert | Mapping, PersonMatch, PersonCreate, AssignManager, AssignGroupOwner, Custom | **divergent by design** — see below |
| ProjectType modes | 4 (orchestrator fan-out) | 0 (step-driven) | **design difference**; store as metadata |
| Manager/owner resolution | local GUID resolution into Objects store | sink-capability call (push to IC) | **divergent by design** |
| Continue-on-error (workflow+step) | ✅ | workflow-abort only | **gap** |
| Insert-only / UpdateExisting | ✅ | always upsert | **gap** |
| Pre/post scripts (`SyncStepScripts`) | ✅ | ❌ | governance/extensibility — likely no-port |
| Project chaining | ✅ | ❌ | **gap (pure-sync)** |
| Delete-detection / tombstoning | ❌ (per-step ProcessDeletions in Objects store) | ✅ (Phase 2.2, gated) | Conduit ahead |
| Incremental cursor | ❌ | ✅ | Conduit ahead |
| Skip-unchanged | ❌ | ✅ (V20) | Conduit ahead |

**The honest framing of step types:** IC's `LicenseSync`/`SignInLogSync`/`UsageReportSync`/`AppRoleSync`/`GroupMembership` steps **write into IC's own Objects/License/SignIn tables** — they are *governance ingest into IC*, the opposite of Conduit's job (push to an external sink). These should **NOT** be ported as Conduit step types. They belong on the IC *sink* side. IC's `Lookup`/`ManagerLookup` resolve references into the local Objects GUID space — also Objects-coupled. Conduit's `AssignManager`/`AssignGroupOwner` steps are the *push-side* equivalent and are correct as-is.

### 4.3 Objects-coupled (stays IC) vs pure-sync (must reach Conduit)

**Litmus (from the code-share decision):** does it reference `DataAccessLibrary.Models` / the `Objects` table / Identities? → governance, stays IC.

- **Stays IC (do NOT chase parity):** `ProjectType=PersonMatch/PersonCreate/HRImport` modes, `GroupMembership`/`Lookup`/`GroupOwnerLookup`/`LicenseSync`/`SignInLogSync`/`UsageReportSync`/`AppRoleSync`/`IdentityManagerLookup` step processors, fuzzy-match mapping columns, `AutoCreateIdentities`/`IdentityMatchingStrategy`/`MinMatchConfidenceThreshold`, `SyncStepScripts`, the `IdentityStoreSink`, `InternalSync*` legacy family.
- **Must reach Conduit (pure-sync):** per-step run history (`SyncStepRuns`-equivalent), richer `SyncRuns` counters (Deleted/PersonsCreated/per-step/progress/CurrentStep), per-step `ObjectClass`, `ContinueOnError` (workflow+step), `UpdateExisting`/insert-only, per-step `BatchSize`/page-size, `ProcessDeletions` flag, project chaining, error-budget (`PauseOnError`/`MaxErrorsBeforePause`), `Priority`, `LogLevel`, template lineage (`IsTemplateMode`/`IsBuiltIn`/`IsReadOnly`/`SourceSyncProjectId`), mapping fidelity columns (`DataType`/`TargetType`/`DefaultValue`/`IsEnabled`).

### 4.4 Concrete parity plan (phased; schema first, then behavior)

**Phase A — schema breadth (Conduit migrations V22–V24). Low risk, additive.**
- **V22 — per-step run history.** New `SyncStepRuns` table (FK `SyncRunId`, optional `WorkflowStepId`; columns mirror IC: StepName, ObjectClass, Started/Completed, Status, ObjectsRead/Created/Updated/Deleted/Skipped, ErrorCount, DurationMs). Backfill not needed (history is forward-only). *Effort: ~0.5 day schema + repo; ~0.5 day to make the orchestrator write a row per step (it already computes `StepResult` per step — persist it instead of only rolling up in memory).*
- **V23 — `SyncProjects` + `Workflows` + `WorkflowSteps` + `AttributeMappings` column breadth.** Add the "Yes" columns from §4.1 A–D. All additive with defaults. Add per-step `ObjectClass`, `ContinueOnError`, `UpdateExisting`, `ProcessDeletions`, `BatchSize`, `PageSize`; workflow `ObjectClass`/`ContinueOnError`/`MaxExecutionTimeMinutes`; project `ProjectType`(metadata)/`ConflictResolutionStrategy`/`PauseOnError`/`MaxErrorsBeforePause`/`Priority`/`LogLevel`/template flags; mapping `DataType`/`TargetType`/`DefaultValue`/`IsEnabled`. *Effort: ~1 day schema + model/repo updates.*
- **V24 — richer `SyncRuns` counters + `SyncRunSteps` progress fields + (optional) `SyncProjectChains`.** Add `ObjectsDeleted`, `PersonsCreated`, `CurrentStep`, `TotalSteps/CompletedSteps/FailedSteps/SkippedSteps`, `ProgressPercentage` to `SyncRuns`; add `SyncProjectChains` if chaining is in scope. *Effort: ~0.5–1 day.*

**Phase B — orchestrator behavior. Medium risk (the engine is the careful part).**
- Make `RunCoreAsync` persist a `SyncStepRuns` row per step (data already in `StepResult`). *~0.5 day.*
- Honor per-step `ContinueOnError` (currently a thrown step aborts the workflow) and per-workflow `ContinueOnError`. *~0.5 day.*
- Honor per-step `ObjectClass` (today the project's `ObjectClass` is passed to `source.EnumerateAsync`; switch to the step's when present) — this is what lets ONE Conduit project hold IC's multi-object-class workflows. *~0.5–1 day; touches the pump signature.*
- Honor `UpdateExisting` (insert-only) and per-step `BatchSize`/page-size. *~0.5 day.*
- Live progress %, CurrentStep, per-step counts into `SyncRuns`/`SyncStepRuns`. *~0.5 day.*
- (If chaining ported) post-run chain trigger. *~0.5 day.*

**Phase C — import/round-trip fidelity (optional, do last).**
- A translator that takes an IC `SyncProject` export and materializes the equivalent Conduit project/workflow/step/mapping/scope, storing IC-only governance knobs as `WorkflowSteps.Configuration` JSON + `SyncProjects` metadata columns so an IC project survives a round-trip even where Conduit doesn't *act* on the governance fields. *~1–2 days.*

**Total honest estimate:** Phase A ~2–2.5 days; Phase B ~3 days; Phase C ~1–2 days. **~6–8 days** for full structural-hold + run parity, excluding the governance step types that should stay IC.

**Risk flags:**
- The pump signature change (per-step ObjectClass) touches the hot loop and the tombstone/skip-unchanged logic — regression-test delete-detection after.
- `SyncStepRuns` writes add per-step DB round-trips; batch them or write at step end only (IC writes once per step, fine).
- Do NOT let the governance step types leak into Conduit's `WorkflowStepTypes` — that's the false-parity trap. Keep them IC-side on the sink.
- Conduit's `ObjectClass`-on-project assumption is load-bearing in `PumpAsync` and the connector `EnumerateAsync` contract; moving it to the step is the riskiest single change.

---

## 5. Cleanup notes (analysis only — do NOT delete)

- **`domain2` project with source == sink** (reported on Conduit's .56 DB): a SyncProject whose `SourceTenantId == SinkTenantId` is a self-loop test artifact. It would enumerate a system and write back to itself — almost certainly dead test data. **Verify** via `SELECT Id,Name,SourceTenantId,SinkTenantId FROM SyncProjects WHERE SourceTenantId = SinkTenantId` and delete after confirming it's not a deliberate in-place reshape project. (Could not query the live .56 DB during this static audit — flag for runtime verification.)
- **V19 gap in the Conduit migrator:** versions jump V18→V20 (`DatabaseMigrator.cs:912`→`:950`). Intentional or a dropped migration? Worth a one-line comment so a future reader doesn't think a migration was lost.
- **Orphan scope rows:** after V21 dropped `UQ_SyncProjectScopes_SyncProjectId`, confirm no pre-V21 project still has a stray project-level scope row alongside per-step rows that would now double-apply. Query: scopes where `WorkflowStepId IS NULL` for a project that also has step-level scopes.
- **`InternalSync*` family (IC side):** if any are now fully dead (no project references them), they're candidates for an eventual archive — but they're IC-internal and out of Conduit's scope; note only.
- **Backfilled Default workflows (V17):** every pre-Phase-7 Conduit project got a synthetic `Default`/`Mapping` workflow+step. Harmless, but when the import translator (Phase C) lands, ensure it doesn't double-create.

---

## Appendix — file:line index

| Artifact | Location |
|---|---|
| IC sync DDL (all tables) | `IdentityCenter/Software/DataAccessLibrary/Migrations/Scripts/V004__InitialSchema.sql` (SyncProjects:1383, SyncWorkflows:1994, SyncSteps:2237, AttributeMappings:2323, SyncProjectRuns:1963, SyncStepRuns:2352, PostSyncTasks:2215, SyncProjectChains:1942, InternalSyncSteps:1919) |
| IC SyncDirection add | `…/Scripts/V011__AddSyncDirectionAndManagerEmployeeId.sql` |
| IC SyncProjects/Steps defaults | `…/Scripts/V127__SyncProjectsNotNullDefaults.sql`, `V128__NotNullColumnDefaults.sql` |
| IC orchestrator | `…/DataAccessLibrary/Services/SyncProjectOrchestrator.cs` (entry:188, ProjectType routing:354, workflow walk:1039, step ladder:1101, lookup:2268, group membership:1916, license:2632) |
| IC generators | `…/WebPortal/Services/AutoSyncProjectGenerator.cs` (Core:74, Infra:80, Full:91), `AutoAttributeMappingService.cs` |
| Conduit models | `Conduit/src/Conduit.Core/SyncModels/SyncProject.cs` (SyncProject:14, SyncProjectScope:74, AttributeMapping:198, Workflow:222, WorkflowStep:242, WorkflowStepTypes:263) |
| Conduit migrator | `Conduit/src/Conduit.DataAccess/DatabaseMigrator.cs` (V14:580, V15:729, V16:759, V17:810, V20:950, V21:1012) |
| Conduit orchestrator | `Conduit/src/Conduit.Sync/Orchestration/SyncProjectOrchestrator.cs` (entry:82, RunCore:169, step switch:275, Mapping:474, PersonMatch:508, pump:732, tombstone:899) |
| Conduit generator | `Conduit/src/Conduit.Sync/Templates/SyncProjectGenerator.cs` (AdCore:66, modes:16) |
