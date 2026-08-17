using System;
using System.Collections.Generic;
using System.Linq;
using Conduit.Core.SyncModels;
using Conduit.Sync.Templates;

namespace Conduit.Sync.Connectors;

/// <summary>
/// Serializable, marketing-truthful projection of ONE connector's capabilities.
/// A PUBLIC marketing site consumes this (via the /api/connectors/capabilities
/// endpoint AND the static wwwroot/connector-capabilities.json emit), so every
/// field must be honest about what the sinks ACTUALLY do — not merely what the
/// raw capability flags declare.
///
/// The controller and the static emitter both project through the SAME
/// <see cref="From(IConnectorAdapter)"/> mapper so the live endpoint and the
/// shipped JSON can never drift.
/// </summary>
public sealed class ConnectorCapabilityDescriptor
{
    public string SystemType { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>GA / Preview / Pilot / Planned. Verified (Troi-assigned) — see the table below.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// TRUE only when the SystemType is offered in the customer onboarding dropdown
    /// (ConnectedSystems.razor). Lets the marketing site filter honestly. Emulator +
    /// LocalDirectory are internal fixtures ⇒ false.
    /// </summary>
    public bool CustomerFacing { get; init; }

    /// <summary>The connector can act as a sync SOURCE (read).</summary>
    public bool SupportsSource { get; init; }

    /// <summary>
    /// The connector can act as a sync SINK — i.e. a sync run writes to it.
    /// This is <see cref="IConnectorAdapter.SupportsSink"/> verbatim; it is a CLEAN
    /// flag and is deliberately NOT overloaded with any provisioning meaning.
    /// </summary>
    public bool WritesDuringSync { get; init; }

    /// <summary>
    /// Phase-5 inbound-proxy provisioning primitives. DISTINCT from
    /// <see cref="WritesDuringSync"/>. Computed from the TRUTHFULNESS RULE below
    /// (declared flag AND the sink genuinely overrides the method), NOT from the raw
    /// Supports* flags alone.
    /// </summary>
    public ProvisioningPrimitiveDescriptor ProvisioningPrimitives { get; init; } = new();

    /// <summary>
    /// The sink deletes records during a SYNC run (it genuinely overrides
    /// <see cref="IConnectorSink.DeleteAsync"/>). A SEPARATE axis from the Delete
    /// provisioning primitive — a connector can delete-during-sync while having no
    /// inbound provisioning Delete (e.g. SCIM, GenericLdap). There is NO capability
    /// flag for this; it is sourced from the verified sink-override table, never from
    /// <c>SupportsDelete</c>.
    /// </summary>
    public bool DeleteDuringSync { get; init; }

    public bool MembershipSupport { get; init; }
    public bool LicenseIngest { get; init; }
    public bool UsageIngest { get; init; }
    public bool SignInIngest { get; init; }
    public bool AppRoleIngest { get; init; }
    public bool IncrementalSupport { get; init; }

    public IReadOnlyList<string> SupportedObjectClasses { get; init; } = Array.Empty<string>();
    public IReadOnlyList<RequiredCredentialDescriptor> RequiredCredentials { get; init; } = Array.Empty<RequiredCredentialDescriptor>();
    public IReadOnlyList<TenantFieldRequirementDescriptor> TenantFieldRequirements { get; init; } = Array.Empty<TenantFieldRequirementDescriptor>();

    public IReadOnlyList<string> KnownLimitations { get; init; } = Array.Empty<string>();

    /// <summary>"Tested" / "Untested" — evidence-based, derived from Status.</summary>
    public string TestStatus { get; init; } = string.Empty;

    /// <summary>Stable relative docs path. Placeholder convention until a docs tree exists.</summary>
    public string DocsPath { get; init; } = string.Empty;

    // ─────────────────────────────────────────────────────────────────────────
    // THE SHARED MAPPER. Controller + static emitter both call this so they can
    // never diverge.
    // ─────────────────────────────────────────────────────────────────────────
    public static ConnectorCapabilityDescriptor From(IConnectorAdapter adapter)
    {
        var type = adapter.SystemType;
        var caps = adapter.Capabilities;

        var status = StatusFor(type);

        return new ConnectorCapabilityDescriptor
        {
            SystemType = type,
            DisplayName = adapter.DisplayName,
            Status = status,
            CustomerFacing = CustomerFacingTypes.Contains(type),
            SupportsSource = adapter.SupportsSource,

            // CLEAN flag — SupportsSink directly. Do NOT overload it.
            WritesDuringSync = adapter.SupportsSink,

            ProvisioningPrimitives = new ProvisioningPrimitiveDescriptor
            {
                // declaredFlag AND the sink genuinely overrides the method (see
                // TRUTHFULNESS RULE). The raw flags alone lie (LocalDirectory declares
                // Create/Delete but its EmulatorSink overrides neither → 501/no-op).
                Create = caps.SupportsCreate && OverridesCreateAsync.Contains(type),

                // UPDATE is the one primitive whose interface default is NOT a throwing
                // no-op — IConnectorSink.UpdateAsync delegates to the sink's REQUIRED
                // UpsertAsync (a genuine directory write for every declaring sink), so
                // the declared flag is authoritative here and is not gated by an override
                // table. Non-declaring sinks (SCIM, GenericLdap, Okta, ...) stay false.
                Update = caps.SupportsUpdate,

                Delete = caps.SupportsDelete && OverridesDeleteAsync.Contains(type),
                Move = caps.SupportsMove && OverridesMoveAsync.Contains(type),
                ResetPassword = caps.SupportsResetPassword && OverridesResetPasswordAsync.Contains(type),
            },

            // Separate axis: the sink genuinely overrides IConnectorSink.DeleteAsync.
            DeleteDuringSync = OverridesDeleteAsync.Contains(type),

            // These flags are trustworthy — read them directly.
            MembershipSupport = caps.SupportsGroupMembership,
            LicenseIngest = caps.SupportsLicenseIngest,
            UsageIngest = caps.SupportsUsageReportIngest,
            SignInIngest = caps.SupportsSignInLogIngest,
            AppRoleIngest = caps.SupportsAppRoleIngest,
            IncrementalSupport = caps.SupportsIncremental,

            // future refinement — move object-classes onto the adapter for single-authority.
            // GetObjectClasses is a pure switch on SystemType and never touches the
            // injected IAttributeMapService, so the shared throwaway generator is safe.
            SupportedObjectClasses = ClassProvider
                .GetObjectClasses(type, GenerationMode.Full)
                .ToArray(),

            RequiredCredentials = adapter.CredentialTypes
                .Select(RequiredCredentialDescriptor.From)
                .ToArray(),

            TenantFieldRequirements = adapter.TenantFieldRequirements
                .Select(TenantFieldRequirementDescriptor.From)
                .ToArray(),

            KnownLimitations = KnownLimitationsFor(type),
            TestStatus = TestStatusFor(status),
            DocsPath = $"/docs/connectors/{type}",
        };
    }

    // ── Object-class provider ────────────────────────────────────────────────
    // future refinement — expose GetObjectClasses statically or move the classes onto
    // the adapter so this null-fed instance is unnecessary.
    private static readonly SyncProjectGenerator ClassProvider = new(NoopAttributeMapService.Instance);

    // ── Customer-facing set ──────────────────────────────────────────────────
    // Mirrors ConnectedSystems.razor L400-416 (the onboarding dropdown, 16 types).
    // Emulator + LocalDirectory are intentionally absent.
    // future refinement — expose this as a shared constant that the onboarding list
    // renders from, so the two cannot drift.
    private static readonly HashSet<string> CustomerFacingTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ActiveDirectory", "ActiveRoles", "EntraID", "AzureResourceGraph", "Okta",
        "GoogleWorkspace", "GenericLdap", "Scim", "Database", "SqlDiscovery", "CSV",
        "AWS", "AWSIdentityCenter", "SharePoint", "IdentityCenter", "CertificationCenterSaaS",
    };

    // ── Verified sink-override truth (TRUTHFULNESS RULE) ─────────────────────
    // The raw Supports* flags diverge from what the sinks actually implement. These
    // sets encode, per SystemType, which sinks GENUINELY override the given sink
    // method (verified by code inspection of Conduit.Connectors.*/*Sink.cs). A
    // provisioning primitive is TRUE only when the declared flag AND the override are
    // both true — so a flag that lies (e.g. LocalDirectory SupportsCreate) is
    // suppressed here rather than published as a falsehood.
    //
    // future refinement — derive provisioning-primitive truth from a verified sink
    // capability instead of this hand-maintained table; pinned by tests until then.

    // CreateAsync overridden by exactly 3 sinks: ActiveDirectorySink, EntraIDSink,
    // IdentityCenterSink. CertificationCenterSaaS uses IdentityCenterSink ⇒ it too.
    private static readonly HashSet<string> OverridesCreateAsync = new(StringComparer.OrdinalIgnoreCase)
    {
        "ActiveDirectory", "EntraID", "IdentityCenter", "CertificationCenterSaaS",
    };

    // Sinks that genuinely override IConnectorSink.DeleteAsync (a real delete, not the
    // interface default that returns Skipped). Drives BOTH the Delete provisioning
    // primitive (with the declared flag) AND DeleteDuringSync (alone). CertificationCenterSaaS
    // and AWSIdentityCenter included: they use IdentityCenterSink / AwsSsoSink, both of
    // which override DeleteAsync. Emulator/LocalDirectory/Csv/SharePoint/SqlDiscovery/
    // ActiveRoles/AzureResourceGraph do NOT.
    private static readonly HashSet<string> OverridesDeleteAsync = new(StringComparer.OrdinalIgnoreCase)
    {
        "ActiveDirectory", "EntraID", "IdentityCenter", "CertificationCenterSaaS",
        "GenericLdap", "Scim", "GoogleWorkspace", "Okta", "AWS", "AWSIdentityCenter", "Database",
    };

    // MoveAsync is real only on ActiveDirectorySink (OU move). EntraID has no OU concept.
    private static readonly HashSet<string> OverridesMoveAsync = new(StringComparer.OrdinalIgnoreCase)
    {
        "ActiveDirectory",
    };

    // ResetPasswordAsync overridden by AD, EntraID, IdentityCenter (and thus
    // CertificationCenterSaaS via IdentityCenterSink). Note the declared flag zeroes
    // IdentityCenter/CertificationCenterSaaS (they do not advertise SupportsResetPassword),
    // so the published primitive is false for those two regardless — the override set
    // reflects sink reality; the AND decides the output. Okta is deliberately absent.
    private static readonly HashSet<string> OverridesResetPasswordAsync = new(StringComparer.OrdinalIgnoreCase)
    {
        "ActiveDirectory", "EntraID", "IdentityCenter", "CertificationCenterSaaS",
    };

    // ── Verified Status table (Troi-assigned) ────────────────────────────────
    private static readonly HashSet<string> GaTypes = new(StringComparer.OrdinalIgnoreCase)
    { "ActiveDirectory", "EntraID", "IdentityCenter" };
    private static readonly HashSet<string> PreviewTypes = new(StringComparer.OrdinalIgnoreCase)
    { "CertificationCenterSaaS", "Database", "SqlDiscovery", "AzureResourceGraph", "SharePoint", "Okta", "AWS" };
    private static readonly HashSet<string> PilotTypes = new(StringComparer.OrdinalIgnoreCase)
    { "AWSIdentityCenter", "GoogleWorkspace", "Scim", "GenericLdap", "ActiveRoles", "CSV" };
    // Planned: Emulator, LocalDirectory (the default arm below).

    private static string StatusFor(string type)
    {
        if (GaTypes.Contains(type)) return "GA";
        if (PreviewTypes.Contains(type)) return "Preview";
        if (PilotTypes.Contains(type)) return "Pilot";
        return "Planned";
    }

    // Evidence: GA/Preview are Tested; Pilot/Planned are Untested (matches the
    // per-connector notes — e.g. ActiveRoles "no Conduit-side test coverage yet").
    private static string TestStatusFor(string status) =>
        status is "GA" or "Preview" ? "Tested" : "Untested";

    private static readonly IReadOnlyList<string> GuidBlockedCreate =
        new[] { "Create user is Guid-blocked; read + partial write only." };

    private static IReadOnlyList<string> KnownLimitationsFor(string type) => type switch
    {
        "Okta" => GuidBlockedCreate,
        "GoogleWorkspace" => GuidBlockedCreate,
        "AWS" => GuidBlockedCreate,
        "Scim" => new[] { "Sink is declarative; no write service today — do not imply provisioning works." },
        "GenericLdap" => new[] { "Write path stubbed at the provisioning layer; treat as a read connector." },
        "ActiveRoles" => new[] { "No Conduit-side test coverage yet." },
        "Emulator" => new[] { "Internal fixture — not a customer product." },
        "LocalDirectory" => new[] { "Internal fixture — not a customer product." },
        _ => Array.Empty<string>(),
    };

    /// <summary>
    /// Inert IAttributeMapService so a throwaway <see cref="SyncProjectGenerator"/> can
    /// be constructed purely to reach its pure GetObjectClasses switch. Never invoked.
    /// </summary>
    private sealed class NoopAttributeMapService : IAttributeMapService
    {
        public static readonly NoopAttributeMapService Instance = new();
        public List<AttributeMapping> BuildMappings(
            string sourceSystemType, string sinkSystemType, string objectClass) => new();
    }
}

/// <summary>The five inbound-proxy provisioning primitives, each honesty-gated.</summary>
public sealed class ProvisioningPrimitiveDescriptor
{
    public bool Create { get; init; }
    public bool Update { get; init; }
    public bool Delete { get; init; }
    public bool Move { get; init; }
    public bool ResetPassword { get; init; }
}

/// <summary>Serializable projection of one <see cref="CredentialTypeInfo"/>.</summary>
public sealed class RequiredCredentialDescriptor
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public IReadOnlyList<CredentialFieldDescriptor> Fields { get; init; } = Array.Empty<CredentialFieldDescriptor>();

    public static RequiredCredentialDescriptor From(CredentialTypeInfo info) => new()
    {
        Name = info.Name,
        DisplayName = info.DisplayName,
        Fields = info.Fields.Select(f => new CredentialFieldDescriptor
        {
            Key = f.Key,
            Label = f.Label,
            Required = f.IsRequired,
            Secret = f.IsSecret,
        }).ToArray(),
    };
}

public sealed class CredentialFieldDescriptor
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public bool Required { get; init; }
    public bool Secret { get; init; }
}

/// <summary>Serializable projection of one <see cref="TenantFieldRequirement"/>.</summary>
public sealed class TenantFieldRequirementDescriptor
{
    public string FieldName { get; init; } = string.Empty;
    public bool Required { get; init; }
    public string? HelpText { get; init; }
    public string? Placeholder { get; init; }

    public static TenantFieldRequirementDescriptor From(TenantFieldRequirement r) => new()
    {
        FieldName = r.FieldName,
        Required = r.Required,
        HelpText = r.HelpText,
        Placeholder = r.Placeholder,
    };
}
