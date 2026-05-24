using System;
using System.Collections.Generic;
using System.Net.Http;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.Okta;

/// <summary>
/// Okta adapter — source AND sink. Credentials: { OrgUrl, ApiToken } under
/// CredentialName="okta". Uses SSWS-token auth on the Okta REST v1 API.
/// </summary>
public sealed class OktaAdapter : IConnectorAdapter
{
    public string SystemType => "Okta";
    public string DisplayName => "Okta";
    public bool SupportsSource => true;
    public bool SupportsSink => true;

    /// <summary>
    /// Okta's REST API has no native /Bulk — per-record only — but supports
    /// incremental via the lastUpdated filter.
    /// </summary>
    public ConnectorCapabilities Capabilities { get; } = new()
    {
        SupportsBulk = false,
        MaxBatchSize = 1,
        SupportsIncremental = true,
        // Phase 8: AssignManager via profile.managerId PATCH. PersonMatch / PersonCreate
        // belong to the governance store (IC), not the Okta directory. AssignGroupOwner
        // is not modeled in Okta — groups don't have a single-owner concept.
        SupportsAssignManager = true
    };

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes { get; } = new[]
    {
        new CredentialTypeInfo
        {
            Name = "okta",
            DisplayName = "Okta API Token",
            Description = "Okta org URL + SSWS API token.",
            Fields = new[]
            {
                new CredentialFieldSpec { Key = "OrgUrl", Label = "Org URL", Placeholder = "https://your-org.okta.com", IsRequired = true },
                new CredentialFieldSpec { Key = "ApiToken", Label = "API Token", IsRequired = true, IsSecret = true },
            }
        }
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly CredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public OktaAdapter(IHttpClientFactory httpFactory, CredentialProtector protector, ILoggerFactory loggerFactory)
    {
        _httpFactory = httpFactory;
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new OktaSource(tenantId, _httpFactory, _protector, _loggerFactory.CreateLogger<OktaSource>());

    public IConnectorSink? CreateSink(Guid tenantId) =>
        new OktaSink(tenantId, _httpFactory, _protector, _loggerFactory.CreateLogger<OktaSink>());
}

internal sealed record OktaCredentials(string OrgUrl, string ApiToken);

internal static class OktaCredentialReader
{
    public const string CredentialName = "okta";

    public static async System.Threading.Tasks.Task<OktaCredentials?> ReadAsync(CredentialProtector p, Guid tenantId)
    {
        var name = CredentialNameContext.Resolve(CredentialName, CredentialSide.Source);
        var raw = await p.RetrieveAsync(tenantId, name);
        if (string.IsNullOrEmpty(raw))
        {
            var sinkName = CredentialNameContext.Resolve(CredentialName, CredentialSide.Sink);
            if (!string.Equals(sinkName, name, System.StringComparison.OrdinalIgnoreCase))
                raw = await p.RetrieveAsync(tenantId, sinkName);
        }
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var u = doc.RootElement.TryGetProperty("OrgUrl", out var uEl) ? uEl.GetString() : null;
            var t = doc.RootElement.TryGetProperty("ApiToken", out var tEl) ? tEl.GetString() : null;
            if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(t)) return null;
            return new OktaCredentials(u!.TrimEnd('/'), t!);
        }
        catch { return null; }
    }

    public static HttpClient BuildClient(IHttpClientFactory factory, OktaCredentials creds)
    {
        var client = factory.CreateClient("OktaConnector");
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("SSWS", creds.ApiToken);
        return client;
    }
}
