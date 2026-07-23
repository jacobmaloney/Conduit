using System.Text.Json;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Connectors.IdentityCenter.Tests;

/// <summary>
/// Pins the load-bearing sink contract the Certification Center adapter inherits
/// UNCHANGED by reusing <see cref="IdentityCenterSink"/>:
///   (a) objectClass is forwarded lowercased on the bulk body — IC's ingest gate
///       DROPS rows with no class, so groups vanish if this regresses.
///   (b) the People/Systems (Identities/Objects) target routes to the right bulk
///       endpoint — the branded People/Systems picker writes the same TargetTable
///       the sink resolves here.
/// No live IC: a capturing handler asserts on the request the sink built.
/// </summary>
public class IdentityCenterSinkDispatchTests
{
    private const string BaseUrl = "https://api.certification-center.com";

    private static IdentityCenterSink BuildSink(CapturingHandler handler)
    {
        var factory = new SingleClientHttpFactory(handler);
        var protector = new StubCredentialProtector(BaseUrl, "test-key");
        return new IdentityCenterSink(Guid.NewGuid(), factory, protector, NullLogger<IdentityCenterSink>.Instance);
    }

    private static ConnectorObject Obj(string objectClass) => new()
    {
        SourceId = "src-1",
        ObjectClass = objectClass,
        Attributes = new() { ["displayName"] = "Admins" }
    };

    [Theory]
    [InlineData("Group", "group")]
    [InlineData("USER", "user")]
    [InlineData("device", "device")]
    public async Task ObjectClass_is_forwarded_lowercased_on_the_bulk_body(string sent, string expected)
    {
        IdentityCenterTableContext.Sink = "Objects";
        var handler = new CapturingHandler();
        var sink = BuildSink(handler);

        await sink.UpsertBatchAsync(new[] { Obj(sent) }, CancellationToken.None);

        Assert.NotNull(handler.LastBody);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var item = JsonCI.Prop(doc.RootElement, "Items")[0];
        Assert.Equal(expected, JsonCI.Prop(item, "ObjectClass").GetString());
    }

    [Theory]
    [InlineData("Objects", "/api/objects/bulk")]
    [InlineData("Identities", "/api/identities/bulk")]
    [InlineData(null, "/api/objects/bulk")] // unset → Objects (back-compat default)
    public async Task Target_table_routes_to_the_right_bulk_endpoint(string? targetTable, string expectedPath)
    {
        IdentityCenterTableContext.Sink = targetTable;
        var handler = new CapturingHandler();
        var sink = BuildSink(handler);

        await sink.UpsertBatchAsync(new[] { Obj("user") }, CancellationToken.None);

        Assert.NotNull(handler.LastUri);
        Assert.Equal(BaseUrl + expectedPath, handler.LastUri!.ToString());
    }
}
