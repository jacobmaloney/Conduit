using System;
using System.Collections.Generic;
using System.Linq;
using Conduit.Sync.Connectors;
using Conduit.Web.Connectors;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// Pins the marketing-truthful capability mapper. Builds the REAL 18-adapter registry
/// via the same AddConduitConnectors registration the app and the export tool use, then
/// asserts the two-axis write rendering and the honesty-gated provisioning primitives.
/// </summary>
public class ConnectorCapabilityDescriptorTests
{
    private static readonly string[] ExpectedSystemTypes =
    {
        "ActiveDirectory", "ActiveRoles", "AWS", "AWSIdentityCenter", "AzureResourceGraph",
        "CertificationCenterSaaS", "CSV", "Database", "Emulator", "EntraID", "GenericLdap",
        "GoogleWorkspace", "IdentityCenter", "LocalDirectory", "Okta", "Scim",
        "SharePoint", "SqlDiscovery",
    };

    private static Dictionary<string, ConnectorCapabilityDescriptor> BuildDescriptors()
    {
        var provider = new ServiceCollection()
            .AddConnectorMetadataDependencies()
            .AddConduitConnectors()
            .BuildServiceProvider();
        using var scope = provider.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ConnectorRegistry>();

        return registry.All
            .Select(ConnectorCapabilityDescriptor.From)
            .ToDictionary(d => d.SystemType, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Okta_ResetPasswordFalse_DeleteDuringSyncTrue()
    {
        var okta = BuildDescriptors()["Okta"];
        Assert.False(okta.ProvisioningPrimitives.ResetPassword);
        Assert.True(okta.DeleteDuringSync);
    }

    [Fact]
    public void Scim_WritesDuringSync_ButNoProvisioningPrimitives()
    {
        var scim = BuildDescriptors()["Scim"];
        Assert.True(scim.WritesDuringSync);
        var p = scim.ProvisioningPrimitives;
        Assert.False(p.Create);
        Assert.False(p.Update);
        Assert.False(p.Delete);
        Assert.False(p.Move);
        Assert.False(p.ResetPassword);
    }

    [Fact]
    public void Create_TrueForExactlyTheFourWritableDirectories()
    {
        var expectedCreators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ActiveDirectory", "EntraID", "IdentityCenter", "CertificationCenterSaaS",
        };

        var descriptors = BuildDescriptors();
        foreach (var (systemType, descriptor) in descriptors)
        {
            var expected = expectedCreators.Contains(systemType);
            Assert.True(
                descriptor.ProvisioningPrimitives.Create == expected,
                $"{systemType} Create expected {expected} but was {descriptor.ProvisioningPrimitives.Create}");
        }

        // Explicit: the flag lies (SupportsCreate=true) but the sink does not override CreateAsync.
        Assert.False(descriptors["LocalDirectory"].ProvisioningPrimitives.Create);
    }

    [Fact]
    public void LocalDirectory_DeletePrimitiveFalse()
    {
        var local = BuildDescriptors()["LocalDirectory"];
        Assert.False(local.ProvisioningPrimitives.Delete);
    }

    [Fact]
    public void GenericLdap_DeleteDuringSyncTrue_NoProvisioningPrimitives()
    {
        var ldap = BuildDescriptors()["GenericLdap"];
        Assert.True(ldap.DeleteDuringSync);
        var p = ldap.ProvisioningPrimitives;
        Assert.False(p.Create);
        Assert.False(p.Update);
        Assert.False(p.Delete);
        Assert.False(p.Move);
        Assert.False(p.ResetPassword);
    }

    [Fact]
    public void EntraID_MoveFalse()
    {
        var entra = BuildDescriptors()["EntraID"];
        Assert.False(entra.ProvisioningPrimitives.Move);
    }

    [Fact]
    public void EveryAdapter_YieldsCompleteDescriptor()
    {
        var descriptors = BuildDescriptors();
        Assert.Equal(ExpectedSystemTypes.Length, descriptors.Count);

        foreach (var descriptor in descriptors.Values)
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.SystemType));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.DisplayName));
            Assert.NotNull(descriptor.ProvisioningPrimitives);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Status));
        }
    }

    [Fact]
    public void ConnectorRegistry_HasExpectedEighteen()
    {
        var descriptors = BuildDescriptors();
        Assert.Equal(
            ExpectedSystemTypes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            descriptors.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }
}
