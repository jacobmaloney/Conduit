using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Conduit.Core.Models;
using Conduit.Core.SyncModels;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Templates;

namespace Conduit.Web.Services
{
    /// <summary>
    /// REMOVABLE 1:1 comparison fixture (NOT the conference demo — see DemoSeedService).
    /// Seeds ONE sentinel-named sync project that mirrors an IdentityCenter multi-class
    /// project's structure (user / group / computer / contact, one workflow per class,
    /// the user workflow carrying the "Resolve Manager Relationships" Lookup step) so an
    /// engineer can put Conduit's Edit Sync Project modal side-by-side with IC's.
    ///
    /// SAFETY / REVERSIBILITY:
    ///  - Everything it creates is SENTINEL-tagged: the project name is prefixed with
    ///    <see cref="SentinelPrefix"/> and the two tenants use fixed reserved slugs. It
    ///    NEVER touches any project/tenant it did not create.
    ///  - <see cref="SeedAsync"/> is idempotent: if a project whose name starts with the
    ///    sentinel already exists, it does nothing.
    ///  - It builds the project through the REAL <see cref="ISyncProjectGenerator"/>, so
    ///    the fixture is a faithful 1:1 of what Auto-Generate produces — including the
    ///    IC-sink-gated Lookup step (the sink tenant is IdentityCenter-typed on purpose).
    ///
    /// REMOVAL — pick either:
    ///  (1) Code: call <see cref="RemoveAsync"/> (deletes the sentinel project; pass
    ///      removeTenants:true to also drop the two sentinel tenants).
    ///  (2) SQL one-liner (project only):
    ///        DELETE FROM SyncProjects WHERE Name LIKE '[PARITY-DEMO]%';
    ///      (Workflows / WorkflowSteps / AttributeMappings / SyncProjectScopes cascade or
    ///      are FK'd to the project; if your schema lacks ON DELETE CASCADE, delete the
    ///      child rows first by SyncProjectId — RemoveAsync handles this for you.)
    /// </summary>
    public class ParityDemoSeedService
    {
        /// <summary>Sentinel name prefix — the single handle for find + remove.</summary>
        public const string SentinelPrefix = "[PARITY-DEMO]";

        private const string SourceSlug = "parity-demo-src";
        private const string SinkSlug = "parity-demo-ic-sink";

        // The IC multi-class shape this fixture mirrors. user carries the Lookup step;
        // computer/contact also pick up IC's managedBy/manager lookups via the generator.
        private static readonly string[] FixtureClasses = { "user", "group", "computer", "contact" };

        private readonly TenantRepository _tenants;
        private readonly SyncProjectRepository _projects;
        private readonly WorkflowRepository _workflows;
        private readonly ISyncProjectGenerator _generator;

        public ParityDemoSeedService(
            TenantRepository tenants,
            SyncProjectRepository projects,
            WorkflowRepository workflows,
            ISyncProjectGenerator generator)
        {
            _tenants = tenants;
            _projects = projects;
            _workflows = workflows;
            _generator = generator;
        }

        public record ParitySeedResult(bool Created, Guid ProjectId, string ProjectName, int Workflows, int Steps, string Note);

        /// <summary>
        /// Idempotently creates the sentinel parity project. Returns Created=false (with
        /// the existing project's id) if one is already present. Non-destructive.
        /// </summary>
        public async Task<ParitySeedResult> SeedAsync()
        {
            var existing = (await _projects.GetAllAsync())
                .FirstOrDefault(p => p.Name.StartsWith(SentinelPrefix, StringComparison.Ordinal));
            if (existing is not null)
            {
                return new ParitySeedResult(false, existing.Id, existing.Name, 0, 0,
                    "A parity-demo project already exists — skipped (idempotent).");
            }

            // Sentinel source (AD-typed so the generated LDAP filters/page sizes match IC)
            // and an IdentityCenter-typed SINK so the IC-gated Lookup step is emitted.
            var source = await EnsureTenant(SourceSlug, "Parity Demo — AD Source",
                "ActiveDirectory", "parity.demo.local");
            var sink = await EnsureTenant(SinkSlug, "Parity Demo — IdentityCenter Sink",
                "IdentityCenter", "ic.parity.demo.local");

            var existingNames = (await _projects.GetAllAsync()).Select(p => p.Name).ToList();

            // Real generator → true 1:1 with Auto-Generate (incl. the IC Lookup step).
            var generated = _generator
                .Generate(source, sink, FixtureClasses, cronSchedule: null, existingNames)
                .Single();

            // Sentinel-tag the project name so it is trivially findable + removable.
            generated.Project.Name = $"{SentinelPrefix} {source.Name} → {sink.Name} (Full)";
            generated.Project.Description =
                "REMOVABLE 1:1 parity fixture (ParityDemoSeedService). Mirrors an IC multi-class project " +
                "for side-by-side modal comparison. Safe to delete: DELETE FROM SyncProjects WHERE Name LIKE '[PARITY-DEMO]%'.";

            await _projects.CreateAsync(generated.Project);

            var stepCount = 0;
            foreach (var w in generated.Workflows)
            {
                await _workflows.CreateAsync(w.Workflow);
                foreach (var st in w.Steps)
                {
                    await _workflows.CreateStepAsync(st.Step);
                    await _workflows.UpsertScopeForStepAsync(generated.Project.Id, st.Step.Id, st.Scope);
                    if (st.Mappings.Count > 0)
                        await _workflows.ReplaceMappingsForStepAsync(generated.Project.Id, st.Step.Id, st.Mappings);
                    stepCount++;
                }
            }

            return new ParitySeedResult(true, generated.Project.Id, generated.Project.Name,
                generated.Workflows.Count, stepCount,
                "Created parity-demo project. Remove with ParityDemoSeedService.RemoveAsync() " +
                "or: DELETE FROM SyncProjects WHERE Name LIKE '[PARITY-DEMO]%';");
        }

        /// <summary>
        /// Removes the sentinel parity project (and its workflows/steps via the repo).
        /// Pass removeTenants:true to also drop the two sentinel tenants. Only ever
        /// touches sentinel-tagged rows.
        /// </summary>
        public async Task<string> RemoveAsync(bool removeTenants = false)
        {
            var notes = new List<string>();
            var sentinelProjects = (await _projects.GetAllAsync())
                .Where(p => p.Name.StartsWith(SentinelPrefix, StringComparison.Ordinal))
                .ToList();

            foreach (var p in sentinelProjects)
            {
                // Delete children first so this works regardless of ON DELETE CASCADE.
                foreach (var w in await _workflows.GetByProjectAsync(p.Id))
                {
                    foreach (var s in await _workflows.GetStepsAsync(w.Id))
                        await _workflows.DeleteStepAsync(s.Id);
                    await _workflows.DeleteAsync(w.Id);
                }
                await _projects.DeleteAsync(p.Id);
                notes.Add($"Deleted project '{p.Name}' ({p.Id}).");
            }

            if (sentinelProjects.Count == 0)
                notes.Add("No parity-demo project found.");

            if (removeTenants)
            {
                foreach (var slug in new[] { SourceSlug, SinkSlug })
                {
                    var t = await _tenants.GetBySlugAsync(slug);
                    if (t is not null && await _tenants.DeleteAsync(t.Id))
                        notes.Add($"Deleted sentinel tenant '{slug}'.");
                }
            }

            return string.Join(" ", notes);
        }

        private async Task<Tenant> EnsureTenant(string slug, string name, string systemType, string domain)
        {
            var existing = await _tenants.GetBySlugAsync(slug);
            if (existing is not null)
                return existing;

            return await _tenants.CreateAsync(new Tenant
            {
                Name = name,
                Slug = slug,
                Description = "REMOVABLE parity-demo tenant (ParityDemoSeedService).",
                SystemType = systemType,
                Domain = domain,
                IsActive = true
            });
        }
    }
}
