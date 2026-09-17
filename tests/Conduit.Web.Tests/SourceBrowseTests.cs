using System.Runtime.CompilerServices;
using System.Security.Claims;
using Conduit.Core.Models;
using Conduit.Core.SyncModels;
using Conduit.SourceBrowsing;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Conduit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Web.Tests;

public partial class SourceBrowseTests
{
    private readonly Store store = new();
    private readonly Adapter adapter = new();
    private static ClaimsPrincipal Admin => new(new ClaimsIdentity([new(ClaimTypes.Role, "Admin")], "Cookies"));
    private SourceBrowseService Service(bool editable = false) => new(store, new([adapter]), new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "test-host"), NullLogger<SourceBrowseService>.Instance, editable ? new Targets() : null);
    private SourceBrowseRequest Request(int limit = 2) => new(new(store.Connection.Id), new(limit, "User"));
    private SourceBrowseRequest ProjectRequest(int limit = 2) => new(new(store.Connection.Id, store.Project.Id, store.Step.Id), new(limit));

    [Fact] public async Task ConnectionSampleUsesSourceOnlyWithLookaheadAndNamedHost()
    {
        var result = await Service().ReadAsync(Admin, Request(), default);
        Assert.Equal(2, result.Rows.Count); Assert.True(result.HasMore); Assert.Equal(3, adapter.Source.Yielded);
        Assert.Equal("Conduit · test-host", result.Context.ExecutionLocation);
        Assert.Equal(store.Connection.Id, adapter.CreatedFor); Assert.Equal(3, adapter.Source.Scope!.MaxObjects);
        Assert.Equal("[hidden]", result.Rows[0].Fields.Single(f => f.Name == "password").Value);
    }
    [Fact] public async Task WrongExecutionHostRefusesBeforeMetadataOrSourceRead()
    {
        await Refuses("WrongExecutionHost", Request() with { ExpectedInstanceId = Guid.NewGuid() });
        var error = await Assert.ThrowsAsync<SourceBrowseException>(() => Service().DescribeAsync(Admin, Request().Selection, default, Guid.NewGuid()));
        Assert.Equal("WrongExecutionHost", error.Code); Assert.Equal(0, store.Reads); Assert.Equal(0, adapter.Source.Yielded);
    }
    [Fact] public async Task ProjectUsesSavedStepClassScopesCredentialAndCapWithoutMutation()
    {
        store.Scope.MaxObjects = 1;
        using var outer = CredentialNameContext.Push("outer", "outer-sink");
        var result = await Service().ReadAsync(Admin, ProjectRequest(), default);
        Assert.Single(result.Rows); Assert.False(result.HasMore);
        Assert.Equal("Group", adapter.Source.Class); Assert.Equal("read-only", adapter.Source.Credential);
        Assert.Equal(store.Scope.IncludedBaseDNs, adapter.Source.Scope!.IncludedBaseDNs);
        Assert.Equal(store.Scope.ExcludedBaseDNs, adapter.Source.Scope.ExcludedBaseDNs);
        Assert.Equal("(name=Finance*)", adapter.Source.Scope.LdapFilter);
        Assert.Equal(1, store.Scope.MaxObjects); Assert.Equal("outer", CredentialNameContext.Source); Assert.Equal("outer-sink", CredentialNameContext.Sink);
    }
    [Fact] public async Task ProjectRequiresExplicitStepAndRefusesScopeReplacement()
    {
        var description = await Service().DescribeAsync(Admin, new(store.Connection.Id, store.Project.Id), default);
        Assert.False(description.CanBrowse); Assert.Single(description.Steps); Assert.Equal(0, adapter.Source.Yielded);
        Assert.Equal($"Group - {store.Step.Name}", description.Steps[0].Name);
        await Refuses("SelectImportStep", new(new(store.Connection.Id, store.Project.Id), new(2)));
        await Refuses("SavedScopeRequired", ProjectRequest() with { Query = new(2, BaseDn: "DC=other") });
    }
    [Fact] public async Task ForeignStepAndConnectionDoNotReadSource()
    {
        await Refuses("StepUnavailable", ProjectRequest() with { Selection = new(store.Connection.Id, store.Project.Id, Guid.NewGuid()) });
        await Refuses("SourceMismatch", ProjectRequest() with { Selection = new(Guid.NewGuid(), store.Project.Id, store.Step.Id) });
    }
    [Fact] public async Task InvalidSavedScopeFailsClosed()
    {
        store.Scope.IncludedBaseDNs = "not-json";
        await Refuses("InvalidScope", ProjectRequest());
    }
    [Theory] [InlineData("EntraID")] [InlineData("Custom")]
    public async Task UnsupportedConnectorIsNotEnumerated(string type)
    {
        store.Connection.SystemType = type; await Refuses("UnsupportedReader", Request());
    }
    [Fact] public async Task InactiveConnectionIsNotEnumerated()
    {
        store.Connection.IsActive = false; await Refuses("InactiveConnection", Request());
    }
    [Theory] [InlineData(0)] [InlineData(201)]
    public async Task InvalidLimitDoesNotReadMetadataOrSource(int limit)
    {
        await Refuses("InvalidLimit", Request(limit)); Assert.Equal(0, store.Reads);
    }
    [Fact] public async Task TenantTokenMustOwnSourceEvenWhenItOwnsSink()
    {
        var token = Token(store.Project.SinkTenantId);
        await Refuses("SourceAccessDenied", ProjectRequest(), token);
        var allowed = await Service().ReadAsync(Token(store.Project.SourceTenantId), ProjectRequest(), default);
        Assert.Equal(2, allowed.Rows.Count);
    }
    [Theory] [InlineData("ArsProxy")] [InlineData("Unknown")]
    public async Task OtherTokenScopesCannotReadSource(string scope)
    {
        await Refuses("SourceAccessDenied", Request(), Token(store.Connection.Id, scope));
    }
    [Fact] public async Task SignedOutAndNonAdminAreRefusedBeforeMetadata()
    {
        await Refuses("AuthenticationRequired", Request(), new ClaimsPrincipal());
        await Refuses("AdministratorRequired", Request(), new(new ClaimsIdentity([], "Cookies")));
        Assert.Equal(0, store.Reads);
    }
    [Fact] public async Task MidStreamFailureDiscardsRowsAndHidesSourceException()
    {
        adapter.Source.Fail = true;
        var error = await Assert.ThrowsAsync<SourceBrowseException>(() => Service().ReadAsync(Admin, Request(20), default));
        Assert.Equal("SourceReadFailed", error.Code); Assert.DoesNotContain("PRIVATE", error.Message); Assert.Equal(1, adapter.Source.Yielded);
    }
    [Fact] public async Task IncompleteAdReadIsNotEmptySuccess()
    {
        store.Connection.SystemType = adapter.Type = "ActiveDirectory"; adapter.Source.Count = 0; adapter.Source.Complete = false;
        await Refuses("IncompleteRead", Request());
        adapter.Source.Complete = true;
        var empty = await Service().ReadAsync(Admin, Request(), default); Assert.Empty(empty.Rows); Assert.False(empty.HasMore);
    }
    [Fact] public async Task CancellationDoesNotBecomeAnEmptySample()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().ReadAsync(Admin, Request(), cts.Token));
        Assert.Equal(0, store.Reads); Assert.Equal(0, adapter.Source.Yielded);
    }
    [Theory]
    [InlineData("Password")] [InlineData("client_secret")] [InlineData("access-token")]
    [InlineData("unicodePwd")] [InlineData("ms-Mcs-AdmPwd")] [InlineData("msLAPS-Password")]
    [InlineData("privateKey")] [InlineData("API_KEY")]
    public void SecretFieldsAreHiddenBeforeCrossingUiOrHttp(string name)
    {
        var row = SourceBrowseSample.Row(1, new Dictionary<string, object?> { [name] = "PRIVATE" });
        Assert.Equal("[hidden]", Assert.Single(row.Fields).Value);
    }
    [Fact] public void ValuesAndFieldCountsAreBoundedWithoutObjectSerialization()
    {
        var values = Enumerable.Range(0, 100).Select(i => new KeyValuePair<string, object?>("field" + i, new string('x', 9000)));
        var row = SourceBrowseSample.Row(1, values);
        Assert.Equal(81, row.Fields.Count); Assert.Contains("shortened", row.Fields[0].Value);
        Assert.Equal("[binary value]", SourceBrowseSample.Row(1, new Dictionary<string, object?> { ["photo"] = new byte[1000] }).Fields[0].Value);
    }
    [Fact] public async Task MappingReadUsesSavedStepMetadataAndProjectsTheActualSourceSample()
    {
        var context = await Service().DescribeAsync(Admin, ProjectRequest().Selection, default);
        Assert.Equal("label", Assert.Single(context.Mapping!.SavedRules).Target);
        var request = ProjectRequest() with { Query = new(2, Mappings: [new("name", "label", "upper")]) };
        var page = await Service().ReadAsync(Admin, request, default);
        Assert.Equal("ROW 0", Assert.Single(page.Rows[0].MappedFields!).Output);
        Assert.Equal("read-only", adapter.Source.Credential); Assert.Equal("Group", adapter.Source.Class);
        Assert.Equal("name", Assert.Single(context.Mapping.SavedRules).Source);
    }
    [Fact] public async Task SecretMappingRefusesBeforeAnySourceOrMetadataRead()
    {
        await Refuses("SensitiveMapping", Request() with { Query = new(2, Mappings: [new("password", "label", "upper")]) });
        Assert.Equal(0, store.Reads); Assert.Equal(0, adapter.Source.Yielded);
    }
    private async Task Refuses(string code, SourceBrowseRequest request, ClaimsPrincipal? user = null)
    {
        var reads = adapter.Source.Yielded;
        var error = await Assert.ThrowsAsync<SourceBrowseException>(() => Service().ReadAsync(user ?? Admin, request, default));
        Assert.Equal(code, error.Code); Assert.Equal(reads, adapter.Source.Yielded);
    }
    private static ClaimsPrincipal Token(Guid tenant, string scope = "Tenant") => new(new ClaimsIdentity([new("TokenScope", scope), new("TenantId", tenant.ToString())], "ApiToken"));
    private sealed class Store : ISourceBrowseStore
    {
        public Tenant Connection = new() { Id = Guid.NewGuid(), Name = "CSV fixture", SystemType = "CSV" };
        public SyncProject Project;
        public WorkflowStep Step = new() { Name = "Groups", ObjectClass = "Group" };
        public SyncProjectScope Scope = new() { IncludedBaseDNs = "[\"OU=Finance,DC=example\"]", ExcludedBaseDNs = "[\"OU=Blocked,OU=Finance,DC=example\"]", LdapFilter = "(name=Finance*)" };
        public int Reads;
        public Tenant Target = new() { Name = "Target", SystemType = "ActiveDirectory" };
        public List<AttributeMapping> Mappings = [new() { SourceAttribute = "name", SinkAttribute = "label", TransformExpr = "upper" }];
        public List<AttributeMapping> ProjectMappings = [];
        public Action? BeforeSave;
        public int Saves;
        public bool Conflict, FailReload;
        public Store() => Project = new() { Name = "Fixture project", SourceTenantId = Connection.Id, SinkTenantId = Target.Id, SourceCredentialName = "read-only" };
        public Task<Tenant?> ConnectionAsync(Guid id) { Reads++; if (FailReload && Saves > 0) throw new IOException("PRIVATE"); return Task.FromResult<Tenant?>(id == Connection.Id ? Connection : id == Target.Id ? Target : null); }
        public Task<SyncProject?> ProjectAsync(Guid id) { Reads++; return Task.FromResult<SyncProject?>(id == Project.Id ? Project : null); }
        public Task<IReadOnlyList<WorkflowStep>> ImportStepsAsync(Guid id) => Task.FromResult<IReadOnlyList<WorkflowStep>>([Step]);
        public Task<IReadOnlyList<AttributeMapping>> MappingsAsync(Guid id) => Task.FromResult<IReadOnlyList<AttributeMapping>>(Mappings);
        public Task<IReadOnlyList<AttributeMapping>> ProjectMappingsAsync(Guid id) => Task.FromResult<IReadOnlyList<AttributeMapping>>(ProjectMappings);
        public Task SaveMappingsAsync(Guid projectId, Guid stepId, IReadOnlyList<AttributeMapping> mappings, MappingSaveCondition condition, CancellationToken ct)
        {
            Assert.Equal(Project.Id, projectId); Assert.Equal(Step.Id, stepId);
            BeforeSave?.Invoke();
            if (Conflict || condition.Revision != MappingSaveCondition.Version(Project, Step, Mappings, Scope, ProjectMappings)) throw new InvalidOperationException("MappingsChanged: fixture conflict");
            Saves++; Mappings = mappings.ToList(); return Task.CompletedTask;
        }
        public Task<SyncProjectScope?> ScopeAsync(Guid id, Guid? stepId) => Task.FromResult<SyncProjectScope?>(Scope);
    }
    private sealed class Adapter : IConnectorAdapter
    {
        public string Type = "CSV";
        public string SystemType => Type;
        public string DisplayName => Type;
        public bool SupportsSource => true;
        public bool SupportsSink => false;
        public Source Source = new();
        public Guid CreatedFor;
        public IConnectorSource CreateSource(Guid id) { CreatedFor = id; return Source; }
        public IConnectorSink? CreateSink(Guid id) => throw new InvalidOperationException("A browser must never create a sink.");
    }
    private sealed class Source : IConnectorSource
    {
        public int Count = 5, Yielded;
        public bool Fail, Complete = true;
        public string? Class, Credential;
        public SyncProjectScope? Scope;
        public Task<SyncEnumerationResult> EnumerateAsync(string objectClass, SyncProjectScope scope, SyncCursor? cursor, CancellationToken ct) =>
            Task.FromResult(new SyncEnumerationResult { Objects = ReadAsync(objectClass, scope, ct), WasCompleteRead = () => Complete });
        public async IAsyncEnumerable<ConnectorObject> ReadAsync(string objectClass, SyncProjectScope scope, [EnumeratorCancellation] CancellationToken ct)
        {
            Class = objectClass; Scope = scope; Credential = CredentialNameContext.Source;
            for (var i = 0; i < Math.Min(Count, scope.MaxObjects ?? int.MaxValue); i++)
            {
                ct.ThrowIfCancellationRequested(); await Task.Yield();
                if (Fail && i == 1) throw new IOException("PRIVATE path or bind information");
                Yielded++;
                yield return new() { SourceId = i.ToString(), Attributes = new() { ["name"] = "Row " + i, ["password"] = "PRIVATE" } };
            }
        }
        public Task<ConnectorTestResult> TestConnectionAsync(CancellationToken ct) => throw new InvalidOperationException("Browsing must not run connection probes.");
    }
}
