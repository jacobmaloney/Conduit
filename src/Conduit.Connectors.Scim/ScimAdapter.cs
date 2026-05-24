using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.Scim;

/// <summary>
/// SCIM 2.0 adapter — source AND sink against any RFC 7644 endpoint. Uses
/// Bearer-token auth. Credentials: { BaseUrl, BearerToken, UserEndpoint?,
/// GroupEndpoint? } under "scim". Endpoints default to /Users and /Groups.
/// </summary>
public sealed class ScimAdapter : IConnectorAdapter
{
    public string SystemType => "Scim";
    public string DisplayName => "SCIM 2.0";
    public bool SupportsSource => true;
    public bool SupportsSink => true;

    /// <summary>
    /// SCIM 2.0 /Bulk endpoint per RFC 7644 §3.7 supports multi-operation
    /// batches; modern servers allow 50–1000 per request. Conduit picks 100 as
    /// a safe default. Source supports incremental via meta.lastModified filter.
    /// </summary>
    public ConnectorCapabilities Capabilities { get; } = new()
    {
        SupportsBulk = true,
        MaxBatchSize = 100,
        SupportsIncremental = true,
        // Phase 8: AssignManager via PATCH on enterprise extension manager.value.
        // PersonMatch / PersonCreate belong to the governance store (IC), not the
        // SCIM directory. AssignGroupOwner is not a SCIM concept.
        SupportsAssignManager = true
    };

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes { get; } = new[]
    {
        new CredentialTypeInfo
        {
            Name = "scim",
            DisplayName = "SCIM 2.0",
            Description = "Bearer-token SCIM endpoint (RFC 7644).",
            Fields = new[]
            {
                new CredentialFieldSpec { Key = "BaseUrl", Label = "Base URL", Placeholder = "https://scim.example.com/v2", IsRequired = true },
                new CredentialFieldSpec { Key = "BearerToken", Label = "Bearer Token", IsRequired = true, IsSecret = true },
                new CredentialFieldSpec { Key = "UserEndpoint", Label = "User Endpoint", Placeholder = "/Users" },
                new CredentialFieldSpec { Key = "GroupEndpoint", Label = "Group Endpoint", Placeholder = "/Groups" },
            }
        }
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly CredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public ScimAdapter(IHttpClientFactory httpFactory, CredentialProtector protector, ILoggerFactory loggerFactory)
    {
        _httpFactory = httpFactory;
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new ScimSource(tenantId, _httpFactory, _protector, _loggerFactory.CreateLogger<ScimSource>());

    public IConnectorSink? CreateSink(Guid tenantId) =>
        new ScimSink(tenantId, _httpFactory, _protector, _loggerFactory.CreateLogger<ScimSink>());
}

internal sealed record ScimCredentials(string BaseUrl, string BearerToken, string UserEndpoint, string GroupEndpoint);

internal static class ScimCredentialReader
{
    public const string CredentialName = "scim";

    public static async Task<ScimCredentials?> ReadAsync(CredentialProtector p, Guid tenantId)
    {
        // Phase 2: honor per-project credential-name override (source side first,
        // then sink — same name space, sinks fall through if source override is set).
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
            var tok = doc.RootElement.TryGetProperty("BearerToken", out var tEl) ? tEl.GetString() : null;
            var ue = doc.RootElement.TryGetProperty("UserEndpoint", out var ueEl) ? ueEl.GetString() : null;
            var ge = doc.RootElement.TryGetProperty("GroupEndpoint", out var geEl) ? geEl.GetString() : null;
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(tok)) return null;
            return new ScimCredentials(
                url!.TrimEnd('/'),
                tok!,
                string.IsNullOrEmpty(ue) ? "/Users" : ue!,
                string.IsNullOrEmpty(ge) ? "/Groups" : ge!);
        }
        catch { return null; }
    }

    public static HttpClient BuildClient(IHttpClientFactory factory, ScimCredentials creds)
    {
        var client = factory.CreateClient("ScimConnector");
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/scim+json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", creds.BearerToken);
        return client;
    }
}
