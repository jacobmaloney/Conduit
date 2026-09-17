using System.Net;
using Conduit.Sync.Connectors;
using Conduit.Sync.Orchestration;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Connectors.IdentityCenter.Tests;

public class IdentityCenterMembershipIntegrityTests
{
    private static IdentityCenterSink Sink(HttpMessageHandler handler, string table = "Objects")
    {
        IdentityCenterTableContext.Sink = table;
        return new(Guid.NewGuid(), new SingleClientHttpFactory(handler),
            new StubCredentialProtector("https://fixture.invalid", "fixture-only"), NullLogger<IdentityCenterSink>.Instance);
    }

    private static GroupMembership[] Edges(int count) =>
        [new("group-1", Enumerable.Range(0, count).Select(i => "member-" + i).ToArray())];

    [Fact]
    public async Task Later_successful_chunk_does_not_hide_earlier_failure_or_advance_checkpoint()
    {
        var handler = new SequenceHandler(call => call == 1
            ? new(HttpStatusCode.BadRequest) { Content = new StringContent("refused") }
            : Ok(1));
        var sink = Sink(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sink.EmitGroupMembershipsAsync("fixture", Edges(1001), default));
        Assert.Equal(2, handler.Calls);
        Assert.Contains("failedChunks=1", error.Message);
        Assert.Contains("edgesPersisted=1", error.Message);
        var checkpoints = new SyncCheckpointBuffer(); var saved = "prior";
        Assert.False(checkpoints.Offer(Guid.NewGuid(), new() { Token = "new" }, true, SyncStepDelta.Exception.Failed));
        await checkpoints.CommitAsync(true, (_, cursor) => { saved = cursor.Token; return Task.CompletedTask; }, default);
        Assert.Equal("prior", saved);
    }

    [Theory]
    [InlineData("{\"edgesPersisted\":0,\"membersResolved\":0,\"membersUnresolved\":1,\"groupsResolved\":1,\"groupsUnresolved\":0}")]
    [InlineData("{\"edgesPersisted\":0,\"membersResolved\":0,\"membersUnresolved\":0,\"groupsResolved\":0,\"groupsUnresolved\":1}")]
    [InlineData("invalid-json")]
    [InlineData("{}")]
    [InlineData("{\"edgesPersisted\":\"1\",\"membersResolved\":1,\"membersUnresolved\":0,\"groupsResolved\":1,\"groupsUnresolved\":0}")]
    [InlineData("{\"edgesPersisted\":-1,\"membersResolved\":1,\"membersUnresolved\":0,\"groupsResolved\":1,\"groupsUnresolved\":0}")]
    [InlineData("{\"edgesPersisted\":2,\"membersResolved\":1,\"membersUnresolved\":0,\"groupsResolved\":1,\"groupsUnresolved\":0}")]
    [InlineData("{\"edgesPersisted\":0,\"membersResolved\":0,\"membersUnresolved\":0,\"groupsResolved\":1,\"groupsUnresolved\":0}")]
    public async Task Unresolved_or_unreadable_success_response_is_an_incomplete_effect(string body)
    {
        var sink = Sink(new SequenceHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sink.EmitGroupMembershipsAsync("fixture", Edges(1), default));
        Assert.Contains("incomplete", error.Message);
    }

    [Fact]
    public async Task Complete_chunks_return_the_persisted_count_and_people_target_remains_unsupported_noop()
    {
        var handler = new SequenceHandler(call => Ok(call == 1 ? 1000 : 1));
        Assert.Equal(1001, await Sink(handler).EmitGroupMembershipsAsync("fixture", Edges(1001), default));
        Assert.Equal(2, handler.Calls);
        var unsupported = new SequenceHandler(_ => throw new InvalidOperationException("No request is permitted."));
        Assert.Equal(0, await Sink(unsupported, "Identities").EmitGroupMembershipsAsync("fixture", Edges(1), default));
        Assert.Equal(0, unsupported.Calls);
    }

    [Fact]
    public async Task Duplicate_resolved_members_can_acknowledge_one_distinct_persisted_edge()
    {
        var handler = new SequenceHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"edgesPersisted\":1,\"membersResolved\":2,\"membersUnresolved\":0,\"groupsResolved\":1,\"groupsUnresolved\":0}")
        });
        Assert.Equal(1, await Sink(handler).EmitGroupMembershipsAsync("fixture",
            [new("group-1", new[] { "member-1", "member-1" })], default));
    }

    [Fact]
    public async Task Cancellation_is_preserved()
    {
        var sink = Sink(new SequenceHandler(_ => throw new OperationCanceledException()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sink.EmitGroupMembershipsAsync("fixture", Edges(1), default));
    }

    private static HttpResponseMessage Ok(int count) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"{{\"edgesPersisted\":{count},\"membersResolved\":{count},\"membersUnresolved\":0,\"groupsResolved\":1,\"groupsUnresolved\":0}}")
    };

    private sealed class SequenceHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(++Calls));
    }
}
