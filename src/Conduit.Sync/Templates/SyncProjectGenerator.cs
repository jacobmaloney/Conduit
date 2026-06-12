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
    /// A fully-populated, in-memory sync-project graph spanning MANY object classes
    /// (V23 redesign): ONE project -> ONE workflow -> N Mapping steps, one per object
    /// class, each step carrying its own ObjectClass + scope + attribute mappings.
    /// Nothing is persisted here; the caller writes it through the repositories.
    /// </summary>
    public class GeneratedSyncProject
    {
        public SyncProject Project { get; set; } = new();
        public Workflow Workflow { get; set; } = new();
        public List<GeneratedSyncStep> Steps { get; set; } = new();
    }

    public interface ISyncProjectGenerator
    {
        /// <summary>The object classes a (source SystemType, mode) pair will generate.</summary>
        IReadOnlyList<string> GetObjectClasses(string sourceSystemType, GenerationMode mode);

        /// <summary>
        /// V23: builds ONE in-memory sync-project graph for the (source, sink, mode)
        /// pair — a single project whose one workflow holds N Mapping steps, one per
        /// object class in the source connector's set. Each step carries its own
        /// ObjectClass, default scope/filter, and auto-filled attribute mappings (via
        /// the Phase 2 <see cref="IAttributeMapService"/>). The project name is
        /// de-duplicated against <paramref name="existingNames"/> (#2/#3 suffix like IC).
        /// Returns a list for forward-compatibility, but today yields a single project.
        /// </summary>
        IReadOnlyList<GeneratedSyncProject> Generate(
            Tenant sourceTenant,
            Tenant sinkTenant,
            GenerationMode mode,
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
            "administrativeUnit", "conditionalAccessPolicy", "oAuth2PermissionGrant", "domain"
        };

        // ── SharePoint / M365: Core(2) / Collaboration(5) / Full(6) ──
        private static readonly string[] SharePointCore = { "site", "team" };
        private static readonly string[] SharePointCollaboration = { "site", "team", "drive", "channel", "list" };
        private static readonly string[] SharePointFull = { "site", "team", "drive", "channel", "list", "subscribedSku" };

        // ── SCIM(2), Okta(2/3), Google(2/3), AWS(2/3), Generic LDAP(2/3), Database(1/2) ──
        private static readonly string[] ScimCore = { "user", "group" };
        private static readonly string[] ScimFull = { "user", "group" };
        private static readonly string[] OktaCore = { "user", "group" };
        private static readonly string[] OktaFull = { "user", "group", "application" };
        private static readonly string[] GoogleCore = { "user", "group" };
        private static readonly string[] GoogleFull = { "user", "group", "organizationalUnit" };
        private static readonly string[] AwsCore = { "user", "group" };
        private static readonly string[] AwsFull = { "user", "group", "role" };
        private static readonly string[] LdapCore = { "user", "group" };
        private static readonly string[] LdapFull = { "user", "group", "organizationalUnit" };
        private static readonly string[] DatabaseCore = { "user" };
        private static readonly string[] DatabaseFull = { "user", "group" };

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
                case "Aws":
                    return mode == GenerationMode.Core ? AwsCore : AwsFull;
                case "GenericLdap":
                    return mode == GenerationMode.Core ? LdapCore : LdapFull;
                case "Database":
                    return mode == GenerationMode.Core ? DatabaseCore : DatabaseFull;
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
            var sourceType = sourceTenant.SystemType;
            var sinkType = sinkTenant.SystemType;
            var objectClasses = GetObjectClasses(sourceType, mode);
            var cron = string.IsNullOrWhiteSpace(cronSchedule) ? null : cronSchedule;

            var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
            var modeLabel = ModeLabel(mode);

            if (objectClasses.Count == 0)
                return new List<GeneratedSyncProject>();

            // V23: ONE project + ONE workflow for the whole (source, sink, mode) pair.
            // Each object class becomes a Mapping STEP under that single workflow.
            var projectId = Guid.NewGuid();
            var workflowId = Guid.NewGuid();

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

            var workflow = new Workflow
            {
                Id = workflowId,
                SyncProjectId = projectId,
                Name = $"{modeLabel} sync",
                Ordinal = 0,
                Enabled = true
            };

            var steps = new List<GeneratedSyncStep>(objectClasses.Count);
            var ordinal = 0;
            foreach (var objectClass in objectClasses)
            {
                var stepId = Guid.NewGuid();

                // Only Mapping steps. The orchestrator skips every non-Mapping (governance)
                // step type, so we never emit Lookup / GroupMembership / License / SignInLog /
                // UsageReport / AppRole steps — those are IC governance, not the free pump.
                var step = new WorkflowStep
                {
                    Id = stepId,
                    WorkflowId = workflowId,
                    Name = $"{objectClass} mapping",
                    StepType = WorkflowStepTypes.Mapping,
                    ObjectClass = objectClass,
                    Ordinal = ordinal++,
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

                steps.Add(new GeneratedSyncStep
                {
                    Step = step,
                    Scope = scope,
                    Mappings = mappings
                });
            }

            return new List<GeneratedSyncProject>
            {
                new GeneratedSyncProject
                {
                    Project = project,
                    Workflow = workflow,
                    Steps = steps
                }
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
