using System.Linq;
using Conduit.Sync.Connectors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Connectors.IdentityCenter.Tests;

/// <summary>
/// Pins the Certification Center adapter's presentation contract and its
/// resolvability through <see cref="ConnectorRegistry"/> — including that it
/// coexists with the IdentityCenter adapter (distinct SystemType, no collision).
/// </summary>
public class CertificationCenterSaaSAdapterTests
{
    private static CertificationCenterSaaSAdapter BuildAdapter()
    {
        var factory = new SingleClientHttpFactory(new CapturingHandler());
        var protector = new StubCredentialProtector("https://api.certification-center.com", "k");
        return new CertificationCenterSaaSAdapter(factory, protector, NullLoggerFactory.Instance);
    }

    [Fact]
    public void Registry_resolves_the_branded_type_alongside_IdentityCenter()
    {
        var branded = BuildAdapter();
        var ic = new IdentityCenterAdapter(
            new SingleClientHttpFactory(new CapturingHandler()),
            new StubCredentialProtector("https://ic.local", "k"),
            NullLoggerFactory.Instance);

        var registry = new ConnectorRegistry(new IConnectorAdapter[] { branded, ic });

        var resolved = registry.Get("CertificationCenterSaaS");
        Assert.NotNull(resolved);
        Assert.IsType<CertificationCenterSaaSAdapter>(resolved);
        Assert.Equal("Certification Center", resolved!.DisplayName);
        Assert.True(resolved.SupportsSink);

        // No collision: the IdentityCenter type still resolves to its own adapter.
        Assert.IsType<IdentityCenterAdapter>(registry.Get("IdentityCenter"));
    }

    [Fact]
    public void Credential_is_minimal_paste_key_and_go()
    {
        var adapter = BuildAdapter();

        var ctype = Assert.Single(adapter.CredentialTypes);
        // Reuses the IC credential name ("identitycenter") so the reader/plumbing is
        // byte-identical — the reader's internal const is what the adapter binds to.
        Assert.Equal("identitycenter", ctype.Name);

        var baseUrl = ctype.Fields.Single(f => f.Key == "BaseUrl");
        Assert.Equal("https://api.certification-center.com", baseUrl.DefaultValue);

        var apiKey = ctype.Fields.Single(f => f.Key == "ApiKey");
        Assert.True(apiKey.IsSecret);

        // The power-user AgentApiKey field is intentionally NOT exposed.
        Assert.DoesNotContain(ctype.Fields, f => f.Key == "AgentApiKey");
    }

    [Fact]
    public void Sink_is_the_reused_IdentityCenter_sink()
    {
        var adapter = BuildAdapter();
        Assert.True(adapter.Capabilities.SupportsBulk);
        Assert.IsType<IdentityCenterSink>(adapter.CreateSink(Guid.NewGuid()));
    }
}
