using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;

namespace Conduit.Connectors.SharePoint;

/// <summary>
/// SharePoint Online / Teams adapter — source-only (Phase 1.5 P2 scope). Uses
/// Microsoft Graph via ClientSecretCredential. Credentials shape mirrors
/// EntraID: { TenantId, ClientId, ClientSecret }. Object classes: "Site",
/// "Team". Sink for SharePoint is intentionally not shipped — write-back is
/// usually orchestrated through Graph Sites API differently per scenario.
/// </summary>
public sealed class SharePointAdapter : IConnectorAdapter
{
    public string SystemType => "SharePoint";
    public string DisplayName => "SharePoint Online";
    public bool SupportsSource => true;
    public bool SupportsSink => false;

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes { get; } = new[]
    {
        new CredentialTypeInfo
        {
            Name = "sharepoint",
            DisplayName = "SharePoint App Registration",
            Description = "Same shape as Entra — Azure AD app reg with Sites.Read.All / Group.Read.All.",
            Fields = new[]
            {
                new CredentialFieldSpec { Key = "TenantId", Label = "Tenant ID", IsRequired = true, Placeholder = "00000000-0000-0000-0000-000000000000" },
                new CredentialFieldSpec { Key = "ClientId", Label = "Client ID (Application ID)", IsRequired = true },
                new CredentialFieldSpec { Key = "ClientSecret", Label = "Client Secret", IsRequired = true, IsSecret = true },
            }
        }
    };

    private readonly CredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public SharePointAdapter(CredentialProtector protector, ILoggerFactory loggerFactory)
    {
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new SharePointSource(tenantId, _protector, _loggerFactory.CreateLogger<SharePointSource>());

    public IConnectorSink? CreateSink(Guid tenantId) => null;
}

internal sealed record SharePointCredentials(string TenantId, string ClientId, string ClientSecret);

internal static class SharePointCredentialReader
{
    public const string CredentialName = "sharepoint";

    public static async Task<SharePointCredentials?> ReadAsync(CredentialProtector p, Guid tenantId)
    {
        // Phase 3: honor per-project credential-name override (source first, sink fallback).
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
            var t = doc.RootElement.TryGetProperty("TenantId", out var tEl) ? tEl.GetString() : null;
            var c = doc.RootElement.TryGetProperty("ClientId", out var cEl) ? cEl.GetString() : null;
            var s = doc.RootElement.TryGetProperty("ClientSecret", out var sEl) ? sEl.GetString() : null;
            if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(c) || string.IsNullOrEmpty(s)) return null;
            return new SharePointCredentials(t!, c!, s!);
        }
        catch { return null; }
    }

    public static GraphServiceClient CreateClient(SharePointCredentials creds)
    {
        var credential = new ClientSecretCredential(creds.TenantId, creds.ClientId, creds.ClientSecret);
        return new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
    }
}

public sealed class SharePointSource : IConnectorSource
{
    private readonly Guid _tenantId;
    private readonly CredentialProtector _protector;
    private readonly ILogger<SharePointSource> _logger;

    public SharePointSource(Guid tenantId, CredentialProtector protector, ILogger<SharePointSource> logger)
    {
        _tenantId = tenantId;
        _protector = protector;
        _logger = logger;
    }

    public async IAsyncEnumerable<ConnectorObject> ReadAsync(
        string objectClass,
        SyncProjectScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var creds = await SharePointCredentialReader.ReadAsync(_protector, _tenantId)
            ?? throw new InvalidOperationException($"No 'sharepoint' credential for tenant {_tenantId}.");
        var client = SharePointCredentialReader.CreateClient(creds);
        var emitted = 0;

        if (string.Equals(objectClass, "Team", StringComparison.OrdinalIgnoreCase))
        {
            var teams = await client.Teams.GetAsync(req => req.QueryParameters.Top = 100, cancellationToken);
            while (teams?.Value != null)
            {
                foreach (var t in teams.Value)
                {
                    if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                    var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["objectClass"] = "team",
                        ["id"] = t.Id,
                        ["objectGuid"] = t.Id,
                        ["displayName"] = t.DisplayName,
                        ["cn"] = t.DisplayName,
                        ["description"] = t.Description,
                        ["webUrl"] = t.WebUrl
                    };
                    emitted++;
                    yield return new ConnectorObject
                    {
                        SourceId = t.Id ?? string.Empty,
                        ObjectClass = "Team",
                        Attributes = attrs
                    };
                }
                if (string.IsNullOrEmpty(teams.OdataNextLink)) break;
                teams = await client.Teams.WithUrl(teams.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
            }
            yield break;
        }

        // Default: Site
        var sites = await client.Sites.GetAsync(req => req.QueryParameters.Top = 100, cancellationToken);
        while (sites?.Value != null)
        {
            foreach (var s in sites.Value)
            {
                if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["objectClass"] = "site",
                    ["id"] = s.Id,
                    ["objectGuid"] = s.Id,
                    ["displayName"] = s.DisplayName,
                    ["cn"] = s.DisplayName,
                    ["description"] = s.Description,
                    ["webUrl"] = s.WebUrl,
                    ["name"] = s.Name,
                    ["whenCreated"] = s.CreatedDateTime?.ToString("o")
                };
                emitted++;
                yield return new ConnectorObject
                {
                    SourceId = s.Id ?? string.Empty,
                    ObjectClass = "Site",
                    Attributes = attrs
                };
            }
            if (string.IsNullOrEmpty(sites.OdataNextLink)) break;
            sites = await client.Sites.WithUrl(sites.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var creds = await SharePointCredentialReader.ReadAsync(_protector, _tenantId);
            if (creds is null) return new ConnectorTestResult { IsSuccessful = false, Message = "No 'sharepoint' credential stored." };
            var client = SharePointCredentialReader.CreateClient(creds);
            var root = await client.Sites["root"].GetAsync(cancellationToken: cancellationToken);
            return new ConnectorTestResult { IsSuccessful = true, Message = $"Root site: {root?.WebUrl ?? "(unknown)"}." };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }
}
