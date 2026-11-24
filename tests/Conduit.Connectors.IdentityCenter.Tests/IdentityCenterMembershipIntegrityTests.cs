using System.Text.Json;
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

    /// <summary>
    /// MEMBERSHIP-RECONCILE-01 (UC034-F19): once every chunk landed, the sink names the pushed groups (a group with no
    /// members included) and when the push began, so IC can mark removed the rows it did not refresh. A push with a
    /// failed chunk does not reconcile (its rows were not all refreshed), and a refused reconcile is not an incomplete push.
    /// </summary>
    [Fact]
    public async Task A_complete_push_reconciles_the_pushed_groups_and_a_failed_push_does_not()
    {
        var handler = new CapturingHandler(call => call <= 2 ? Ok(call == 1 ? 1000 : 1) : new(HttpStatusCode.OK) { Content = new StringContent("{\"groupsResolved\":2,\"groupsUnresolved\":0,\"membershipsRemoved\":3}") });
        var before = DateTime.UtcNow;
        var edges = new List<GroupMembership>(Edges(1001)) { new("group-empty", Array.Empty<string>()) };
        Assert.Equal(1001, await Sink(handler).EmitGroupMembershipsAsync("fixture", edges, default));
        Assert.Equal(3, handler.Calls);
        Assert.EndsWith("/api/objects/group-memberships/reconcile", handler.Urls[2]);
        using var doc = JsonDocument.Parse(handler.Bodies[2]);
        var groups = doc.RootElement.GetProperty("groupSourceUniqueIds").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "group-1", "group-empty" }, groups);
        var since = doc.RootElement.GetProperty("seenSinceUtc").GetDateTime();
        Assert.InRange(since, before.AddMinutes(-16), before.AddMinutes(-14));   // the push's start minus the window

        var failing = new CapturingHandler(call => call == 1 ? new(HttpStatusCode.BadRequest) { Content = new StringContent("refused") } : Ok(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sink(failing).EmitGroupMembershipsAsync("fixture", Edges(1001), default));
        Assert.Equal(2, failing.Calls);   // two chunks, no reconcile

        var refusedReconcile = new CapturingHandler(call => call == 1 ? Ok(1) : new(HttpStatusCode.NotFound));
        Assert.Equal(1, await Sink(refusedReconcile).EmitGroupMembershipsAsync("fixture", Edges(1), default));
        Assert.Equal(2, refusedReconcile.Calls);
    }

    [Fact]
    public async Task Complete_chunks_return_the_persisted_count_and_people_target_remains_unsupported_noop()
    {
        var handler = new SequenceHandler(call => Ok(call == 1 ? 1000 : 1));
        Assert.Equal(1001, await Sink(handler).EmitGroupMembershipsAsync("fixture", Edges(1001), default));
        Assert.Equal(3, handler.Calls);   // two chunks and the reconcile (MEMBERSHIP-RECONCILE-01)
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

    /// <summary>
    /// A project that reads accounts from chosen containers and groups from more of the directory has groups naming
    /// members it never reads. Told so, the push leaves those pairs out, counts them and succeeds; not told, the same
    /// answer stays an incomplete effect (Jacob's first Granite read, 2026-11-24: 40,022 such pairs, one failed step).
    /// </summary>
    [Fact]
    public async Task Members_outside_the_read_scope_are_counted_not_failed_only_when_the_project_is_narrowed()
    {
        const string body = "{\"edgesPersisted\":2,\"membersResolved\":2,\"membersUnresolved\":3,\"groupsResolved\":1,\"groupsUnresolved\":0}";
        HttpResponseMessage Answer(int _) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

        var outcome = await Sink(new SequenceHandler(Answer)).EmitGroupMembershipsAsync("fixture", Edges(5), membersOutsideReadScopeExpected: true, default);
        Assert.Equal(2, outcome.EdgesPersisted);
        Assert.Equal(3, outcome.MembersNotHeld);

        var strict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sink(new SequenceHandler(Answer)).EmitGroupMembershipsAsync("fixture", Edges(5), membersOutsideReadScopeExpected: false, default));
        Assert.Contains("membersUnresolved=3", strict.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sink(new SequenceHandler(Answer)).EmitGroupMembershipsAsync("fixture", Edges(5), default));
    }

    [Fact]
    public async Task A_group_the_sink_does_not_hold_and_a_failed_chunk_stay_failures_in_a_narrowed_project()
    {
        const string noGroup = "{\"edgesPersisted\":0,\"membersResolved\":0,\"membersUnresolved\":0,\"groupsResolved\":0,\"groupsUnresolved\":1}";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sink(new SequenceHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(noGroup) }))
                .EmitGroupMembershipsAsync("fixture", Edges(1), membersOutsideReadScopeExpected: true, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sink(new SequenceHandler(_ => new(HttpStatusCode.InternalServerError)))
                .EmitGroupMembershipsAsync("fixture", Edges(1), membersOutsideReadScopeExpected: true, default));
    }

    [Fact]
    public void The_rule_and_its_run_log_line()
    {
        Assert.False(MemberScopeRule.MembersOutsideReadScopeExpected(0));
        Assert.True(MemberScopeRule.MembersOutsideReadScopeExpected(2));
        Assert.Null(MemberScopeRule.LeftOutLine(0));
        var line = MemberScopeRule.LeftOutLine(40022);
        Assert.Contains("left out 40022 membership(s)", line);
        Assert.Contains("not a failure", line);
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

    private sealed class CapturingHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Urls { get; } = new();
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Urls.Add(request.RequestUri?.ToString() ?? string.Empty);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));
            return respond(Calls);
        }
    }
}
