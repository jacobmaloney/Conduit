using System.Net;
using System.Text.Json;
using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Security;
using Microsoft.Extensions.Configuration;

namespace Conduit.Connectors.IdentityCenter.Tests;

/// <summary>
/// Captures the last HTTP request (method + URI + body) the sink sent and returns a
/// canned 200 with an empty results array. No live IC — the tests assert on the
/// REQUEST the sink built, not on any response mapping.
/// </summary>
internal sealed class CapturingHandler : HttpMessageHandler
{
    public HttpMethod? LastMethod { get; private set; }
    public Uri? LastUri { get; private set; }
    public string? LastBody { get; private set; }
    private readonly string _responseJson;

    public CapturingHandler(string responseJson = "{\"results\":[]}") => _responseJson = responseJson;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastMethod = request.Method;
        LastUri = request.RequestUri;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_responseJson, System.Text.Encoding.UTF8, "application/json")
        };
    }
}

internal sealed class SingleClientHttpFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public SingleClientHttpFactory(HttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

/// <summary>
/// Returns a fixed credential blob for RetrieveAsync so the sink can build a client
/// without a DB. StoreAsync is unused by the sink tests.
/// </summary>
internal sealed class StubCredentialProtector : CredentialProtector
{
    private readonly string _blob;

    public StubCredentialProtector(string baseUrl, string apiKey) : base(
        new ConfigurationBuilder().Build(),
        new ConnectionCredentialRepository(new DatabaseConfig()),
        new CredentialKeyringRepository(new DatabaseConfig()))
    {
        _blob = JsonSerializer.Serialize(new { BaseUrl = baseUrl, ApiKey = apiKey });
    }

    public override Task<string?> RetrieveAsync(Guid tenantId, string credentialName) =>
        Task.FromResult<string?>(_blob);
}

/// <summary>
/// In-memory <see cref="ConnectionCredentialRepository"/> — captures the encrypted row
/// StoreAsync persists and serves it back to RetrieveAsync, so a REAL CredentialProtector
/// can be exercised (real AES-GCM) with no SQL. Relies on the two repo methods being virtual.
/// </summary>
internal sealed class InMemoryConnectionCredentialRepository : ConnectionCredentialRepository
{
    private readonly Dictionary<(Guid, string), CredentialRow> _rows = new();

    public InMemoryConnectionCredentialRepository() : base(new DatabaseConfig()) { }

    public CredentialRow? LastStored { get; private set; }

    public override Task UpsertAsync(CredentialRow row)
    {
        _rows[(row.TenantId, row.CredentialName)] = row;
        LastStored = row;
        return Task.CompletedTask;
    }

    public override Task<CredentialRow?> GetAsync(Guid tenantId, string credentialName) =>
        Task.FromResult(_rows.TryGetValue((tenantId, credentialName), out var r) ? r : null);
}

internal static class JsonCI
{
    /// <summary>Case-insensitive property lookup (the sink serializes with default PascalCase).</summary>
    public static JsonElement Prop(JsonElement el, string name)
    {
        foreach (var p in el.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value;
        throw new KeyNotFoundException($"No property '{name}' in {el}");
    }
}
