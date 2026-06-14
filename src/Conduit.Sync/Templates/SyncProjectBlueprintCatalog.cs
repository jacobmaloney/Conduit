using System;
using System.Collections.Generic;
using System.Linq;
using Conduit.Core.Models;
using Conduit.Core.SyncModels;

namespace Conduit.Sync.Templates;

/// <summary>
/// A curated, declarative sync-project blueprint: a named "make it easy" starting
/// point that expands — via <see cref="ISyncProjectGenerator"/> — into a fully
/// configured multi-class project (one Mapping step per class, default scope/page
/// size, auto-filled attribute mappings). A blueprint selects its classes EITHER by
/// a <see cref="GenerationMode"/> OR by an explicit lowercase native class list.
/// </summary>
public sealed class SyncProjectBlueprint
{
    /// <summary>Stable identifier, safe to reference from UI / persistence.</summary>
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>The SystemType the SOURCE connection must be (e.g. "EntraID").</summary>
    public string SourceSystemType { get; init; } = string.Empty;

    /// <summary>
    /// When set, classes come from <see cref="ISyncProjectGenerator.GetObjectClasses"/>
    /// for (SourceSystemType, this mode). Mutually exclusive with
    /// <see cref="ExplicitClasses"/>.
    /// </summary>
    public GenerationMode? Mode { get; init; }

    /// <summary>
    /// When set, an explicit lowercase native class list. Mutually exclusive with
    /// <see cref="Mode"/>.
    /// </summary>
    public IReadOnlyList<string>? ExplicitClasses { get; init; }

    /// <summary>Cron string defaulted into the create flow (operator can change it).</summary>
    public string? DefaultCronSchedule { get; init; }

    /// <summary>Free-form advisory notes shown to the operator before creating.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Per-class advisories. Keyed by lowercase native class. Used by the UI to flag
    /// classes that are discovered-but-not-yet-populated, or that need a prerequisite.
    /// </summary>
    public IReadOnlyDictionary<string, string> ClassAdvisories { get; init; }
        = new Dictionary<string, string>();

    /// <summary>True when this blueprint selects classes by mode.</summary>
    public bool UsesMode => Mode.HasValue;

    /// <summary>
    /// The classes this blueprint will create against the given generator. For a
    /// mode blueprint this is <see cref="ISyncProjectGenerator.GetObjectClasses"/>;
    /// for an explicit blueprint it is the configured list.
    /// </summary>
    public IReadOnlyList<string> ResolveClasses(ISyncProjectGenerator generator)
    {
        if (Mode.HasValue)
            return generator.GetObjectClasses(SourceSystemType, Mode.Value);
        return ExplicitClasses ?? Array.Empty<string>();
    }

    /// <summary>
    /// Expands this blueprint into the SAME GeneratedSyncProject shape the
    /// auto-generate path produces, by delegating to the matching Generate overload.
    /// Persists nothing.
    /// </summary>
    public IReadOnlyList<GeneratedSyncProject> Expand(
        ISyncProjectGenerator generator,
        Tenant sourceTenant,
        Tenant sinkTenant,
        string? cronSchedule,
        IReadOnlyCollection<string> existingNames)
    {
        var cron = cronSchedule ?? DefaultCronSchedule;
        if (Mode.HasValue)
            return generator.Generate(sourceTenant, sinkTenant, Mode.Value, cron, existingNames);
        return generator.Generate(
            sourceTenant, sinkTenant, ExplicitClasses ?? Array.Empty<string>(), cron, existingNames);
    }
}

/// <summary>
/// The in-code catalog of curated sync-project blueprints. Static + one-file like
/// the sibling Templates catalogs (no DB table). Adding a blueprint is a one-file
/// edit and a rebuild.
/// </summary>
public static class SyncProjectBlueprintCatalog
{
    // Daily at 02:00. Matches the cron style used elsewhere in the sync UI.
    private const string DailyCron = "0 0 2 * * ?";

    private const string DisplayConcealedNamesPrereq =
        "Microsoft 365 usage data requires an admin to PATCH "
        + "https://graph.microsoft.com/v1.0/admin/reportSettings with "
        + "{\"displayConcealedNames\": false} — otherwise usage rows come back empty.";

    private static readonly IReadOnlyList<SyncProjectBlueprint> _all = new List<SyncProjectBlueprint>
    {
        new SyncProjectBlueprint
        {
            Id = "entra-directory-governance",
            Name = "Entra ID Directory Governance",
            Description =
                "Inventories the whole Entra ID directory — users, groups, service principals, "
                + "directory roles, applications, devices, administrative units, conditional access "
                + "policies, OAuth2 permission grants, domains, and Microsoft 365 usage — into "
                + "IdentityCenter for governance.",
            SourceSystemType = "EntraID",
            Mode = GenerationMode.Full,
            DefaultCronSchedule = DailyCron,
            Notes = new[]
            {
                "Requires Graph application scopes: Application.Read.All, RoleManagement.Read.Directory, "
                    + "Device.Read.All, AdministrativeUnit.Read.All, Policy.Read.All, Directory.Read.All, "
                    + "Domain.Read.All.",
                "oAuth2PermissionGrant requires Directory.Read.All (unavoidably broad).",
                DisplayConcealedNamesPrereq
            },
            ClassAdvisories = new Dictionary<string, string>
            {
                ["m365usage"] = DisplayConcealedNamesPrereq,
                ["oAuth2PermissionGrant"] = "Requires the broad Directory.Read.All scope."
            }
        },
        new SyncProjectBlueprint
        {
            Id = "m365-license-usage",
            Name = "Microsoft 365 License & Usage",
            Description =
                "Focused on license and usage signal: users, Microsoft 365 usage reports, and "
                + "SharePoint sites. Rides the same Entra ID connection.",
            SourceSystemType = "EntraID",
            ExplicitClasses = new[] { "user", "m365usage", "site" },
            DefaultCronSchedule = DailyCron,
            Notes = new[]
            {
                DisplayConcealedNamesPrereq,
                "Requires the Reports.Read.All and Sites.Read.All Graph scopes (plus the base "
                    + "directory scopes for users)."
            },
            ClassAdvisories = new Dictionary<string, string>
            {
                ["m365usage"] = DisplayConcealedNamesPrereq,
                ["site"] = "Requires the Sites.Read.All scope."
            }
        },
        new SyncProjectBlueprint
        {
            Id = "azure-resource-inventory",
            Name = "Azure Resource Inventory",
            Description =
                "Inventories Azure subscriptions and resources via Azure Resource Graph for "
                + "cloud-asset governance.",
            SourceSystemType = "AzureResourceGraph",
            ExplicitClasses = new[] { "azuresubscription", "azureresource" },
            DefaultCronSchedule = DailyCron,
            Notes = new[]
            {
                "The service principal needs the Azure RBAC Reader role at the subscription or "
                    + "management-group scope you want to inventory.",
                "Source-only: Azure Resource Graph never emits deletions."
            }
        }
    };

    // Classes advertised by the generator but not yet populated by a live connector.
    private static readonly HashSet<string> DeferredClasses =
        new(StringComparer.Ordinal) { "sharepointgroup" };

    /// <summary>All blueprints, in display order.</summary>
    public static IReadOnlyList<SyncProjectBlueprint> All => _all;

    /// <summary>Look up a blueprint by its stable Id, or null.</summary>
    public static SyncProjectBlueprint? GetById(string id) =>
        _all.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Blueprints whose SourceSystemType matches the given connection type.</summary>
    public static IReadOnlyList<SyncProjectBlueprint> ForSourceSystemType(string systemType) =>
        _all.Where(b => string.Equals(b.SourceSystemType, systemType, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>True if a class is known-discovered-but-not-yet-populated.</summary>
    public static bool IsDeferredClass(string objectClass) =>
        DeferredClasses.Contains(objectClass);
}
