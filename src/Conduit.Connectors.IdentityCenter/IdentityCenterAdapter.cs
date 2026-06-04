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
                // V22: the IC table (Objects | Identities) is NO LONGER a connection
                // credential field. It moved onto the Sync Project's source/sink
                // endpoints (SyncProject.SourceTable / SinkTable) so ONE IdentityCenter
                // connection can be source=Identities AND sink=Objects in a single
                // project (IC/Identities → IC/Objects). The connector reads it per side
                // from IdentityCenterTableContext, which the orchestrator stamps from
                // the project. Picked once in the New/Edit Sync Project wizard.
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

/// <summary>Which IC table an endpoint targets. Objects = directory accounts
/// (/api/objects/*); Identities = people golden records (/api/identities/*).</summary>
internal enum IcTable { Objects, Identities }

internal sealed record IdentityCenterCredentials(string BaseUrl, string ApiKey, IcTable Table);

internal static class IdentityCenterCredentialReader
{
    public const string CredentialName = "identitycenter";

    /// <summary>
    /// Reads the IC credential for the GIVEN side. The credential blob now carries
    /// ONLY BaseUrl + ApiKey; the table (Objects | Identities) is resolved per side
    /// from <see cref="IdentityCenterTableContext"/>, which the orchestrator stamps
    /// from the project's SourceTable / SinkTable (V22). This is what lets one IC
    /// connection source from Identities AND sink into Objects in one project.
    /// Falls back to the other side's blob only when the requested side has none
    /// stored (single-credential installs) — the TABLE always tracks the requested
    /// side regardless of which blob supplied the URL/key.
    /// </summary>
    public static async Task<IdentityCenterCredentials?> ReadAsync(
        CredentialProtector p, Guid tenantId, CredentialSide side = CredentialSide.Source)
    {
        var name = CredentialNameContext.Resolve(CredentialName, side);
        var raw = await p.RetrieveAsync(tenantId, name);
        if (string.IsNullOrEmpty(raw))
        {
            var other = side == CredentialSide.Source ? CredentialSide.Sink : CredentialSide.Source;
            var otherName = CredentialNameContext.Resolve(CredentialName, other);
            if (!string.Equals(otherName, name, StringComparison.OrdinalIgnoreCase))
                raw = await p.RetrieveAsync(tenantId, otherName);
        }
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var url = doc.RootElement.TryGetProperty("BaseUrl", out var uEl) ? uEl.GetString() : null;
            var key = doc.RootElement.TryGetProperty("ApiKey",  out var kEl) ? kEl.GetString() : null;
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key)) return null;
            // V22: table comes from the per-side project endpoint (ambient context the
            // orchestrator stamps from SyncProject.SourceTable / SinkTable), NOT the
            // credential blob. Explicit "Identities" → Identities; unset / unknown /
            // "Objects" → Objects (back-compat default).
            var tableKey = IdentityCenterTableContext.Resolve(side);
            var table = string.Equals(tableKey, "Identities", StringComparison.OrdinalIgnoreCase)
                ? IcTable.Identities
                : IcTable.Objects;
            return new IdentityCenterCredentials(url!.TrimEnd('/'), key!, table);
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
