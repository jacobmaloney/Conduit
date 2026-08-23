using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Connectors.Database;
using Conduit.Connectors.Scim;
using Conduit.Core.SyncModels;
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
