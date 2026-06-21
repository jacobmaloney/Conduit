using System;
using System.Collections.Generic;
using System.Linq;
using Conduit.Core.Models;
using Conduit.Core.SyncModels;

namespace Conduit.Sync.Templates
{
    /// <summary>
    /// Generation mode. Maps IdentityCenter's connector template tiers onto Conduit's
    /// symmetric model. Core and Full apply to every connector; the middle tiers apply
    /// to specific connectors and fall back to Full elsewhere.
    /// </summary>
    public enum GenerationMode
    {
        Core,
        Full,
        AdInfrastructure,
        EntraSecurity,
        SharePointCollaboration
    }

    /// <summary>
    /// One generated Mapping step within a project's workflow: the step itself plus
    /// its per-step scope and attribute mappings, all for a single object class.
    /// </summary>
    public class GeneratedSyncStep
    {
        public WorkflowStep Step { get; set; } = new();
        public SyncProjectScope Scope { get; set; } = new();
        public List<AttributeMapping> Mappings { get; set; } = new();
    }

    /// <summary>
    /// One generated per-class workflow: the <see cref="Workflow"/> entity plus the
    /// single Mapping step (with its scope + mappings) it carries. IC-parity shape —
    /// each object class becomes its OWN workflow, named like IC ("user Upsert Sync"),
    /// holding exactly one Mapping step, so the Edit Sync Project → Workflows tab
    /// renders one collapsible per class.
    /// </summary>
    public class GeneratedWorkflow
    {
        public Workflow Workflow { get; set; } = new();
        public List<GeneratedSyncStep> Steps { get; set; } = new();
    }

    /// <summary>
    /// A fully-populated, in-memory sync-project graph spanning MANY object classes
    /// (V23.1 redesign): ONE project -> N workflows, ONE PER OBJECT CLASS, each
    /// workflow carrying that class's single Mapping step (ObjectClass + scope +
    /// attribute mappings). This mirrors IdentityCenter's "Configured Workflows (N)"
    /// layout. Nothing is persisted here; the caller writes it through the repositories.
    /// </summary>
    public class GeneratedSyncProject
    {
        public SyncProject Project { get; set; } = new();
        public List<GeneratedWorkflow> Workflows { get; set; } = new();
    }

    public interface ISyncProjectGenerator
    {
        /// <summary>The object classes a (source SystemType, mode) pair will generate.</summary>
        IReadOnlyList<string> GetObjectClasses(string sourceSystemType, GenerationMode mode);

        /// <summary>
        /// V23.1: builds ONE in-memory sync-project graph for the (source, sink, mode)
        /// pair — a single project with ONE WORKFLOW PER OBJECT CLASS (IC parity), each
        /// workflow named "<class> Upsert Sync" and holding a single Mapping step. Each
        /// step carries its own ObjectClass, default scope/filter, and auto-filled
        /// attribute mappings (via the Phase 2 <see cref="IAttributeMapService"/>). The
        /// project name is de-duplicated against <paramref name="existingNames"/> (#2/#3
        /// suffix like IC). Returns a list for forward-compatibility, but yields one project.
        /// </summary>
        IReadOnlyList<GeneratedSyncProject> Generate(
            Tenant sourceTenant,
            Tenant sinkTenant,
            GenerationMode mode,
            string? cronSchedule,
            IReadOnlyCollection<string> existingNames);

        /// <summary>
        /// Blueprint path: builds the SAME in-memory sync-project graph as the
        /// mode-based overload, but over an EXPLICIT lowercase native class list
        /// instead of <see cref="GetObjectClasses"/>. Used by the blueprint catalog
        /// for curated class selections (e.g. {user, m365usage, site}). Behaviour is
        /// identical per class (one workflow + its Mapping step + default scope/page size
        /// + auto-filled mappings); IsEnabled=false, SkipUnchanged=true, project
        /// ObjectClass=classes[0] as the back-compat fallback.
        /// </summary>
        IReadOnlyList<GeneratedSyncProject> Generate(
            Tenant sourceTenant,
            Tenant sinkTenant,
            IReadOnlyCollection<string> explicitClasses,
            string? cronSchedule,
            IReadOnlyCollection<string> existingNames);
    }

    public class SyncProjectGenerator : ISyncProjectGenerator
    {
        private readonly IAttributeMapService _attributeMapService;

        public SyncProjectGenerator(IAttributeMapService attributeMapService)
        {
            _attributeMapService = attributeMapService;
        }

        // ── Active Directory: Core(4) / Infrastructure(20) / Full(24) ──
        private static readonly string[] AdCore = { "user", "group", "computer", "contact" };
        private static readonly string[] AdInfrastructure =
        {
            "organizationalUnit", "container", "domainDNS",
            "groupPolicyContainer", "msDS-GroupManagedServiceAccount", "msDS-ManagedServiceAccount",
            "foreignSecurityPrincipal", "trustedDomain",
            "serviceConnectionPoint", "printQueue", "subnet", "site", "siteLink",
            "pKICertificateTemplate", "msFVE-RecoveryInformation", "certificationAuthority",
            "attributeSchema", "classSchema", "dnsNode", "dnsZone"
        };
        private static readonly string[] AdFull = AdCore.Concat(AdInfrastructure).ToArray();

        // ── Entra ID: Core(2) / Security(7) / Full(10) ──
        private static readonly string[] EntraCore = { "user", "group" };
        private static readonly string[] EntraSecurity =
            { "user", "group", "servicePrincipal", "directoryRole", "application", "device", "oAuth2PermissionGrant" };
        private static readonly string[] EntraFull =
        {
            "user", "group", "servicePrincipal", "directoryRole",
            "application", "device",
            "administrativeUnit", "conditionalAccessPolicy", "oAuth2PermissionGrant", "domain",
            "m365usage", "signinlog"
        };

        // ── SharePoint / M365: Core(2) / Collaboration(6) / Full(7) ──
        private static readonly string[] SharePointCore = { "site", "team" };
        private static readonly string[] SharePointCollaboration = { "site", "team", "channel", "channelfile", "drive", "list", "sharepointgroup" };
        private static readonly string[] SharePointFull = { "site", "team", "channel", "channelfile", "drive", "list", "subscribedSku", "sharepointgroup" };

        // ── SCIM(2), Okta(2/3), Google(2/3), AWS(2/3), Generic LDAP(2/3), Database(1/2) ──
        private static readonly string[] ScimCore = { "user", "group" };
        private static readonly string[] ScimFull = { "user", "group" };
        private static readonly string[] OktaCore = { "user", "group" };
        private static readonly string[] OktaFull = { "user", "group", "application" };
        private static readonly string[] GoogleCore = { "user", "group" };
        private static readonly string[] GoogleFull = { "user", "group", "organizationalUnit", "role", "domain" };
        private static readonly string[] AwsCore = { "user", "group" };
        private static readonly string[] AwsFull = { "user", "group", "role", "policy", "account" };
        private static readonly string[] AwsIdentityCenterCore = { "user", "group" };
        private static readonly string[] AwsIdentityCenterFull = { "user", "group", "permissionSet" };
        private static readonly string[] LdapCore = { "user", "group" };
        private static readonly string[] LdapFull = { "user", "group", "organizationalUnit" };
        private static readonly string[] DatabaseCore = { "user" };
        private static readonly string[] DatabaseFull = { "user", "group" };
        // SQL Discovery emits exactly one class: scanned SQL hosts as computers.
        private static readonly string[] SqlDiscoveryClasses = { "computer" };
        // Azure Resource Graph: subscriptions + resources (same set Core/Full).
        private static readonly string[] AzureResourceGraphClasses = { "azuresubscription", "azureresource" };

        // ── Fallback for connectors with no dedicated set (Emulator, Csv, IdentityCenter) ──
        private static readonly string[] GenericCore = { "user", "group" };
        private static readonly string[] GenericFull = { "user", "group" };

        public IReadOnlyList<string> GetObjectClasses(string sourceSystemType, GenerationMode mode)
        {
            switch (sourceSystemType)
            {
                case "ActiveDirectory":
                // Active Roles objects ARE AD objects (the fast read is raw AD LDAP),
                // so ARS shares AD's object-class set, filters and page size.
                case "ActiveRoles":
                    return mode switch
                    {
                        GenerationMode.Core => AdCore,
                        GenerationMode.AdInfrastructure => AdInfrastructure,
                        _ => AdFull
                    };
                case "EntraID":
                    return mode switch
                    {
                        GenerationMode.Core => EntraCore,
                        GenerationMode.EntraSecurity => EntraSecurity,
                        _ => EntraFull
                    };
                case "SharePoint":
                    return mode switch
                    {
                        GenerationMode.Core => SharePointCore,
                        GenerationMode.SharePointCollaboration => SharePointCollaboration,
                        _ => SharePointFull
                    };
                case "Scim":
                    return mode == GenerationMode.Core ? ScimCore : ScimFull;
                case "Okta":
                    return mode == GenerationMode.Core ? OktaCore : OktaFull;
                case "GoogleWorkspace":
                    return mode == GenerationMode.Core ? GoogleCore : GoogleFull;
                case "AWS":
                    return mode == GenerationMode.Core ? AwsCore : AwsFull;
                case "AWSIdentityCenter":
                    return mode == GenerationMode.Core ? AwsIdentityCenterCore : AwsIdentityCenterFull;
                case "GenericLdap":
                    return mode == GenerationMode.Core ? LdapCore : LdapFull;
                case "Database":
                    return mode == GenerationMode.Core ? DatabaseCore : DatabaseFull;
                case "SqlDiscovery":
                    return SqlDiscoveryClasses;
                case "AzureResourceGraph":
                    return AzureResourceGraphClasses;
                default:
                    return mode == GenerationMode.Core ? GenericCore : GenericFull;
            }
        }

        public IReadOnlyList<GeneratedSyncProject> Generate(
            Tenant sourceTenant,
            Tenant sinkTenant,
            GenerationMode mode,
            string? cronSchedule,
            IReadOnlyCollection<string> existingNames)
        {
            var objectClasses = GetObjectClasses(sourceTenant.SystemType, mode);
            return Build(sourceTenant, sinkTenant, objectClasses, ModeLabel(mode), cronSchedule, existingNames);
        }

        public IReadOnlyList<GeneratedSyncProject> Generate(
            Tenant sourceTenant,
            Tenant sinkTenant,
            IReadOnlyCollection<string> explicitClasses,
            string? cronSchedule,
            IReadOnlyCollection<string> existingNames)
        {
            // Preserve caller order, drop blanks/dupes, normalise to lowercase native names.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var classes = (explicitClasses ?? Array.Empty<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .Where(c => seen.Add(c))
                .ToArray();
            return Build(sourceTenant, sinkTenant, classes, "Full", cronSchedule, existingNames);
        }

        /// <summary>
        /// The ONE per-class build loop shared by both Generate overloads. Produces a
        /// single project + ONE WORKFLOW PER CLASS (IC parity), each workflow holding one
        /// Mapping step with its own scope, page size and auto-filled mappings. Persists nothing.
        /// </summary>
        private IReadOnlyList<GeneratedSyncProject> Build(
            Tenant sourceTenant,
            Tenant sinkTenant,
            IReadOnlyList<string> objectClasses,
            string modeLabel,
            string? cronSchedule,
            IReadOnlyCollection<string> existingNames)
        {
            var sourceType = sourceTenant.SystemType;
            var sinkType = sinkTenant.SystemType;
            var cron = string.IsNullOrWhiteSpace(cronSchedule) ? null : cronSchedule;

            var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

            if (objectClasses.Count == 0)
                return new List<GeneratedSyncProject>();

            // V23.1: ONE project, then ONE WORKFLOW PER OBJECT CLASS (IC parity).
            // Each object class becomes its own workflow named "<class> Upsert Sync"
            // (IC's convention) holding exactly one Mapping step. The orchestrator
            // iterates workflows in Ordinal order, so N per-class workflows run
            // identically to the prior one-workflow-N-steps shape.
            var projectId = Guid.NewGuid();

            var baseName = $"{sourceTenant.Name} to {sinkTenant.Name} ({modeLabel})";
            var projectName = UniqueName(baseName, taken);
            taken.Add(projectName);

            var project = new SyncProject
            {
                Id = projectId,
                Name = projectName,
                Description = $"Auto-generated {modeLabel} sync from {sourceTenant.Name} to {sinkTenant.Name} ({objectClasses.Count} object class(es))",
                SourceTenantId = sourceTenant.Id,
                SinkTenantId = sinkTenant.Id,
                // ObjectClass is NOT NULL on the project and is now only a back-compat
                // fallback; the authoritative per-class data lives on each step. Stamp
                // the first class so legacy readers and the fallback path stay sane.
                ObjectClass = objectClasses[0],
                CronSchedule = cron,
                IsEnabled = false,
                // Default ON: re-syncs of the same directory skip rows whose content
                // hash is unchanged, so repeat runs are near-instant. The first run
                // still writes everything (and populates the hashes); subsequent runs
                // only push real changes.
                SkipUnchanged = true
            };

            var workflows = new List<GeneratedWorkflow>(objectClasses.Count);
            var workflowOrdinal = 0;
            foreach (var objectClass in objectClasses)
            {
                var workflowId = Guid.NewGuid();
                var stepId = Guid.NewGuid();

                // One workflow per class, named IC-style ("user Upsert Sync"). The
                // workflow carries the class; the step is the Mapping. Workflow order
                // mirrors class order; the single step sits at ordinal 0 within it.
                var workflow = new Workflow
                {
                    Id = workflowId,
                    SyncProjectId = projectId,
                    Name = $"{objectClass} Upsert Sync",
                    Ordinal = workflowOrdinal++,
                    Enabled = true
                };

                // The Mapping step. The orchestrator skips every non-Mapping (governance)
                // step type by default. For the FREE pump we emit Mapping only.
                // NOTE: "signinlog" (and "m365usage") are pumped AS Mapping steps — they
                // ride the normal source→sink loop and the IC sink routes them to their
                // dedicated ingest endpoints, so they are NOT filtered out here.
                var step = new WorkflowStep
                {
                    Id = stepId,
                    WorkflowId = workflowId,
                    Name = $"{objectClass} mapping",
                    StepType = WorkflowStepTypes.Mapping,
                    ObjectClass = objectClass,
                    Ordinal = 0,
                    Enabled = true
                };

                var scope = new SyncProjectScope
                {
                    Id = Guid.NewGuid(),
                    SyncProjectId = projectId,
                    WorkflowStepId = stepId,
                    LdapFilter = GetDefaultFilter(sourceType, objectClass),
                    PageSize = GetDefaultPageSize(sourceType)
                };

                var mappings = _attributeMapService.BuildMappings(sourceType, sinkType, objectClass);
                foreach (var m in mappings)
                {
                    m.SyncProjectId = projectId;
                    m.WorkflowStepId = stepId;
                }

                var generatedSteps = new List<GeneratedSyncStep>
                {
                    new GeneratedSyncStep
                    {
                        Step = step,
                        Scope = scope,
                        Mappings = mappings
                    }
                };

                // LICENSED IC feature: emit the IC-parity relationship-resolution
                // (Lookup) step ONLY when the SINK is an IdentityCenter connection.
                // For any non-IC sink (the free pump) we keep the Mapping-only shape.
                // This is the structural gate; the connection-license gate (Task 3)
                // governs whether an IC sink can exist at all.
                var lookupStep = BuildResolveRelationshipsStep(sinkType, objectClass, projectId, workflowId);
                if (lookupStep is not null)
                    generatedSteps.Add(lookupStep);

                workflows.Add(new GeneratedWorkflow
                {
                    Workflow = workflow,
                    Steps = generatedSteps
                });
            }

            return new List<GeneratedSyncProject>
            {
                new GeneratedSyncProject
                {
                    Project = project,
                    Workflows = workflows
                }
            };
        }

        /// <summary>
        /// IC-parity relationship-resolution (Lookup) step, mirroring IdentityCenter's
        /// AutoSyncProjectGenerator. Emitted ONLY when the sink is an IdentityCenter
        /// connection (the licensed IC integration) AND the object class is one IC
        /// attaches a DN-resolution lookup to:
        ///   user → "Resolve Manager Relationships" (manager)
        ///   contact → "Resolve Manager Relationships" (manager)
        ///   computer / organizationalUnit → "Resolve ManagedBy Relationships" (managedBy)
        ///   group → "Resolve Group Owner Relationships" (managedBy → owner)
        /// Returns null for the free pump (non-IC sink) or any other class, preserving
        /// the Mapping-only shape. The step is a STRUCTURAL/VISUAL marker today: it
        /// persists and renders 1:1 with IC's dashed Lookup card, but the orchestrator
        /// has no Lookup arm so it is cleanly Skipped at run time (manager-resolution
        /// itself is IC-side governance, not implemented in the pump).
        /// </summary>
        private static GeneratedSyncStep? BuildResolveRelationshipsStep(
            string sinkType, string objectClass, Guid projectId, Guid workflowId)
        {
            // Connection-target gate: only IdentityCenter sinks get governance Lookup steps.
            if (!string.Equals(sinkType, "IdentityCenter", StringComparison.OrdinalIgnoreCase))
                return null;

            var (stepName, sourceAttr) = objectClass.ToLowerInvariant() switch
            {
                "user"               => ("Resolve Manager Relationships",     "ManagerSourceId"),
                "contact"            => ("Resolve Manager Relationships",     "ManagerSourceId"),
                "computer"           => ("Resolve ManagedBy Relationships",   "ManagedBySourceId"),
                "organizationalunit" => ("Resolve ManagedBy Relationships",   "ManagedBySourceId"),
                "group"              => ("Resolve Group Owner Relationships", "ManagedBySourceId"),
                _                    => (null, null)
            };

            if (stepName is null || sourceAttr is null)
                return null;

            var stepId = Guid.NewGuid();
            var lookupStep = new WorkflowStep
            {
                Id = stepId,
                WorkflowId = workflowId,
                Name = stepName,
                StepType = WorkflowStepTypes.Lookup,
                ObjectClass = objectClass,
                // After the class's Mapping step (ordinal 0), mirroring IC's order.
                Ordinal = 1,
                Enabled = true
            };

            // IC seeds a single DNLookup attribute mapping (DN → IdentityColumn).
            // We mirror it so the step's Mappings count and shape match IC. It is not
            // executed by the pump (Lookup steps are skipped); it is structural.
            var mapping = new AttributeMapping
            {
                Id = Guid.NewGuid(),
                SyncProjectId = projectId,
                WorkflowStepId = stepId,
                SourceAttribute = sourceAttr,
                SinkAttribute = objectClass.Equals("group", StringComparison.OrdinalIgnoreCase)
                    ? "OwnerObjectId"
                    : "ManagerObjectId",
                // Conduit's mapping model has no dedicated TransformationType; the DN→
                // ObjectId resolution intent (IC's "DNLookup") is carried in TransformExpr.
                TransformExpr = "DNLookup"
            };

            var scope = new SyncProjectScope
            {
                Id = Guid.NewGuid(),
                SyncProjectId = projectId,
                WorkflowStepId = stepId,
                LdapFilter = string.Empty,
                PageSize = 0
            };

            return new GeneratedSyncStep
            {
                Step = lookupStep,
                Scope = scope,
                Mappings = new List<AttributeMapping> { mapping }
            };
        }

        private static string ModeLabel(GenerationMode mode) => mode switch
        {
            GenerationMode.Core => "Core",
            GenerationMode.AdInfrastructure => "Infrastructure",
            GenerationMode.EntraSecurity => "Security",
            GenerationMode.SharePointCollaboration => "Collaboration",
            _ => "Full"
        };

        private static string UniqueName(string baseName, HashSet<string> taken)
        {
            if (!taken.Contains(baseName))
                return baseName;
            var n = 2;
            string candidate;
            do
            {
                candidate = $"{baseName} #{n}";
                n++;
            } while (taken.Contains(candidate));
            return candidate;
        }

        /// <summary>
        /// Per-connector default scope filter. AD uses LDAP objectCategory filters;
        /// directory-API connectors (Entra, SharePoint, SCIM, etc.) fetch-all with an
        /// empty filter that the operator can refine.
        /// </summary>
        private static string GetDefaultFilter(string sourceSystemType, string objectClass)
        {
            // Active Roles uses the same raw-AD LDAP filters as ActiveDirectory.
            if (sourceSystemType != "ActiveDirectory" && sourceSystemType != "ActiveRoles")
                return string.Empty;

            return objectClass.ToLowerInvariant() switch
            {
                "user" => "(&(objectClass=user)(objectCategory=person))",
                "group" => "(&(objectClass=group)(objectCategory=group))",
                "computer" => "(&(objectClass=computer)(objectCategory=computer))",
                "contact" => "(&(objectClass=contact)(objectCategory=person))",
                "organizationalunit" => "(objectClass=organizationalUnit)",
                "container" => "(objectClass=container)",
                "domaindns" => "(objectClass=domainDNS)",
                "grouppolicycontainer" => "(objectClass=groupPolicyContainer)",
                "msds-groupmanagedserviceaccount" => "(objectClass=msDS-GroupManagedServiceAccount)",
                "msds-managedserviceaccount" => "(objectClass=msDS-ManagedServiceAccount)",
                "foreignsecurityprincipal" => "(objectClass=foreignSecurityPrincipal)",
                "trusteddomain" => "(objectClass=trustedDomain)",
                "serviceconnectionpoint" => "(objectClass=serviceConnectionPoint)",
                "printqueue" => "(objectClass=printQueue)",
                "subnet" => "(objectClass=subnet)",
                "site" => "(objectClass=site)",
                "sitelink" => "(objectClass=siteLink)",
                "pkicertificatetemplate" => "(objectClass=pKICertificateTemplate)",
                "msfve-recoveryinformation" => "(objectClass=msFVE-RecoveryInformation)",
                "certificationauthority" => "(objectClass=certificationAuthority)",
                "attributeschema" => "(objectClass=attributeSchema)",
                "classschema" => "(objectClass=classSchema)",
                "dnsnode" => "(objectClass=dnsNode)",
                "dnszone" => "(objectClass=dnsZone)",
                _ => $"(objectClass={objectClass})"
            };
        }

        /// <summary>AD pages at 1000; directory-API connectors at 999; others default 500.</summary>
        private static int GetDefaultPageSize(string sourceSystemType) => sourceSystemType switch
        {
            "ActiveDirectory" => 1000,
            "ActiveRoles" => 1000,
            "EntraID" => 999,
            "SharePoint" => 999,
            "Scim" => 999,
            _ => 500
        };
    }
}
