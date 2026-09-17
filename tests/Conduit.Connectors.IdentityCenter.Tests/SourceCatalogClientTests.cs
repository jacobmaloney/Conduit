using System.Net;
using System.Text.Json;
using Conduit.SourceBrowsing;
using Conduit.Sync.Security;
using Xunit;

namespace Conduit.Connectors.IdentityCenter.Tests;

public class SourceCatalogClientTests
{
    [Fact] public async Task CatalogUsesSavedCredentialHostAndOnlyReadsMetadata()
    {
        var id = Guid.NewGuid(); var credentials = new Credentials(); var handler = new Handler(); var factory = new Factory(handler);
        var result = await new IdentityCenterSourceCatalogClient(credentials, factory).ReadAsync(id, default);
        Assert.Equal(id, credentials.Id); Assert.Equal("identitycenter", credentials.Name);
        Assert.Equal("https://ic-api.example.test/api/source-catalog", handler.Uri!.AbsoluteUri); Assert.Equal("fixture-only", handler.Key);
        Assert.Equal(HttpMethod.Get, handler.Method); Assert.Equal("IC Local", result.ExecutionLocation);
        Assert.Equal(IdentityCenterSourceCatalogClient.HttpClientName, factory.Name);
    }
    [Theory] [InlineData(401, "AccessDenied")] [InlineData(403, "AccessDenied")] [InlineData(404, "EndpointMissing")] [InlineData(503, "RemoteFailure")]
    public async Task RemoteFailuresAreNotEmptyCatalogs(int status, string code)
    {
        var handler = new Handler { Status = (HttpStatusCode)status };
        var error = await Assert.ThrowsAsync<SourceBrowseException>(() => new IdentityCenterSourceCatalogClient(new Credentials(), new Factory(handler)).ReadAsync(Guid.NewGuid(), default));
        Assert.Equal(code, error.Code); Assert.DoesNotContain("PRIVATE", error.Message);
    }
    [Fact] public async Task MissingCredentialMakesNoRequest()
    {
        var handler = new Handler();
        var error = await Assert.ThrowsAsync<SourceBrowseException>(() => new IdentityCenterSourceCatalogClient(new Credentials { Missing = true }, new Factory(handler)).ReadAsync(Guid.NewGuid(), default));
        Assert.Equal("NotConfigured", error.Code); Assert.Null(handler.Uri);
    }
    [Fact] public async Task MappingTargetsUseSelectedSinkCredentialsAndRestoreAmbientNames()
    {
        using var outer = CredentialNameContext.Push("source-name", "other-sink");
        var credentials = new Credentials(); var handler = new Handler { Schema = new("People", [new("Email")], "") };
        var schema = await new IdentityCenterSourceCatalogClient(credentials, new Factory(handler)).MappingSchemaAsync(Guid.NewGuid(), "Identities", "selected-sink", default);
        Assert.Equal("selected-sink", credentials.Name); Assert.Equal("Email", Assert.Single(schema.Targets).Name);
        Assert.Equal("https://ic-api.example.test/api/source-catalog/mapping-targets?table=Identities", handler.Uri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, handler.Method); Assert.Equal("source-name", CredentialNameContext.Source); Assert.Equal("other-sink", CredentialNameContext.Sink);
    }
    [Fact] public async Task InvalidOrMissingTargetSchemaCannotBecomeAnEditableEmptySchema()
    {
        var handler = new Handler { Schema = new("Empty", [], "") };
        var client = new IdentityCenterSourceCatalogClient(new Credentials(), new Factory(handler));
        Assert.Equal("InvalidTargetSchema", (await Assert.ThrowsAsync<SourceBrowseException>(() => client.MappingSchemaAsync(Guid.NewGuid(), "Objects", null, default))).Code);
        handler.Status = HttpStatusCode.NotFound;
        var error = await Assert.ThrowsAsync<SourceBrowseException>(() => client.MappingSchemaAsync(Guid.NewGuid(), "Objects", null, default));
        Assert.Equal("TargetSchemaUnavailable", error.Code); Assert.DoesNotContain("PRIVATE", error.Message);
    }
    [Fact] public async Task CancelledTargetSchemaReadNeverSendsRequest()
    {
        using var cts = new CancellationTokenSource(); var handler = new Handler();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new IdentityCenterSourceCatalogClient(new Credentials { OnRead = cts.Cancel }, new Factory(handler)).MappingSchemaAsync(Guid.NewGuid(), "Objects", null, cts.Token));
        Assert.Null(handler.Uri);
    }
    [Fact] public async Task CancellationAfterCredentialReadPreventsHttpRequest()
    {
        using var cts = new CancellationTokenSource(); var handler = new Handler();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new IdentityCenterSourceCatalogClient(new Credentials { OnRead = cts.Cancel }, new Factory(handler)).ReadAsync(Guid.NewGuid(), cts.Token));
        Assert.Null(handler.Uri);
    }
    private sealed class Credentials : CredentialProtector
    {
        public Credentials() : base(null!, null!, null!) { }
        public Guid Id; public string? Name; public bool Missing; public Action? OnRead;
        public override Task<string?> RetrieveAsync(Guid id, string name)
        {
            Id = id; Name = name; OnRead?.Invoke();
            return Task.FromResult<string?>(Missing ? null : "{\"BaseUrl\":\"https://ic-api.example.test\",\"ApiKey\":\"fixture-only\"}");
        }
        public override Task StoreAsync(Guid id, string name, string plaintext) => throw new InvalidOperationException("Catalog cannot write credentials.");
    }
    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public string? Name;
        public HttpClient CreateClient(string name) { Name = name; return new HttpClient(handler, disposeHandler: false); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.OK;
        public SourceMappingSchema? Schema;
        public Uri? Uri; public HttpMethod? Method; public string? Key;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri; Method = request.Method; Key = request.Headers.GetValues("X-API-Key").Single();
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Status == HttpStatusCode.OK ? Schema != null ? JsonSerializer.Serialize(Schema) : JsonSerializer.Serialize(new SourceCatalog("IC Local", null, "https://ic.example.test", [])) : "PRIVATE response body") });
        }
    }
}
