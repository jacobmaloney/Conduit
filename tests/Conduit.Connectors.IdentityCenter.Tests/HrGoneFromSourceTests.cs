using System.Text.Json;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Connectors.IdentityCenter.Tests;

/// <summary>
/// HR-GONE-01 (the Granite walk, 2026-11-05): a person a complete HR read no longer contains reaches IdentityCenter's
/// people endpoint, keyed by employee ID, and never the Objects tombstone endpoint (which resolves a connection and
/// could retire unrelated accounts). The Objects sink keeps its own endpoint. Same canned-handler harness as
/// <see cref="IdentityCenterSinkTagTests"/>.
/// </summary>
public class HrGoneFromSourceTests
{
    private const string BaseUrl = "https://api.certification-center.com";
    private const string Processed = "{\"aborted\":false,\"requested\":2,\"matched\":1,\"softDeleted\":1}";

    private static IdentityCenterSink BuildSink(CapturingHandler handler, string table)
    {
        IdentityCenterTableContext.Sink = table;
        var protector = new StubCredentialProtector(BaseUrl, "test-key");
        return new IdentityCenterSink(Guid.NewGuid(), new SingleClientHttpFactory(handler), protector, NullLogger<IdentityCenterSink>.Instance);
    }

    [Fact]
    public async Task People_gone_from_the_source_go_to_the_people_endpoint_by_employee_id()
    {
        var handler = new CapturingHandler(Processed);
        var result = await BuildSink(handler, "Identities").EmitTombstonesAsync("Conduit", new[] { "GC10041", "GC10099", "GC10041" }, CancellationToken.None);

        Assert.Equal("/api/identities/gone-from-source", handler.LastUri!.AbsolutePath);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("employeeId", JsonCI.Prop(doc.RootElement, "KeyField").GetString());
        Assert.Equal(new[] { "GC10041", "GC10099" }, JsonCI.Prop(doc.RootElement, "KeyValues").EnumerateArray().Select(v => v.GetString()).ToArray());
        Assert.False(JsonCI.Prop(doc.RootElement, "Override").GetBoolean());

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.SoftDeleted);
        Assert.Equal(new[] { "GC10041", "GC10099" }, result.PrunableIds);
    }

    [Fact]
    public async Task A_refused_batch_is_not_pruned_so_it_is_offered_again()
    {
        var handler = new CapturingHandler("{\"aborted\":true,\"abortReason\":\"more than half\",\"requested\":30,\"matched\":30,\"softDeleted\":0}");
        var result = await BuildSink(handler, "Identities").EmitTombstonesAsync("Conduit", new[] { "GC10041" }, CancellationToken.None);

        Assert.True(result.Aborted);
        Assert.Empty(result.PrunableIds);
    }

    [Fact]
    public async Task Objects_still_go_to_the_objects_tombstone_endpoint()
    {
        var handler = new CapturingHandler(Processed);
        await BuildSink(handler, "Objects").EmitTombstonesAsync("Conduit", new[] { "guid-1" }, CancellationToken.None);

        Assert.Equal("/api/objects/tombstones", handler.LastUri!.AbsolutePath);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("guid-1", JsonCI.Prop(doc.RootElement, "SourceUniqueIds")[0].GetString());
    }
}
