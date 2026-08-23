using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Connectors.ActiveRoles;
using Conduit.Connectors.Database;
using Conduit.Connectors.Scim;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Sync.Templates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// Object classes are closed sets, end to end: a connector source refuses a class
/// it cannot read (naming the class), and every class the generator advertises
/// for any connector type either has an attribute template or is explicitly
/// deferred. Before this, an unknown class on SCIM / Database fell through to the
/// User query, and ARS advertised 24 classes with templates for 2.
/// </summary>
public class ObjectClassClosedSetTests
{
    [Theory]
    [InlineData("contact")]
    [InlineData("computer")]
    public async Task ScimSource_refuses_unknown_class_naming_it(string cls)
    {
        var source = new ScimSource(Guid.NewGuid(), null!, null!, NullLogger<ScimSource>.Instance);
        var ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in source.ReadAsync(cls, new SyncProjectScope(), CancellationToken.None)) { }
        });
        Assert.Contains($"'{cls}'", ex.Message);
        Assert.Contains("user, group", ex.Message);
    }

    [Theory]
    [InlineData("contact")]
    [InlineData("computer")]
    public async Task DatabaseSource_refuses_unknown_class_naming_it(string cls)
    {
        var source = new DatabaseSource(Guid.NewGuid(), null!, NullLogger<DatabaseSource>.Instance);
        var ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in source.ReadAsync(cls, new SyncProjectScope(), CancellationToken.None)) { }
        });
        Assert.Contains($"'{cls}'", ex.Message);
        Assert.Contains("user, group", ex.Message);
    }

    [Theory]
    [InlineData("Scim")]
    [InlineData("Database")]
    public void Scim_and_Database_closed_sets_cover_every_generated_class(string systemType)
    {
        var generator = new SyncProjectGenerator(null!);
        foreach (var mode in Enum.GetValues<GenerationMode>())
        foreach (var cls in generator.GetObjectClasses(systemType, mode))
        {
            if (systemType == "Scim") ScimSource.EnsureSupportedObjectClass(cls);
            else DatabaseSource.EnsureSupportedObjectClass(cls);
        }
    }

    private static readonly string[] ConnectorTypes =
    {
        "ActiveDirectory", "ActiveRoles", "EntraID", "SharePoint", "Scim", "Okta",
        "GoogleWorkspace", "AWS", "AWSIdentityCenter", "GenericLdap", "Database",
        "SqlDiscovery", "AzureResourceGraph", "IdentityCenter"
    };

    [Fact]
    public void Every_generated_class_has_a_template_or_is_deferred()
    {
        var generator = new SyncProjectGenerator(null!);
        var missing = new List<string>();
        foreach (var systemType in ConnectorTypes)
        foreach (var mode in Enum.GetValues<GenerationMode>())
        foreach (var cls in generator.GetObjectClasses(systemType, mode))
        {
            if (AttributeTemplateCatalog.Get(systemType, cls) is null
                && !SyncProjectBlueprintCatalog.IsDeferredClass(cls))
                missing.Add($"{systemType}/{cls}");
        }

        Assert.True(missing.Count == 0,
            "Generated classes with no AttributeTemplateCatalog entry and not in DeferredClasses: "
            + string.Join(", ", missing.Distinct()));
    }

    public static IEnumerable<object[]> ArsInfraClasses() =>
        new SyncProjectGenerator(null!).GetObjectClasses("ActiveRoles", GenerationMode.AdInfrastructure)
            .Where(c => !string.Equals(c, "organizationalUnit", StringComparison.OrdinalIgnoreCase))
            .Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(ArsInfraClasses))]
    public async Task ActiveRolesSink_refuses_infra_class_before_bind_naming_it(string cls)
    {
        // Null resolver: reaching ResolveAsync would be a NullReferenceException, so a
        // NotSupportedException proves the gate fired first.
        var sink = new ActiveRolesSink(null!, NullLogger<ActiveRolesSink>.Instance);
        var obj = new ConnectorObject { SourceId = "CN=x,DC=lab", ObjectClass = cls, Attributes = new Dictionary<string, object?>() };
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => sink.UpsertAsync(obj, CancellationToken.None));
        Assert.Contains($"'{cls}'", ex.Message);
        Assert.Contains("user, group, contact, computer, organizationalUnit", ex.Message);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("Group")]
    [InlineData("contact")]
    [InlineData("computer")]
    [InlineData("organizationalunit")]
    public void ActiveRolesSink_gate_passes_every_writable_class(string cls)
    {
        ActiveRolesSink.EnsureSupportedSinkObjectClass(cls);
    }

    [Theory]
    [InlineData("mailbox")]
    [InlineData("*)(objectClass=*")]
    public async Task ActiveRolesSource_refuses_unknown_class_before_credentials_naming_it(string cls)
    {
        var source = new ActiveRolesSource(null!, NullLogger<ActiveRolesSource>.Instance);
        var ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in source.ReadAsync(cls, new SyncProjectScope(), CancellationToken.None)) { }
        });
        Assert.Contains($"'{cls}'", ex.Message);
        Assert.Contains("trustedDomain", ex.Message);
    }

    [Fact]
    public void ActiveRolesSource_closed_set_covers_every_generated_class()
    {
        var generator = new SyncProjectGenerator(null!);
        foreach (var mode in Enum.GetValues<GenerationMode>())
        foreach (var cls in generator.GetObjectClasses("ActiveRoles", mode))
            ActiveRolesSource.EnsureSupportedObjectClass(cls);
    }

    [Fact]
    public void Catalog_values_cannot_be_mutated_through_a_List_downcast()
    {
        foreach (var (systemType, objectClass) in AttributeTemplateCatalog.Keys)
            Assert.IsNotType<List<AttributeTemplateCatalog.Entry>>(AttributeTemplateCatalog.Get(systemType, objectClass));
    }

    // ARS aliases the AD templates, which also resolves real LDAP WRITE names for an
    // AD->ARS project. Parity is safe only because ActiveRolesSink.EnsureSupportedSinkObjectClass
    // refuses every class beyond user/group/contact/computer/organizationalUnit.
    [Fact]
    public void ActiveRoles_templates_match_ActiveDirectory_for_every_shared_class()
    {
        var generator = new SyncProjectGenerator(null!);
        foreach (var cls in generator.GetObjectClasses("ActiveRoles", GenerationMode.Full))
        {
            var ad = AttributeTemplateCatalog.Get("ActiveDirectory", cls)!;
            var ars = AttributeTemplateCatalog.Get("ActiveRoles", cls)!;
            Assert.Contains(ars, e => e.SourceAttribute == "objectGUID" && e.Canonical == "SourceUniqueId");
            // User/Group carry extra ARS virtual attributes; everything else is the AD set verbatim.
            if (cls is "user" or "group") continue;
            Assert.Equal(ad.Select(e => e.SourceAttribute), ars.Select(e => e.SourceAttribute));
        }
    }
}
