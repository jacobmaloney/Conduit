using Conduit.Core.Models;
using Conduit.Sync.Provisioning;
using Xunit;

namespace Conduit.Web.Tests;

public class ProvisioningRouteRegistrarTests
{
    [Fact]
    public void IdentityCenter_sink_registers_the_source_connection_name()
    {
        var source = new Tenant { Name = "domain local / AD", SystemType = "ActiveDirectory" };
        var sink = new Tenant { Name = "IdentityCenter", SystemType = "IdentityCenter" };

        Assert.Equal("domain-local-AD", ProvisioningRouteRegistrar.ResolveRouteName(source, sink));
    }

    [Theory]
    [InlineData("ActiveDirectory")]
    [InlineData("EntraID")]
    [InlineData("Emulator")]
    public void Non_IdentityCenter_sinks_do_not_create_a_provisioning_route(string sinkType)
    {
        var source = new Tenant { Name = "domain.local", SystemType = "ActiveDirectory" };
        var sink = new Tenant { Name = "Other", SystemType = sinkType };

        Assert.Null(ProvisioningRouteRegistrar.ResolveRouteName(source, sink));
    }
}
