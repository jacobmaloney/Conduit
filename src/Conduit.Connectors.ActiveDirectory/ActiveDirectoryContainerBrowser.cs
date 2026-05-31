using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.ActiveDirectory;

/// <summary>
/// Live AD container browser used by the wizard Scope step's "Browse" button to
/// pick a Base DN. Mirrors IdentityCenter's OU browser (DirectorySchemaService.
/// GetActiveDirectoryOrganizationalUnitsAsync): a OneLevel LDAP search for
/// organizationalUnit + container objects under a parent DN, with the directory's
/// defaultNamingContext used as the root when no parent is supplied.
///
/// Binds with the same path as <see cref="ActiveDirectorySource"/> —
/// System.DirectoryServices.Protocols simple bind, host:port from Tenant.Domain,
/// credentials from ConnectionCredentials ("ldap", {Username,Password}). No data
/// is cached or persisted; every call is a fresh live read against the source.
/// </summary>
public sealed class ActiveDirectoryContainerBrowser : IConnectorContainerBrowser
{
    private readonly Guid _tenantId;
    private readonly TenantRepository _tenantRepo;
    private readonly CredentialProtector _protector;
    private readonly ILogger _logger;

    public ActiveDirectoryContainerBrowser(
        Guid tenantId,
        TenantRepository tenantRepo,
        CredentialProtector protector,
        ILogger logger)
    {
        _tenantId = tenantId;
        _tenantRepo = tenantRepo;
        _protector = protector;
        _logger = logger;
    }

    public async Task<DirectoryBrowseResult> BrowseContainersAsync(
        string? parentDn,
        CancellationToken cancellationToken)
    {
        var tenant = await _tenantRepo.GetByIdAsync(_tenantId);
        if (tenant is null)
            return DirectoryBrowseResult.Fail("Tenant not found.");

        var creds = await ReadCredsAsync();
        if (creds is null)
            return DirectoryBrowseResult.Fail("No 'ldap' credential stored. Save credentials before browsing.");

        var (host, port) = ParseHostPort(tenant.Domain);

        // Bind + search run on the LDAP API which is synchronous; keep the UI
        // thread free by offloading to the thread pool (matches IC's Task.Run).
        return await Task.Run(() => BrowseSync(host, port, creds, parentDn, cancellationToken), cancellationToken);
    }

    private DirectoryBrowseResult BrowseSync(
        string host, int port, AdCredentials creds, string? parentDn, CancellationToken cancellationToken)
    {
        try
        {
            using var connection = new LdapConnection(new LdapDirectoryIdentifier(host, port))
            {
                AuthType = AuthType.Basic
            };
            connection.SessionOptions.ProtocolVersion = 3;
            connection.Credential = new NetworkCredential(creds.Username, creds.Password);
            connection.Bind();

            // Resolve the search base. Null/blank parent => directory root via RootDSE.
            var searchBase = parentDn;
            if (string.IsNullOrWhiteSpace(searchBase))
            {
                var rootReq = new SearchRequest("", "(objectClass=*)", SearchScope.Base,
                    new[] { "defaultNamingContext" });
                var rootResp = (SearchResponse)connection.SendRequest(rootReq);
                searchBase = rootResp.Entries.Count > 0 && rootResp.Entries[0].Attributes.Contains("defaultNamingContext")
                    ? rootResp.Entries[0].Attributes["defaultNamingContext"][0]?.ToString()
                    : null;
                if (string.IsNullOrWhiteSpace(searchBase))
                    return DirectoryBrowseResult.Fail("Could not determine the directory's defaultNamingContext.");
            }

            // OneLevel: immediate children only (lazy drill-down). Match IC: OUs + containers.
            var request = new SearchRequest(
                searchBase,
                "(|(objectClass=organizationalUnit)(objectClass=container))",
                SearchScope.OneLevel,
                new[] { "distinguishedName", "ou", "cn", "name", "description" });
            request.Controls.Add(new PageResultRequestControl(1000));

            var nodes = new List<DirectoryContainerNode>();
            SearchResponse response;
            try
            {
                response = (SearchResponse)connection.SendRequest(request);
            }
            catch (DirectoryOperationException ex)
            {
                _logger.LogWarning(ex, "AD container browse search failed under {Base} for tenant {TenantId}", searchBase, _tenantId);
                return DirectoryBrowseResult.Fail($"Search under '{searchBase}' failed: {ex.Message}");
            }

            foreach (SearchResultEntry entry in response.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dn = entry.DistinguishedName;
                if (string.IsNullOrEmpty(dn)) continue;

                var name = FirstValue(entry, "ou") ?? FirstValue(entry, "name") ?? FirstValue(entry, "cn") ?? dn;
                var description = FirstValue(entry, "description");

                nodes.Add(new DirectoryContainerNode
                {
                    Name = name,
                    DistinguishedName = dn,
                    Description = description,
                    HasChildren = true // resolved on expand, same as IC
                });
            }

            nodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return DirectoryBrowseResult.Ok(nodes, searchBase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AD container browse failed for tenant {TenantId} (parent {Parent})", _tenantId, parentDn ?? "root");
            return DirectoryBrowseResult.Fail(ex.Message);
        }
    }

    private static string? FirstValue(SearchResultEntry entry, string attr)
        => entry.Attributes.Contains(attr) && entry.Attributes[attr].Count > 0
            ? entry.Attributes[attr][0]?.ToString()
            : null;

    // ─── credential + host helpers (mirror ActiveDirectorySource) ────────────

    private sealed record AdCredentials(string Username, string Password);

    private async Task<AdCredentials?> ReadCredsAsync()
    {
        var name = CredentialNameContext.Resolve("ldap", CredentialSide.Source);
        var raw = await _protector.RetrieveAsync(_tenantId, name);
        if (string.IsNullOrEmpty(raw))
        {
            var sinkName = CredentialNameContext.Resolve("ldap", CredentialSide.Sink);
            if (!string.Equals(sinkName, name, StringComparison.OrdinalIgnoreCase))
                raw = await _protector.RetrieveAsync(_tenantId, sinkName);
        }
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var u = doc.RootElement.TryGetProperty("Username", out var uEl) ? uEl.GetString() : null;
            var p = doc.RootElement.TryGetProperty("Password", out var pEl) ? pEl.GetString() : null;
            if (string.IsNullOrEmpty(u) || p is null) return null;
            return new AdCredentials(u, p);
        }
        catch
        {
            return null;
        }
    }

    private static (string Host, int Port) ParseHostPort(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            throw new InvalidOperationException("Tenant.Domain is required for AD connections (host or host:port).");
        var parts = domain.Split(':');
        if (parts.Length == 1) return (parts[0], 389);
        if (int.TryParse(parts[1], out var p)) return (parts[0], p);
        return (parts[0], 389);
    }
}
