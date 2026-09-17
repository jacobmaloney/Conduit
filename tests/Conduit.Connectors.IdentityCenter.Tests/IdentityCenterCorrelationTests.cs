using System.Net;
using System.Text.Json;
using Conduit.Sync.Connectors;
using Conduit.Sync.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Connectors.IdentityCenter.Tests;

public class IdentityCenterCorrelationTests
{
    private static ConnectorObject Account(string id = "account-1") => new() { SourceId = id, Attributes = new() {
        ["_source"] = "ActiveDirectory", ["_sourceConnection"] = "HR Directory", ["employeeID"] = "E-10",
        ["department"] = "Research", ["mail"] = "ada@example.test" } };
    private static IdentityCenterSink Sink(HttpMessageHandler handler) => new(Guid.NewGuid(), new SingleClientHttpFactory(handler),
        new StubCredentialProtector("https://offline.example.test", "offline-key"), NullLogger<IdentityCenterSink>.Instance);
    private static object Row(int index, string outcome = "Unmatched", bool matched = false, bool canCreate = true) => new {
        index, outcome, matched, canCreate, reason = "Explained matching outcome", identityId = matched ? Guid.NewGuid().ToString() : null,
        confidence = matched ? .95 : 0, matchedBy = matched ? "Email" : null };

    [Fact]
    public async Task SendsCanonicalSourceContextAndUsesBoundedBatches()
    {
        var handler = new Handler(); var sink = Sink(handler);
        var results = await sink.MatchPeopleAsync(Enumerable.Range(0, 251).Select(i => Account(i.ToString())).ToArray(), default);
        Assert.Equal(251, results.Count); Assert.All(results, r => Assert.True(r.CanCreate));
        Assert.Equal(new[] { 250, 1 }, handler.Counts); Assert.All(handler.Paths, p => Assert.Equal("/api/identities/match/batch", p));
        var item = handler.Bodies[0].GetProperty("items")[0];
        Assert.Equal("HR-Directory", item.GetProperty("sourceConnection").GetString());
        Assert.Equal("0", item.GetProperty("sourceUniqueId").GetString());
        Assert.Equal("Research", item.GetProperty("candidateKeys").GetProperty("department").GetString());
        Assert.Equal("E-10", item.GetProperty("candidateKeys").GetProperty("employeeId").GetString());
    }

    [Theory]
    [InlineData("Link")] [InlineData("Retain")]
    public async Task SuccessfulMatchKeepsWeightAndBlocksCreation(string outcome)
    {
        var handler = new Handler { Response = _ => JsonSerializer.Serialize(new { results = new[] { Row(0, outcome, true, false) } }) };
        var result = await Sink(handler).MatchPersonAsync(Account(), default);
        Assert.Equal(outcome, result.Outcome); Assert.NotNull(result.MatchedIdentityId); Assert.Equal(.95, result.Confidence); Assert.False(result.CanCreate);
        var writer = new Writer(); var created = await PersonCreationDispatch.ExecuteAsync(writer, Account(), new Dictionary<string, PersonMatchResult> { ["account-1"] = result }, default);
        Assert.True(created.Skipped); Assert.Equal(0, writer.Calls);
    }

    [Theory]
    [InlineData("Ambiguous")] [InlineData("Below threshold")] [InlineData("Missing data")]
    [InlineData("Unavailable link")] [InlineData("Unmatched")]
    public async Task UnresolvedResultCarriesReasonAndNeverDispatchesCreation(string outcome)
    {
        var handler = new Handler { Response = _ => JsonSerializer.Serialize(new { results = new[] { Row(0, outcome, false, false) } }) };
        var result = await Sink(handler).MatchPersonAsync(Account(), default);
        Assert.Contains("Explained matching outcome", result.ErrorMessage); Assert.Contains(outcome, result.ErrorMessage); Assert.False(result.CanCreate);
        var writer = new Writer(); var created = await PersonCreationDispatch.ExecuteAsync(writer, Account(), new Dictionary<string, PersonMatchResult> { ["account-1"] = result }, default);
        Assert.False(created.Created); Assert.NotNull(created.Error); Assert.Equal(0, writer.Calls);
    }

    [Theory]
    [InlineData("empty")] [InlineData("short")] [InlineData("reordered")] [InlineData("legacy")] [InlineData("malformed")]
    [InlineData("invalid-id")] [InlineData("contradictory")] [InlineData("contradictory-miss")]
    public async Task BadBatchInvalidatesEveryResultAndCannotCreate(string scenario)
    {
        var handler = new Handler { Response = _ => scenario switch {
            "empty" => "{}", "short" => JsonSerializer.Serialize(new { results = new[] { Row(0) } }),
            "reordered" => JsonSerializer.Serialize(new { results = new[] { Row(1), Row(0) } }),
            "legacy" => "{\"results\":[{\"matched\":false},{\"matched\":false}]}",
            "malformed" => "bad-json",
            "invalid-id" => "{\"results\":[{\"index\":0,\"outcome\":\"Link\",\"matched\":true,\"canCreate\":false,\"reason\":\"bad\",\"identityId\":\"bad\",\"confidence\":1},{}]}",
            "contradictory-miss" => "{\"results\":[{\"index\":0,\"outcome\":\"Unmatched\",\"matched\":false,\"canCreate\":true,\"reason\":\"bad\",\"identityId\":\"not-null\"},{}]}",
            _ => JsonSerializer.Serialize(new { results = new[] { Row(0, "Link", true, true), Row(1) } }) } };
        var results = await Sink(handler).MatchPeopleAsync([Account(), Account("account-2")], default);
        Assert.Equal(2, results.Count); Assert.All(results, r => { Assert.False(r.CanCreate); Assert.NotNull(r.ErrorMessage); Assert.Null(r.MatchedIdentityId); });
        var writer = new Writer();
        await PersonCreationDispatch.ExecuteAsync(writer, Account(), new Dictionary<string, PersonMatchResult> { ["account-1"] = results[0] }, default);
        Assert.Equal(0, writer.Calls);
    }

    [Theory]
    [InlineData(400)] [InlineData(403)] [InlineData(404)]
    public async Task EndpointRefusalIsNeverAMiss(int status)
    {
        var result = await Sink(new Handler { Status = (HttpStatusCode)status, Response = _ => "{\"message\":\"Matching refused\"}" }).MatchPersonAsync(Account(), default);
        Assert.False(result.CanCreate); Assert.Equal("Matching refused", result.ErrorMessage);
    }

    [Fact]
    public async Task ConfirmedMissAllowsOneCreateAndMissingResultBlocksIt()
    {
        var result = await Sink(new Handler()).MatchPersonAsync(Account(), default); var writer = new Writer();
        var blocked = await PersonCreationDispatch.ExecuteAsync(writer, Account(), new Dictionary<string, PersonMatchResult>(), default);
        Assert.Contains("No matching result", blocked.Error); Assert.Equal(0, writer.Calls);
        var created = await PersonCreationDispatch.ExecuteAsync(writer, Account(), new Dictionary<string, PersonMatchResult> { ["account-1"] = result }, default);
        Assert.True(created.Created); Assert.Equal(1, writer.Calls);
    }

    [Fact]
    public async Task CancellationPropagatesAndDoesNotCreate()
    {
        var handler = new Handler { Cancel = true };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sink(handler).MatchPersonAsync(Account(), default));
        using var cts = new CancellationTokenSource(); cts.Cancel(); var writer = new Writer();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PersonCreationDispatch.ExecuteAsync(writer, Account(), null, cts.Token));
        Assert.Equal(0, writer.Calls);
    }

    [Fact]
    public async Task MissingSourceContextIsNotInvented()
    {
        var account = Account(); account.Attributes.Remove("_sourceConnection"); var handler = new Handler();
        await Sink(handler).MatchPersonAsync(account, default);
        Assert.Equal(JsonValueKind.Null, handler.Bodies[0].GetProperty("items")[0].GetProperty("sourceConnection").ValueKind);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<int> Counts { get; } = []; public List<string> Paths { get; } = []; public List<JsonElement> Bodies { get; } = [];
        public HttpStatusCode Status = HttpStatusCode.OK; public bool Cancel; public Func<int, string>? Response;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Cancel) throw new OperationCanceledException();
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
            Bodies.Add(body); Paths.Add(request.RequestUri!.AbsolutePath); var count = body.GetProperty("items").GetArrayLength(); Counts.Add(count);
            return new(Status) { Content = new StringContent(Response?.Invoke(count) ?? JsonSerializer.Serialize(new { results = Enumerable.Range(0, count).Select(i => Row(i)) })) };
        }
    }
    private sealed class Writer : IConnectorSink
    {
        public int Calls;
        public Task<SinkWriteResult> UpsertAsync(ConnectorObject obj, CancellationToken ct) => throw new NotSupportedException();
        public Task<ConnectorTestResult> TestConnectionAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<PersonCreateResult> CreatePersonAsync(ConnectorObject obj, CancellationToken ct)
        { Calls++; return Task.FromResult(PersonCreateResult.Ok(Guid.NewGuid().ToString())); }
    }
}
