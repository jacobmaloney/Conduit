using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.GoogleWorkspace;

/// <summary>
/// Google Workspace adapter — source AND sink via Admin SDK Directory API.
/// Auth: service-account JSON + domain-wide delegation to an admin user.
/// Credentials stored under "google" as { ServiceAccountJson, AdminEmail, Domain }.
/// </summary>
public sealed class GoogleWorkspaceAdapter : IConnectorAdapter
{
    public string SystemType => "GoogleWorkspace";
    public string DisplayName => "Google Workspace";
    public bool SupportsSource => true;
    public bool SupportsSink => true;

    /// <summary>
    /// Phase 8: AssignManager via Admin SDK relations[]. PersonMatch / PersonCreate
    /// belong to the governance store (IC), not the Google directory. AssignGroupOwner
    /// is doable via Members.Update role=OWNER but defers until a customer asks —
    /// Google's "group owner" semantics differ from M365 group owners.
    /// </summary>
    public ConnectorCapabilities Capabilities { get; } = new()
    {
        SupportsAssignManager = true
    };

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes { get; } = new[]
    {
        new CredentialTypeInfo
        {
            Name = "google",
            DisplayName = "Google Workspace Service Account",
            Description = "Service-account JSON + domain-wide delegation impersonation target.",
            Fields = new[]
            {
                new CredentialFieldSpec { Key = "ServiceAccountJson", Label = "Service Account JSON", IsRequired = true, IsSecret = true, IsMultiline = true, Placeholder = "{\"type\":\"service_account\",...}" },
                new CredentialFieldSpec { Key = "AdminEmail", Label = "Admin Email (impersonation target)", IsRequired = true, Placeholder = "admin@your-domain.com" },
                new CredentialFieldSpec { Key = "Domain", Label = "Domain (optional, leave blank for my_customer)" },
            }
        }
    };

    private readonly CredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public GoogleWorkspaceAdapter(CredentialProtector protector, ILoggerFactory loggerFactory)
    {
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new GoogleWorkspaceSource(tenantId, _protector, _loggerFactory.CreateLogger<GoogleWorkspaceSource>());

    public IConnectorSink? CreateSink(Guid tenantId) =>
        new GoogleWorkspaceSink(tenantId, _protector, _loggerFactory.CreateLogger<GoogleWorkspaceSink>());
}

internal sealed record GoogleCredentials(string ServiceAccountJson, string AdminEmail, string? Domain);

internal static class GoogleCredentialReader
{
    public const string CredentialName = "google";

    public static async Task<GoogleCredentials?> ReadAsync(CredentialProtector p, Guid tenantId)
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
            var j = doc.RootElement.TryGetProperty("ServiceAccountJson", out var jEl) ? jEl.GetString() : null;
            var a = doc.RootElement.TryGetProperty("AdminEmail", out var aEl) ? aEl.GetString() : null;
            var d = doc.RootElement.TryGetProperty("Domain", out var dEl) ? dEl.GetString() : null;
            if (string.IsNullOrEmpty(j) || string.IsNullOrEmpty(a)) return null;
            return new GoogleCredentials(j!, a!, d);
        }
        catch { return null; }
    }

    public static async Task<DirectoryService> CreateServiceAsync(GoogleCredentials creds, bool readOnly)
    {
        var scopes = readOnly
            ? new[]
            {
                DirectoryService.Scope.AdminDirectoryUserReadonly,
                DirectoryService.Scope.AdminDirectoryGroupReadonly,
                DirectoryService.Scope.AdminDirectoryGroupMemberReadonly,
                DirectoryService.Scope.AdminDirectoryOrgunitReadonly
            }
            : new[]
            {
                DirectoryService.Scope.AdminDirectoryUser,
                DirectoryService.Scope.AdminDirectoryGroup,
                DirectoryService.Scope.AdminDirectoryGroupMember,
                DirectoryService.Scope.AdminDirectoryOrgunit
            };
        var credential = GoogleCredential.FromJson(creds.ServiceAccountJson)
            .CreateScoped(scopes)
            .CreateWithUser(creds.AdminEmail);
        await ((ICredential)credential.UnderlyingCredential).GetAccessTokenForRequestAsync();
        return new DirectoryService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Conduit"
        });
    }
}
