using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.IdentityCenter;

/// <summary>
/// IdentityCenter adapter. Source + sink — IC exposes both a paged GET
/// (<c>/api/objects/query</c>) and a batched POST (<c>/api/objects/bulk</c>),
/// so a tenant of this type can sit on either side of a Sync Project.
/// </summary>
/// <remarks>
/// Authentication is IC's standard <c>X-API-Key</c> header. The IC API does
/// NOT echo Conduit's bulk writes back out to AD / Entra — that's deliberate
/// and lives in <c>ObjectsController.BulkUpsert</c>'s docs.
/// </remarks>
public sealed class IdentityCenterAdapter : IConnectorAdapter
{
    public string SystemType => "IdentityCenter";
    public string DisplayName => "IdentityCenter";
    public bool SupportsSource => true;
    public bool SupportsSink => true;

    public ConnectorCapabilities Capabilities { get; } = new()
    {
        SupportsBulk = true,
        MaxBatchSize = 500,
        SupportsIncremental = true,
        // Phase 7: IC is the canonical sink for person-aware workflow steps.
        SupportsPersonMatch = true,
        SupportsPersonCreate = true,
        SupportsAssignManager = true,
        SupportsAssignGroupOwner = true
    };

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes { get; } = new[]
    {
        new CredentialTypeInfo
        {
            Name = "identitycenter",
            DisplayName = "IdentityCenter API",
            Description = "IdentityCenter base URL + admin-scoped API key.",
            Fields = new[]
            {
                new CredentialFieldSpec { Key = "BaseUrl", Label = "Base URL", Placeholder = "https://identitycenter.local:7048", IsRequired = true },
                new CredentialFieldSpec { Key = "ApiKey",  Label = "API Key",  IsRequired = true, IsSecret = true },
            }
        }
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly CredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public IdentityCenterAdapter(IHttpClientFactory httpFactory, CredentialProtector protector, ILoggerFactory loggerFactory)
    {
        _httpFactory = httpFactory;
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new IdentityCenterSource(tenantId, _httpFactory, _protector, _loggerFactory.CreateLogger<IdentityCenterSource>());

    public IConnectorSink? CreateSink(Guid tenantId) =>
        new IdentityCenterSink(tenantId, _httpFactory, _protector, _loggerFactory.CreateLogger<IdentityCenterSink>());
}

internal sealed record IdentityCenterCredentials(string BaseUrl, string ApiKey);

internal static class IdentityCenterCredentialReader
{
    public const string CredentialName = "identitycenter";

    public static async Task<IdentityCenterCredentials?> ReadAsync(CredentialProtector p, Guid tenantId)
    {
        var name = CredentialNameContext.Resolve(CredentialName, CredentialSide.Source);
        var raw = await p.RetrieveAsync(tenantId, name);
        if (string.IsNullOrEmpty(raw))
        {
            var sinkName = CredentialNameContext.Resolve(CredentialName, CredentialSide.Sink);
            if (!string.Equals(sinkName, name, StringComparison.OrdinalIgnoreCase))
                raw = await p.RetrieveAsync(tenantId, sinkName);
        }
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var url = doc.RootElement.TryGetProperty("BaseUrl", out var uEl) ? uEl.GetString() : null;
            var key = doc.RootElement.TryGetProperty("ApiKey",  out var kEl) ? kEl.GetString() : null;
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key)) return null;
            return new IdentityCenterCredentials(url!.TrimEnd('/'), key!);
        }
        catch { return null; }
    }

    public static HttpClient BuildClient(IHttpClientFactory factory, IdentityCenterCredentials creds)
    {
        var client = factory.CreateClient("IdentityCenterConnector");
        client.Timeout = TimeSpan.FromSeconds(120);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // IC's ApiKeyAuthenticationHandler reads X-API-Key.
        client.DefaultRequestHeaders.Remove("X-API-Key");
        client.DefaultRequestHeaders.Add("X-API-Key", creds.ApiKey);
        return client;
    }
}
