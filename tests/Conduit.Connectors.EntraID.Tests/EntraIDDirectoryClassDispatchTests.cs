using Conduit.Connectors.EntraID;
using Conduit.Sync.Templates;
using Xunit;

namespace Conduit.Connectors.EntraID.Tests;

/// <summary>
/// Guards the EntraID extended-directory-class wiring. These tests fail if
/// someone reverts either (a) the generator advertising the 8 new classes or
/// (b) the connector's dispatch set that routes those classes away from the
/// User default. They do NOT require a live Graph tenant — live-Graph dispatch
/// (class string -> correct Graph collection -> emitted ObjectClass) needs a
/// real tenant because GraphServiceClient is a sealed Kiota client and cannot
/// be mocked here.
/// </summary>
public class EntraIDDirectoryClassDispatchTests
{
    // The 8 classes this work added, in the generator's exact native casing.
    public static readonly string[] ExtendedClasses =
    {
        "application", "servicePrincipal", "directoryRole", "device",
        "administrativeUnit", "conditionalAccessPolicy", "oAuth2PermissionGrant", "domain"
    };

    [Fact]
    public void Generator_EntraFull_advertises_all_eight_extended_classes()
    {
        // GetObjectClasses does not use the injected IAttributeMapService.
        var generator = new SyncProjectGenerator(null!);
        var classes = generator.GetObjectClasses("EntraID", GenerationMode.Full);

        foreach (var cls in ExtendedClasses)
            Assert.Contains(cls, classes);

        // base classes still present
        Assert.Contains("user", classes);
        Assert.Contains("group", classes);
    }

    [Theory]
    [InlineData("application")]
    [InlineData("servicePrincipal")]
    [InlineData("directoryRole")]
    [InlineData("device")]
    [InlineData("administrativeUnit")]
    [InlineData("conditionalAccessPolicy")]
    [InlineData("oAuth2PermissionGrant")]
    [InlineData("domain")]
    public void Dispatch_recognises_each_extended_class_case_insensitively(string cls)
    {
        Assert.True(EntraIDSource.IsExtendedDirectoryClass(cls));
        Assert.True(EntraIDSource.IsExtendedDirectoryClass(cls.ToUpperInvariant()));
        Assert.True(EntraIDSource.IsExtendedDirectoryClass(cls.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("group")]
    [InlineData("ManagerRefresh")]
    [InlineData("GroupMemberships")]
    public void Dispatch_does_not_claim_existing_classes(string cls)
    {
        Assert.False(EntraIDSource.IsExtendedDirectoryClass(cls));
    }
}
