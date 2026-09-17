using System.DirectoryServices.Protocols;
using Conduit.Readers.ActiveDirectory;
using Xunit;

namespace Conduit.Web.Tests;

public class SharedAdReaderTests
{
    [Fact] public async Task PagesAcrossEveryBaseAndCompletesOnlyAfterFinalCookie()
    {
        var source = new Pages(new AdPage([Row("CN=A,OU=One,DC=x")], [1]), new([Row("CN=B,OU=One,DC=x")], []), new([Row("CN=C,OU=Two,DC=x")], []));
        var state = new AdReadState(); var rows = await Drain(source, new() { IncludedBases = ["OU=One,DC=x", "OU=Two,DC=x"], Attributes = ["mail"] }, state);
        Assert.Equal(3, rows.Count); Assert.True(state.IsComplete); Assert.False(state.IsTruncated);
        Assert.Equal(new byte[] { 1 }, source.Requests[1].Cookie); Assert.Empty(source.Requests[2].Cookie);
        Assert.Equal("OU=Two,DC=x", source.Requests[2].BaseDn); Assert.All(source.Requests, r => Assert.Equal("mail", Assert.Single(r.Query.Attributes)));
    }
    [Theory] [InlineData("missing", "IncompleteRead")] [InlineData("referral", "IncompleteRead")] [InlineData("repeat", "RepeatedPage")]
    public async Task IncompleteOrLoopingPagesCannotClaimCompletion(string fault, string code)
    {
        var page = new AdPage([Row("CN=A,DC=x")], fault == "missing" ? null : [1], fault == "referral");
        var source = new Pages(page, page); var state = new AdReadState();
        var error = await Assert.ThrowsAsync<AdReadException>(() => Drain(source, new(), state));
        Assert.Equal(code, error.Code); Assert.False(state.IsComplete);
    }
    [Fact] public async Task OpaqueCookieMayStayTheSameWhileResultsAdvance()
    {
        var source = new Pages(new AdPage([Row("CN=A,DC=x")], [1]), new AdPage([Row("CN=B,DC=x")], [1]), new AdPage([Row("CN=C,DC=x")], [])); var state = new AdReadState();
        Assert.Equal(3, (await Drain(source, new(), state)).Count); Assert.True(state.IsComplete); Assert.Equal(3, source.Requests.Count);
    }
    [Fact] public async Task SourceFailureAfterRowsDoesNotMarkComplete()
    {
        var source = new Pages(new AdPage([Row("CN=A,DC=x")], [1])); var state = new AdReadState();
        await Assert.ThrowsAsync<IOException>(() => Drain(source, new(), state)); Assert.False(state.IsComplete); Assert.Equal(1, state.Emitted);
    }
    [Fact] public async Task SampleCapDoesNotFetchAnUnneededPageOrClaimComplete()
    {
        var source = new Pages(new AdPage([Row("CN=A,DC=x")], [1])); var state = new AdReadState();
        Assert.Single(await Drain(source, new() { MaximumRows = 1 }, state));
        Assert.True(state.IsTruncated); Assert.False(state.IsComplete); Assert.Single(source.Requests);
    }
    [Fact] public async Task ExactCapWithFinalCookieIsComplete()
    {
        var state = new AdReadState(); Assert.Single(await Drain(new Pages(new AdPage([Row("CN=A,DC=x")], [])), new() { MaximumRows = 1 }, state));
        Assert.True(state.IsComplete); Assert.False(state.IsTruncated);
    }
    [Fact] public async Task EarlyConsumerExitCannotAuthorizeReconciliation()
    {
        var state = new AdReadState();
        await foreach (var row in AdReader.ReadAsync(new Pages(new AdPage([Row("CN=A,DC=x"), Row("CN=B,DC=x")], [])), new(), state)) break;
        Assert.False(state.IsComplete);
    }
    [Theory]
    [InlineData("CN=A,OU=HR,DC=x", "OU=HR,DC=x", true)]
    [InlineData("OU=HR,DC=x", "OU=HR,DC=x", true)]
    [InlineData("CN=A,OU=OtherHR,DC=x", "OU=HR,DC=x", false)]
    [InlineData("CN=Label\\,OU=HR,DC=x", "OU=HR,DC=x", false)]
    [InlineData("CN=A,OU=HR,DC=x", "DC=y", false)]
    public void ContainerChecksUseDnBoundaries(string dn, string parent, bool expected) => Assert.Equal(expected, AdQuery.Within(dn, parent));
    [Theory] [InlineData(SearchScope.Subtree, 1)] [InlineData(SearchScope.OneLevel, 2)] [InlineData(SearchScope.Base, 2)]
    public async Task OnlySubtreeSearchMayRemoveRedundantChildBases(SearchScope depth, int expectedReads)
    {
        var source = new Pages(new AdPage([], []), new([], []));
        await Drain(source, new() { IncludedBases = ["OU=Child,DC=x", "DC=x", "dc=X"], Depth = depth }, new());
        Assert.Equal(expectedReads, source.Requests.Count);
    }
    [Fact] public async Task ExcludedChildrenAndDuplicateEntriesAreRemoved()
    {
        var source = new Pages(new AdPage([Row("CN=A,DC=x"), Row("CN=A,DC=x"), Row("CN=B,OU=Blocked,DC=x")], []));
        Assert.Single(await Drain(source, new() { ExcludedBases = ["OU=Blocked,DC=x"] }, new()));
    }
    [Fact] public async Task MissingRootIsARefusalNotEmptySuccess()
    {
        var source = new Pages { Root = null }; var state = new AdReadState();
        Assert.Equal("MissingNamingContext", (await Assert.ThrowsAsync<AdReadException>(() => Drain(source, new(), state))).Code);
        Assert.Empty(source.Requests); Assert.False(state.IsComplete);
    }
    [Fact] public async Task InvalidScopeAndCancellationDoNotReadDirectory()
    {
        var source = new Pages(); var state = new AdReadState();
        Assert.Equal("InvalidScope", (await Assert.ThrowsAsync<AdReadException>(() => Drain(source, new() { ExcludedBases = [""] }, state))).Code);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Drain(source, new(), state, cancel.Token));
        Assert.Empty(source.Requests); Assert.Equal(0, source.RootReads);
    }
    [Fact] public async Task RangedMembershipIsCompletedBeforeExposingTheRecord()
    {
        var first = Row("CN=Group,DC=x", new() { ["member;range=0-1"] = new object[] { "A", "B" } });
        var source = new Pages(new AdPage([first], [])) { Continuation = Row(first.DistinguishedName, new() { ["member;range=2-*"] = "C" }) };
        var row = Assert.Single(await Drain(source, new(), new()));
        Assert.Equal(new object[] { "A", "B", "C" }, Assert.IsType<object[]>(row.Attributes["member"]));
        Assert.DoesNotContain("member;range=0-1", row.Attributes.Keys); Assert.Equal("member;range=2-*", source.RangeRequest);
    }
    [Fact] public async Task MissingAttributeContinuationRefusesTheWholeRead()
    {
        var first = Row("CN=Group,DC=x", new() { ["member;range=0-1"] = new object[] { "A", "B" } });
        var source = new Pages(new AdPage([first], [])) { Continuation = Row(first.DistinguishedName) }; var state = new AdReadState();
        Assert.Equal("IncompleteAttribute", (await Assert.ThrowsAsync<AdReadException>(() => Drain(source, new(), state))).Code); Assert.False(state.IsComplete);
    }
    [Fact] public void UncommonClassIsEscapedRatherThanChangingTheFilter()
    {
        Assert.Equal("(objectClass=custom\\2a\\29\\28)", AdQuery.FilterForClass("custom*)("));
        Assert.Contains("objectCategory=person", AdQuery.FilterForClass("User"));
    }
    [Fact] public async Task CancellationDuringAReadDiscardsTheResponseAndDoesNotComplete()
    {
        using var cancel = new CancellationTokenSource(); var source = new Pages(new AdPage([], [])) { BeforePage = cancel.Cancel }; var state = new AdReadState();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Drain(source, new(), state, cancel.Token));
        Assert.False(state.IsComplete); Assert.Equal(0, state.Emitted); Assert.Single(source.Requests);
    }
    [Fact] public void ConduitValueAdapterKeepsGuidFileTimeAndUacConventions()
    {
        var id = Guid.NewGuid(); var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var method = typeof(Conduit.Connectors.ActiveDirectory.ActiveDirectorySource).GetMethod("EntryToConnectorObject", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var raw = Row("CN=A,DC=x", new() { ["objectGUID"] = id.ToByteArray(), ["userAccountControl"] = "514", ["pwdLastSet"] = at.ToFileTimeUtc().ToString(), ["memberOf"] = new object[] { "A", "B" } });
        var row = (Conduit.Sync.Connectors.ConnectorObject)method.Invoke(null, [raw, "User"])!;
        Assert.Equal(id.ToString(), row.SourceId); Assert.Equal("false", row.Attributes["isActive"]);
        Assert.Equal(at.ToString("O"), row.Attributes["pwdLastSet"]); Assert.IsType<List<object?>>(row.Attributes["memberOf"]);
    }
    [Fact] public async Task MissingSelectedSourceCredentialCannotBorrowTheSinkCredential()
    {
        var protector = new Credentials();
        var source = new Conduit.Connectors.ActiveDirectory.ActiveDirectorySource(Guid.NewGuid(), null!, protector, Microsoft.Extensions.Logging.Abstractions.NullLogger<Conduit.Connectors.ActiveDirectory.ActiveDirectorySource>.Instance);
        var method = source.GetType().GetMethod("ReadCredsAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        using var named = Conduit.Sync.Security.CredentialNameContext.Push("source-read", "sink-write");
        var task = (Task)method.Invoke(source, null)!; await task;
        Assert.Null(task.GetType().GetProperty("Result")!.GetValue(task)); Assert.Equal(new[] { "source-read" }, protector.Names);
        protector.Available = true; task = (Task)method.Invoke(source, null)!; await task;
        Assert.NotNull(task.GetType().GetProperty("Result")!.GetValue(task)); Assert.DoesNotContain("sink-write", protector.Names);
    }
    private sealed class Credentials() : Conduit.Sync.Security.CredentialProtector(null!, null!, null!)
    {
        public bool Available; public List<string> Names = [];
        public override Task<string?> RetrieveAsync(Guid tenant, string name)
        {
            Names.Add(name);
            return Task.FromResult(Available ? "{\"Username\":\"fixture\",\"Password\":\"fixture-only\"}" : null);
        }
    }
    private static AdRecord Row(string dn, Dictionary<string, object?>? fields = null) => new(dn, fields ?? []);
    private static async Task<List<AdRecord>> Drain(IAdPageSource source, AdQuery query, AdReadState state, CancellationToken ct = default)
    { var rows = new List<AdRecord>(); await foreach (var row in AdReader.ReadAsync(source, query, state, ct)) rows.Add(row); return rows; }
    private sealed class Pages(params AdPage[] pages) : IAdPageSource
    {
        private readonly Queue<AdPage> queue = new(pages);
        public Action? BeforePage; public List<AdPageRequest> Requests = []; public string? Root = "DC=x"; public int RootReads;
        public AdRecord? Continuation; public string? RangeRequest;
        public Task<string?> DefaultNamingContextAsync(TimeSpan timeout, CancellationToken ct) { RootReads++; return Task.FromResult(Root); }
        public Task<AdPage> ReadPageAsync(AdPageRequest request, CancellationToken ct) { Requests.Add(request); BeforePage?.Invoke(); return Task.FromResult(queue.TryDequeue(out var next) ? next : throw new IOException("fixture connection lost")); }
        public Task<AdRecord> ReadEntryAsync(string dn, IReadOnlyList<string> attributes, TimeSpan timeout, CancellationToken ct)
        { RangeRequest = Assert.Single(attributes); return Task.FromResult(Continuation ?? throw new IOException("fixture range failed")); }
    }
}
