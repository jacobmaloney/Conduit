using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Web.Connectors;

/// <summary>
/// The single authority for the connector-adapter registration list, shared by the
/// capability EXPORT tool and the capability TESTS so both exercise the real 18
/// adapters populated exactly as the app does.
///
/// DRIFT NOTE: Program.cs currently registers the same 18 adapters inline (it is
/// off-limits for this change), so there are, for now, TWO registration sites. The
/// <c>ConnectorRegistry_HasExpectedEighteen</c> test pins the count + SystemTypes so a
/// divergence fails the build.
/// future refinement — Program.cs should call <see cref="AddConduitConnectors"/> so the
/// runtime container and the export/tests share ONE list.
/// </summary>
public static class ConnectorRegistrationExtensions
{
    /// <summary>
    /// Registers all 18 connector adapters + the <see cref="ConnectorRegistry"/>.
    /// Mirrors Program.cs's inline block byte-for-byte (same types, same Scoped lifetime).
    /// </summary>
    public static IServiceCollection AddConduitConnectors(this IServiceCollection services)
    {
        services.AddScoped<IConnectorAdapter, Conduit.Connectors.ActiveDirectory.ActiveDirectoryAdapter>();
        services.AddScoped<IConnectorAdapter, Conduit.Connectors.Emulator.EmulatorAdapter>();
        services.AddScoped<IConnectorAdapter, Conduit.Connectors.Emulator.LocalDirectoryAdapter>();
        services.AddScoped<IConnectorAdapter, Conduit.Connectors.EntraID.EntraIDAdapter>();
        services.AddScoped<IConnectorAdapter, Conduit.Connectors.Okta.OktaAdapter>();
        services.AddScoped<IConnectorAdapter, Conduit.Connectors.GoogleWorkspace.GoogleWorkspaceAdapter>();
        services.AddScoped<IConnectorAdapter, Conduit.Connectors.GenericLdap.GenericLdapAdapter>();
        services.AddScoped<IConnectorAdapter, Conduit.Connectors.Scim.ScimAdapter>();
        services.AddScoped<IConnectorAdapter, Conduit.Connectors.Database.DatabaseAdapter>();
        services.AddScoped<IConnectorAdapter, Conduit.Connectors.Csv.CsvAdapter>();
        services.AddScoped<IConnectorAdapter, Conduit.Connectors.Aws.AwsAdapter>();
        services.AddScoped<IConnectorAdapter, Conduit.Connectors.Aws.AwsSsoAdapter>();
        services.AddScoped<IConnectorAdapter, Conduit.Connectors.SharePoint.SharePointAdapter>();
        services.AddScoped<IConnectorAdapter, Conduit.Connectors.IdentityCenter.IdentityCenterAdapter>();
        services.AddScoped<IConnectorAdapter, Conduit.Connectors.IdentityCenter.CertificationCenterSaaSAdapter>();
        services.AddScoped<IConnectorAdapter, Conduit.Connectors.SqlDiscovery.SqlDiscoveryAdapter>();
        services.AddScoped<IConnectorAdapter, Conduit.Connectors.ActiveRoles.ActiveRolesAdapter>();
        services.AddScoped<IConnectorAdapter, Conduit.Connectors.AzureResourceGraph.AzureResourceGraphAdapter>();
        services.AddScoped<ConnectorRegistry>();
        return services;
    }

    /// <summary>
    /// Registers INERT leaf dependencies sufficient to CONSTRUCT the adapters for
    /// metadata-only reads (capability export + tests). The adapters store these in
    /// their constructors and only touch them inside CreateSource/CreateSink — which
    /// the capability mapper never calls — so throwaway config is safe. NOT a runtime
    /// wiring: repositories point at an empty connection string and are never queried.
    /// </summary>
    public static IServiceCollection AddConnectorMetadataDependencies(this IServiceCollection services)
    {
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(new DatabaseConfig());
        services.AddScoped<TenantRepository>();
        services.AddScoped<ConnectionCredentialRepository>();
        services.AddScoped<CredentialKeyringRepository>();
        services.AddScoped<CredentialProtector>();
        return services;
    }
}
