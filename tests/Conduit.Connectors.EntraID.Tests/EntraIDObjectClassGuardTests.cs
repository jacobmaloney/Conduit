using System;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Connectors.EntraID;
using Conduit.Core.SyncModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Connectors.EntraID.Tests;

/// <summary>
/// The Entra source used to treat any unrecognised object class as "user" and
/// enumerate the whole user collection. It is now a closed set: an unknown class
/// is refused with NotSupportedException before any credential or Graph call.
/// </summary>
public class EntraIDObjectClassGuardTests
{
    private static EntraIDSource NewSource() =>
        new(Guid.NewGuid(), null!, NullLogger<EntraIDSource>.Instance);

    [Theory]
    [InlineData("contact")]
    [InlineData("mailbox")]
    [InlineData("")]
    public async Task ReadAsync_refuses_unknown_class_naming_it(string cls)
    {
        var ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in NewSource().ReadAsync(cls, new SyncProjectScope(), CancellationToken.None)) { }
        });
        Assert.Contains($"'{cls}'", ex.Message);
        Assert.Contains("servicePrincipal", ex.Message);
    }

    [Fact]
    public async Task EnumerateAsync_refuses_unknown_class_naming_it()
    {
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => NewSource().EnumerateAsync("contact", new SyncProjectScope(), null, CancellationToken.None));
        Assert.Contains("'contact'", ex.Message);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("GROUP")]
    [InlineData("ManagerRefresh")]
    [InlineData("GroupMemberships")]
    [InlineData("m365usage")]
    [InlineData("signinlog")]
    [InlineData("license")]
    [InlineData("approleassignment")]
    [InlineData("servicePrincipal")]
    [InlineData("oauth2permissiongrant")]
    public void Guard_accepts_every_class_the_source_can_read(string cls)
    {
        EntraIDSource.EnsureSupportedObjectClass(cls);
    }

    [Fact]
    public void Every_generated_Entra_class_is_in_the_closed_set()
    {
        var generator = new Conduit.Sync.Templates.SyncProjectGenerator(null!);
        foreach (var cls in generator.GetObjectClasses("EntraID", Conduit.Sync.Templates.GenerationMode.Full))
            EntraIDSource.EnsureSupportedObjectClass(cls);
    }
}
