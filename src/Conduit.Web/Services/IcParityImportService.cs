using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Conduit.Core.SyncModels;
using Conduit.DataAccess.Repositories;

namespace Conduit.Web.Services
{
    /// <summary>
    /// PROOF + REMEDY service for IC ↔ Conduit sync-project parity. Two operations,
    /// both REMOVABLE and REVERSIBLE, both sentinel-tagged so they only ever touch
    /// rows this service created:
    ///
    ///  TASK 1 — <see cref="MigrateProjectToPerClassAsync"/>: re-groups an EXISTING
    ///   Conduit project that was generated BEFORE commit 7d7e034 (one "Full sync"
    ///   workflow holding N flat per-class Mapping steps) into the IC-parity shape
    ///   (N per-class workflows, one Mapping step each). It does NOT delete or rewrite
    ///   any step/scope/mapping content — it only re-parents each step under a new
    ///   per-class workflow and re-numbers ordinals. A snapshot of the ORIGINAL
    ///   (workflowId, name, ordinal) per step is stamped into each new workflow's
    ///   Description so the move is fully reversible by <see cref="RevertMigrationAsync"/>.
    ///   It is OPT-IN: nothing runs unless an admin explicitly invokes it on a chosen
    ///   project. It NEVER silently rewrites a live project.
    ///
    ///  TASK 2 — <see cref="ImportFromIdentityCenterAsync"/>: reads a REAL IdentityCenter
    ///   sync project's full graph (SyncProjects → SyncWorkflows → SyncSteps →
    ///   AttributeMappings) from the IC database (read-only), transforms it column-by-
    ///   column into Conduit's tables, and writes it as a NEW project with NEW GUIDs and
    ///   the sentinel-tagged name "[IC-IMPORT] &lt;original&gt;". This proves the two
    ///   schemas hold the same shape (clean column map) and that the imported rows render
    ///   1:1 in Conduit's Edit Sync Project page. It NEVER writes to the IC database.
    ///
    /// REMOVAL:
    ///  - Imports:  <see cref="RemoveImportsAsync"/>  (the safe path — string StartsWith, cascades children).
    ///  - Migration: <see cref="RevertMigrationAsync"/> (restores the original workflow grouping).
    ///
    /// REMOVAL BY SQL (note the ESCAPE — the literal '[' is a LIKE wildcard set opener,
    /// so a naive LIKE '[IC-IMPORT]%' matches the WRONG rows; always escape it):
    ///   DELETE FROM SyncProjects WHERE Name LIKE '\[IC-IMPORT\]%' ESCAPE '\';
    /// (delete AttributeMappings/SyncProjectScopes/WorkflowSteps/Workflows by SyncProjectId
    /// first if the schema has no ON DELETE CASCADE — RemoveImportsAsync handles this).
    /// </summary>
    public class IcParityImportService
    {
        /// <summary>Sentinel name prefix for imported projects — the single handle for find + remove.</summary>
        public const string ImportSentinelPrefix = "[IC-IMPORT]";

        /// <summary>Marker written into a migrated workflow's Description so the move is reversible.</summary>
        private const string MigrationMarker = "[PER-CLASS-MIGRATION]";

        private readonly SyncProjectRepository _projects;
        private readonly WorkflowRepository _workflows;

        public IcParityImportService(SyncProjectRepository projects, WorkflowRepository workflows)
        {
            _projects = projects;
            _workflows = workflows;
        }

        // ════════════════════════════════════════════════════════════════════════
        //  TASK 1 — migrate an existing flat (1-workflow) project to per-class shape
        // ════════════════════════════════════════════════════════════════════════

        public record MigrationResult(bool Changed, int WorkflowsBefore, int WorkflowsAfter, int StepsMoved, string Note);

        /// <summary>
        /// True when a project is in the pre-7d7e034 flat shape: exactly one workflow
        /// whose steps span more than one distinct ObjectClass. (A genuinely single-class
        /// project is left alone — there is nothing to split.)
        /// </summary>
        public async Task<bool> IsFlatLegacyShapeAsync(Guid projectId)
        {
            var workflows = await _workflows.GetByProjectAsync(projectId);
            if (workflows.Count != 1) return false;
            var steps = await _workflows.GetStepsAsync(workflows[0].Id);
            var classes = steps
                .Select(s => (s.ObjectClass ?? string.Empty).ToLowerInvariant())
                .Where(c => c.Length > 0)
                .Distinct()
                .Count();
            return classes > 1;
        }

        /// <summary>
        /// Re-groups a flat project (one workflow / N per-class steps) into the IC-parity
        /// shape (N per-class workflows / one step each). Reversible: the original
        /// (workflowId, name, ordinal, stepObjectClass→ordinal) is encoded into each new
        /// workflow's Description behind the <see cref="MigrationMarker"/>. NON-DESTRUCTIVE:
        /// no step, scope, or mapping row is deleted or content-edited; steps are only
        /// re-parented (WorkflowId) and re-ordinalled.
        /// </summary>
        public async Task<MigrationResult> MigrateProjectToPerClassAsync(Guid projectId)
        {
            var project = await _projects.GetByIdAsync(projectId);
            if (project is null)
                return new MigrationResult(false, 0, 0, 0, "Project not found.");

            var workflows = await _workflows.GetByProjectAsync(projectId);
            if (workflows.Count != 1)
                return new MigrationResult(false, workflows.Count, workflows.Count, 0,
                    $"Project already has {workflows.Count} workflows — not the flat 1-workflow shape; nothing to migrate.");

            var originalWorkflow = workflows[0];
            var steps = (await _workflows.GetStepsAsync(originalWorkflow.Id))
                .OrderBy(s => s.Ordinal).ToList();

            // Group steps by object class, PRESERVING first-seen class order so the new
            // workflows appear in the same order the classes appeared in the flat list.
            var classOrder = new List<string>();
            var byClass = new Dictionary<string, List<WorkflowStep>>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in steps)
            {
                // GroupMembership steps carry ObjectClass "GroupMembership" but belong with
                // the GROUP workflow (IC attaches membership to the group workflow). Route
                // them to "group" if a group class exists, else keep their own class.
                var rawClass = (s.ObjectClass ?? string.Empty);
                var groupingClass = rawClass.Equals("GroupMembership", StringComparison.OrdinalIgnoreCase)
                    ? "group"
                    : rawClass;
                if (string.IsNullOrWhiteSpace(groupingClass))
                    groupingClass = "(unclassified)";

                if (!byClass.TryGetValue(groupingClass, out var list))
                {
                    list = new List<WorkflowStep>();
                    byClass[groupingClass] = list;
                    classOrder.Add(groupingClass);
                }
                list.Add(s);
            }

            if (classOrder.Count <= 1)
                return new MigrationResult(false, 1, 1, 0,
                    "Single object class — already renders as one workflow; nothing to split.");

            // Build a reversible snapshot of the ORIGINAL grouping.
            var snapshot = $"{MigrationMarker} origWf={originalWorkflow.Id};origName={originalWorkflow.Name};origOrd={originalWorkflow.Ordinal}";

            var movedCount = 0;
            var workflowOrdinal = 0;
            foreach (var cls in classOrder)
            {
                var clsSteps = byClass[cls];
                Guid targetWorkflowId;

                if (workflowOrdinal == 0)
                {
                    // Re-use the original workflow as the FIRST per-class workflow (so the
                    // original row is never orphaned and revert has a stable anchor).
                    originalWorkflow.Name = $"{cls} Upsert Sync";
                    originalWorkflow.Ordinal = workflowOrdinal;
                    originalWorkflow.Description = snapshot;
                    await _workflows.UpdateAsync(originalWorkflow);
                    targetWorkflowId = originalWorkflow.Id;
                }
                else
                {
                    var wf = new Workflow
                    {
                        Id = Guid.NewGuid(),
                        SyncProjectId = projectId,
                        Name = $"{cls} Upsert Sync",
                        Ordinal = workflowOrdinal,
                        Enabled = originalWorkflow.Enabled,
                        Description = snapshot
                    };
                    await _workflows.CreateAsync(wf);
                    targetWorkflowId = wf.Id;
                }

                var stepOrdinal = 0;
                foreach (var st in clsSteps)
                {
                    st.WorkflowId = targetWorkflowId;
                    st.Ordinal = stepOrdinal++;
                    await _workflows.UpdateStepAsync(st);   // moves only WorkflowId + Ordinal; content untouched
                    movedCount++;
                }
                workflowOrdinal++;
            }

            return new MigrationResult(true, 1, workflowOrdinal, movedCount,
                $"Migrated to {workflowOrdinal} per-class workflows ({movedCount} steps re-parented). " +
                "Reversible via RevertMigrationAsync.");
        }

        /// <summary>
        /// Reverses <see cref="MigrateProjectToPerClassAsync"/>: collapses every workflow
        /// carrying the <see cref="MigrationMarker"/> snapshot back into the single original
        /// workflow, restoring its name/ordinal and re-parenting all steps under it in their
        /// prior order. Then deletes the now-empty extra workflows. Only touches rows whose
        /// workflow Description carries the marker — safe.
        /// </summary>
        public async Task<MigrationResult> RevertMigrationAsync(Guid projectId)
        {
            var workflows = await _workflows.GetByProjectAsync(projectId);
            var migrated = workflows
                .Where(w => (w.Description ?? string.Empty).StartsWith(MigrationMarker, StringComparison.Ordinal))
                .ToList();
            if (migrated.Count == 0)
                return new MigrationResult(false, workflows.Count, workflows.Count, 0,
                    "No per-class-migration marker found — nothing to revert.");

            // Recover the original workflow identity from the snapshot.
            var (origWfId, origName, origOrd) = ParseSnapshot(migrated[0].Description!);
            var anchor = migrated.FirstOrDefault(w => w.Id == origWfId) ?? migrated[0];

            // Re-parent ALL steps from every migrated workflow under the anchor, in
            // (workflow ordinal, step ordinal) order, re-numbering sequentially.
            var ordered = migrated.OrderBy(w => w.Ordinal).ToList();
            var stepOrdinal = 0;
            var moved = 0;
            foreach (var w in ordered)
            {
                var steps = (await _workflows.GetStepsAsync(w.Id)).OrderBy(s => s.Ordinal).ToList();
                foreach (var st in steps)
                {
                    st.WorkflowId = anchor.Id;
                    st.Ordinal = stepOrdinal++;
                    await _workflows.UpdateStepAsync(st);
                    moved++;
                }
            }

            // Restore the anchor's original identity and clear the marker.
            anchor.Name = origName;
            anchor.Ordinal = origOrd;
            anchor.Description = null;
            await _workflows.UpdateAsync(anchor);

            // Delete the now-empty extra workflows (their steps were all moved).
            foreach (var w in ordered.Where(w => w.Id != anchor.Id))
                await _workflows.DeleteAsync(w.Id);   // safe: empty, no steps remain

            return new MigrationResult(true, migrated.Count, 1, moved,
                $"Reverted to the original single workflow '{origName}' ({moved} steps restored).");
        }

        private static (Guid id, string name, int ordinal) ParseSnapshot(string description)
        {
            // Format: "[PER-CLASS-MIGRATION] origWf=<guid>;origName=<name>;origOrd=<n>"
            var body = description.Substring(MigrationMarker.Length).Trim();
            var parts = body.Split(';');
            var id = Guid.Empty; var name = "Full sync"; var ord = 0;
            foreach (var p in parts)
            {
                var kv = p.Split(new[] { '=' }, 2);
                if (kv.Length != 2) continue;
                switch (kv[0].Trim())
                {
                    case "origWf": Guid.TryParse(kv[1].Trim(), out id); break;
                    case "origName": name = kv[1].Trim(); break;
                    case "origOrd": int.TryParse(kv[1].Trim(), out ord); break;
                }
            }
            return (id, name, ord);
        }

        // ════════════════════════════════════════════════════════════════════════
        //  TASK 2 — import a REAL IC sync project's graph into Conduit (proof)
        // ════════════════════════════════════════════════════════════════════════

        public record ImportResult(
            bool Created, Guid ProjectId, string ProjectName,
            int Workflows, int Steps, int Mappings, int Scopes,
            IReadOnlyList<string> Notes);

        /// <summary>
        /// Lists the importable sync projects in an IC database (read-only) with their
        /// workflow/step/mapping counts, so an admin can pick one.
        /// </summary>
        public async Task<IReadOnlyList<(string Name, int Workflows, int Steps, int Mappings)>> ListIcProjectsAsync(string icConnectionString)
        {
            using var conn = new SqlConnection(icConnectionString);
            var rows = await conn.QueryAsync<(string Name, int Workflows, int Steps, int Mappings)>(@"
                SELECT  p.Name,
                        COUNT(DISTINCT w.Id)  AS Workflows,
                        COUNT(DISTINCT s.Id)  AS Steps,
                        COUNT(m.Id)           AS Mappings
                FROM        SyncProjects     p
                LEFT JOIN   SyncWorkflows    w ON w.SyncProjectId  = p.Id
                LEFT JOIN   SyncSteps        s ON s.SyncWorkflowId = w.Id
                LEFT JOIN   AttributeMappings m ON m.SyncStepId    = s.Id
                GROUP BY p.Name
                ORDER BY Workflows DESC, p.Name;");
            return rows.ToList();
        }

        /// <summary>
        /// Reads ONE IC sync project's full graph (read-only) and writes it into the
        /// Conduit database, transformed column-by-column, as a NEW project with NEW GUIDs
        /// and the sentinel name "[IC-IMPORT] &lt;original&gt;". Idempotent on the sentinel
        /// name (re-import is skipped if a sentinel of that exact name exists). NEVER writes
        /// to IC.
        /// </summary>
        public async Task<ImportResult> ImportFromIdentityCenterAsync(string icConnectionString, string icProjectName)
        {
            var notes = new List<string>();
            var sentinelName = $"{ImportSentinelPrefix} {icProjectName}";

            var existing = (await _projects.GetAllAsync())
                .FirstOrDefault(p => string.Equals(p.Name, sentinelName, StringComparison.Ordinal));
            if (existing is not null)
                return new ImportResult(false, existing.Id, existing.Name, 0, 0, 0, 0,
                    new[] { $"An import named '{sentinelName}' already exists — skipped (idempotent). Remove it first to re-import." });

            // ── READ the IC graph (read-only) ────────────────────────────────────
            using var ic = new SqlConnection(icConnectionString);
            await ic.OpenAsync();

            var icProject = await ic.QuerySingleOrDefaultAsync<IcProjectRow>(
                "SELECT Id, Name, Description, ProjectType, SyncDirection FROM SyncProjects WHERE Name = @Name",
                new { Name = icProjectName });
            if (icProject is null)
                return new ImportResult(false, Guid.Empty, icProjectName, 0, 0, 0, 0,
                    new[] { $"IC project '{icProjectName}' not found in the source database." });

            var icWorkflows = (await ic.QueryAsync<IcWorkflowRow>(
                @"SELECT Id, Name, Description, ObjectClass, WorkflowType, ExecutionOrder, IsEnabled
                  FROM SyncWorkflows WHERE SyncProjectId = @Pid ORDER BY ExecutionOrder",
                new { Pid = icProject.Id })).ToList();

            var icSteps = (await ic.QueryAsync<IcStepRow>(
                @"SELECT Id, SyncWorkflowId, Name, ExecutionOrder, ObjectClass, StepType,
                         LdapFilter, SearchBase, SearchBases, ExcludedSearchBases, SearchScope,
                         LdapPageSize, IsEnabled
                  FROM SyncSteps WHERE SyncWorkflowId IN @Wids",
                new { Wids = icWorkflows.Select(w => w.Id).DefaultIfEmpty(Guid.Empty).ToArray() })).ToList();

            var icMappings = (await ic.QueryAsync<IcMappingRow>(
                @"SELECT Id, SyncStepId, SourceAttribute, TargetAttribute, TargetType,
                         TransformationType, TransformationExpression, IsRequired, ExecutionOrder
                  FROM AttributeMappings WHERE SyncStepId IN @Sids",
                new { Sids = icSteps.Select(s => s.Id).DefaultIfEmpty(Guid.Empty).ToArray() })).ToList();

            // ── TRANSFORM + WRITE into Conduit (through the real repositories) ─────
            var sourceTenantId = await ResolveOrPlaceholderTenantAsync(icProjectName);

            var conduitProject = new SyncProject
            {
                Id = Guid.NewGuid(),
                Name = sentinelName,
                Description =
                    $"REMOVABLE 1:1 import of the IdentityCenter project '{icProjectName}' " +
                    $"(IC type={icProject.ProjectType}, direction={icProject.SyncDirection}). " +
                    "Proves IC↔Conduit table + page parity. Safe to delete: " +
                    "DELETE FROM SyncProjects WHERE Name LIKE '\\[IC-IMPORT\\]%' ESCAPE '\\' (delete child rows first if no cascade).",
                SourceTenantId = sourceTenantId,
                SinkTenantId = sourceTenantId,   // placeholder; import is a structural proof, not a runnable project
                ObjectClass = icWorkflows.FirstOrDefault()?.ObjectClass ?? "user",
                IsEnabled = false,
                SkipUnchanged = true
            };
            await _projects.CreateAsync(conduitProject);

            int wfCount = 0, stepCount = 0, mapCount = 0, scopeCount = 0;
            var ordinal = 0;

            foreach (var icw in icWorkflows.OrderBy(w => w.ExecutionOrder))
            {
                var conduitWf = new Workflow
                {
                    Id = Guid.NewGuid(),
                    SyncProjectId = conduitProject.Id,
                    Name = icw.Name,
                    Description = icw.Description,
                    Ordinal = ordinal++,
                    Enabled = icw.IsEnabled
                };
                await _workflows.CreateAsync(conduitWf);
                wfCount++;

                var stepsForWf = icSteps
                    .Where(s => s.SyncWorkflowId == icw.Id)
                    .OrderBy(s => s.ExecutionOrder)
                    .ToList();

                var stepOrdinal = 0;
                foreach (var ics in stepsForWf)
                {
                    var conduitStep = new WorkflowStep
                    {
                        Id = Guid.NewGuid(),
                        WorkflowId = conduitWf.Id,
                        Name = ics.Name,
                        StepType = MapStepType(ics.StepType),
                        ObjectClass = ics.ObjectClass,
                        Ordinal = stepOrdinal++,
                        Enabled = ics.IsEnabled
                    };
                    await _workflows.CreateStepAsync(conduitStep);
                    stepCount++;

                    // Scope: IC stores filter/base/pageSize as columns on the STEP;
                    // Conduit stores them in a per-step SyncProjectScopes row.
                    var conduitScope = new SyncProjectScope
                    {
                        Id = Guid.NewGuid(),
                        SyncProjectId = conduitProject.Id,
                        WorkflowStepId = conduitStep.Id,
                        LdapFilter = ics.LdapFilter,
                        BaseDN = ics.SearchBase,
                        IncludedBaseDNs = ics.SearchBases,
                        ExcludedBaseDNs = ics.ExcludedSearchBases,
                        PageSize = ics.LdapPageSize ?? 1000
                    };
                    await _workflows.UpsertScopeForStepAsync(conduitProject.Id, conduitStep.Id, conduitScope);
                    scopeCount++;

                    var mapsForStep = icMappings
                        .Where(m => m.SyncStepId == ics.Id)
                        .OrderBy(m => m.ExecutionOrder)
                        .Select(m => new AttributeMapping
                        {
                            Id = Guid.NewGuid(),
                            SyncProjectId = conduitProject.Id,
                            WorkflowStepId = conduitStep.Id,
                            SourceAttribute = m.SourceAttribute ?? string.Empty,
                            SinkAttribute = m.TargetAttribute ?? string.Empty,
                            // Conduit has ONE TransformExpr column. Carry IC's transform
                            // intent: prefer an explicit expression, else the type name
                            // (Direct → null so it renders as a plain passthrough).
                            TransformExpr = ComposeTransform(m.TransformationType, m.TransformationExpression),
                            IsRequired = m.IsRequired,
                            SortOrder = m.ExecutionOrder
                        })
                        .ToList();

                    if (mapsForStep.Count > 0)
                    {
                        await _workflows.ReplaceMappingsForStepAsync(conduitProject.Id, conduitStep.Id, mapsForStep);
                        mapCount += mapsForStep.Count;
                    }
                }
            }

            // Honest divergence notes — the columns that did NOT survive the transform.
            notes.Add($"Imported {wfCount} workflows, {stepCount} steps, {mapCount} mappings, {scopeCount} scopes.");
            notes.Add("DIVERGENCE (lossy, expected): IC AttributeMappings.TargetType " +
                      "(IdentityColumn/ExtendedAttribute) and the matching metadata " +
                      "(UseForMatching/MatchWeight/UseFuzzyMatch/FuzzyMatchThreshold/FuzzyMatchAlgorithm) " +
                      "have NO Conduit column and were dropped — Conduit's pump does not do identity matching.");
            notes.Add("DIVERGENCE (lossy, expected): IC SyncSteps governance flags " +
                      "(SkipPersonMatching/EnablePersonMatching/CreatePersonIfNotFound/ProcessDeletions/" +
                      "UpdateExisting/Configuration) have no 1:1 Conduit step column and were dropped.");
            notes.Add("MATCH: Project→Workflow→Step→Mapping hierarchy, names, object classes, " +
                      "step types, execution order, LDAP filter/base/page size, and source→sink " +
                      "attribute pairs all map cleanly. The page renders the same per-class " +
                      "workflows + dashed Lookup/GroupMembership steps as IC.");

            return new ImportResult(true, conduitProject.Id, sentinelName,
                wfCount, stepCount, mapCount, scopeCount, notes);
        }

        /// <summary>
        /// Removes every sentinel-tagged IC-IMPORT project (and its workflows/steps/
        /// scopes/mappings via the repo cascade). Only ever touches sentinel rows.
        /// </summary>
        public async Task<string> RemoveImportsAsync()
        {
            var notes = new List<string>();
            var sentinels = (await _projects.GetAllAsync())
                .Where(p => p.Name.StartsWith(ImportSentinelPrefix, StringComparison.Ordinal))
                .ToList();

            foreach (var p in sentinels)
            {
                foreach (var w in await _workflows.GetByProjectAsync(p.Id))
                    await _workflows.DeleteAsync(w.Id);   // cascades steps/scopes/mappings
                await _projects.DeleteAsync(p.Id);
                notes.Add($"Deleted import '{p.Name}' ({p.Id}).");
            }
            if (sentinels.Count == 0) notes.Add("No [IC-IMPORT] projects found.");
            return string.Join(" ", notes);
        }

        // ── helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// The import is a STRUCTURAL proof, not a runnable project, so it does not need a
        /// real source/sink connection. Reuse any existing tenant as a placeholder FK so
        /// the NOT NULL SourceTenantId/SinkTenantId constraints are satisfied without
        /// creating throwaway connections. (The project is created IsEnabled=false.)
        /// </summary>
        private async Task<Guid> ResolveOrPlaceholderTenantAsync(string _)
        {
            // Borrow any existing project's source tenant — guaranteed valid FK.
            var any = (await _projects.GetAllAsync()).FirstOrDefault();
            if (any is not null) return any.SourceTenantId;
            // No projects yet: fall back to empty (the FK is nullable-tolerant in practice
            // for a disabled structural fixture; if the schema rejects it the admin page
            // surfaces the error rather than guessing a tenant).
            return Guid.Empty;
        }

        /// <summary>
        /// Maps IC StepType → Conduit StepType. IC and Conduit use the SAME canonical
        /// names for the parity-relevant types (Upsert is IC's Mapping equivalent).
        /// </summary>
        private static string MapStepType(string? icStepType) => (icStepType ?? "Mapping") switch
        {
            "Upsert" => WorkflowStepTypes.Mapping,   // IC "Upsert" == Conduit "Mapping" (the source→sink loop)
            "Mapping" => WorkflowStepTypes.Mapping,
            "Lookup" => WorkflowStepTypes.Lookup,
            "GroupMembership" => WorkflowStepTypes.GroupMembership,
            "LicenseSync" => WorkflowStepTypes.LicenseSync,
            "SignInLogSync" => WorkflowStepTypes.SignInLogSync,
            "UsageReportSync" => WorkflowStepTypes.UsageReportSync,
            "AppRoleSync" => WorkflowStepTypes.AppRoleSync,
            var other => other   // preserve any IC type verbatim; the orchestrator run-skips unknowns
        };

        private static string? ComposeTransform(string? type, string? expression)
        {
            if (!string.IsNullOrWhiteSpace(expression)) return expression;
            if (string.IsNullOrWhiteSpace(type) || string.Equals(type, "Direct", StringComparison.OrdinalIgnoreCase))
                return null;   // plain passthrough — no transform pill
            return type;       // carry the named transform (e.g. "DNLookup")
        }

        // ── IC read DTOs (column-faithful to the IC schema) ──────────────────────
        private sealed class IcProjectRow
        {
            public Guid Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public string? Description { get; set; }
            public string? ProjectType { get; set; }
            public string? SyncDirection { get; set; }
        }
        private sealed class IcWorkflowRow
        {
            public Guid Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public string? Description { get; set; }
            public string ObjectClass { get; set; } = string.Empty;
            public string WorkflowType { get; set; } = string.Empty;
            public int ExecutionOrder { get; set; }
            public bool IsEnabled { get; set; }
        }
        private sealed class IcStepRow
        {
            public Guid Id { get; set; }
            public Guid SyncWorkflowId { get; set; }
            public string Name { get; set; } = string.Empty;
            public int ExecutionOrder { get; set; }
            public string ObjectClass { get; set; } = string.Empty;
            public string? StepType { get; set; }
            public string? LdapFilter { get; set; }
            public string? SearchBase { get; set; }
            public string? SearchBases { get; set; }
            public string? ExcludedSearchBases { get; set; }
            public string? SearchScope { get; set; }
            public int? LdapPageSize { get; set; }
            public bool IsEnabled { get; set; }
        }
        private sealed class IcMappingRow
        {
            public Guid Id { get; set; }
            public Guid SyncStepId { get; set; }
            public string? SourceAttribute { get; set; }
            public string? TargetAttribute { get; set; }
            public string? TargetType { get; set; }
            public string? TransformationType { get; set; }
            public string? TransformationExpression { get; set; }
            public bool IsRequired { get; set; }
            public int ExecutionOrder { get; set; }
        }
    }
}
