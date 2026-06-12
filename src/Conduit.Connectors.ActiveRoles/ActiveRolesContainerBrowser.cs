using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.ActiveRoles;

/// <summary>
/// Live OU/container browser for the wizard Scope step's Base DN picker — the
/// Active Roles parity of <c>ActiveDirectoryContainerBrowser</c>. The operator
/// browsing a Base DN does NOT need policy/VA resolution (it's a directory shape
/// question), so this uses the FAST raw-LDAP path against the DC (adHost) exactly
/// like the connector's Phase-2 fast READ — NOT the EDMS:// AR service. That keeps
/// browsing in the millisecond range and means it works whenever the fast read does.
///
/// Credentials + host come through <see cref="IArsConnectionResolver"/> (the same
/// "ars" blob the source/sink resolve), so the per-run
/// <see cref="CredentialNameContext"/> override the modal pushes is honored. The
/// DC host is <c>adHost</c> (falling back to the AR service host, which is normally
/// domain-joined and serves LDAP). A OneLevel search for organizationalUnit +
/// container objects gives lazy drill-down identical to the AD browser.
/// </summary>
public sealed class ActiveRolesContainerBrowser : IConnectorContainerBrowser
{
    private readonly IArsConnectionResolver _resolver;
    private readonly ILogger _logger;

    public ActiveRolesContainerBrowser(IArsConnectionResolver resolver, ILogger logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    public async Task<DirectoryBrowseResult> BrowseContainersAsync(
        string? parentDn,
        CancellationToken cancellationToken)
    {
        var settings = await _resolver.ResolveAsync(CredentialSide.Source, cancellationToken);
        if (settings is null)
            return DirectoryBrowseResult.Fail(
                "No 'ars' credential resolved. Save Active Roles bind credentials before browsing.");

        var hostHint = settings.AdHost is { Length: > 0 } ? settings.AdHost : settings.ArsServiceHost;
        if (string.IsNullOrWhiteSpace(hostHint))
            return DirectoryBrowseResult.Fail(
                "Active Roles browse needs a domain-controller host. Set 'AD host (fast read)' (or the AR service host) on the credential.");

        var (host, port) = ParseHostPort(hostHint);

        // Bind + search run on the synchronous LDAP API; offload to the thread pool
        // so the UI thread stays free (matches the AD browser).
        return await Task.Run(
            () => BrowseSync(host, port, settings.BindUser, settings.BindPassword, parentDn, cancellationToken),
            cancellationToken);
    }

    private DirectoryBrowseResult BrowseSync(
        string host, int port, string bindUser, string bindPassword, string? parentDn, CancellationToken cancellationToken)
    {
        try
        {
            using var connection = CreateBoundConnection(host, port, bindUser, bindPassword);

            // Null/blank parent => directory root via RootDSE defaultNamingContext.
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

            // OneLevel: immediate children only (lazy drill-down). OUs + containers.
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
                _logger.LogWarning(ex, "ARS container browse search failed under {Base}", searchBase);
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
                    HasChildren = true // resolved on expand
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
            _logger.LogError(ex, "ARS container browse failed (parent {Parent})", parentDn ?? "root");
            return DirectoryBrowseResult.Fail(ex.Message);
        }
    }

    private static string? FirstValue(SearchResultEntry entry, string attr)
        => entry.Attributes.Contains(attr) && entry.Attributes[attr].Count > 0
            ? entry.Attributes[attr][0]?.ToString()
            : null;

    /// <summary>
    /// Negotiate bind against the raw DC — MIRRORS <see cref="FastAdReader"/>'s
    /// bind. Hardened DCs that enforce LDAP signing reject a simple bind; Negotiate
    /// (GSS-SPNEGO → Kerberos/NTLM) is required. DOMAIN\user is split the way SSPI
    /// expects; UPN / bare names pass through unchanged.
    /// </summary>
    private static LdapConnection CreateBoundConnection(string host, int port, string bindUser, string bindPassword)
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(host, port))
        {
            AuthType = AuthType.Negotiate
        };
        connection.SessionOptions.ProtocolVersion = 3;
        // Single-domain perf fix (commit 20cf600): never chase referrals.
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

        NetworkCredential netCred;
        if (bindUser.Contains('\\'))
        {
            var parts = bindUser.Split('\\', 2);
            netCred = new NetworkCredential(parts[1], bindPassword) { Domain = parts[0] };
        }
        else
        {
            netCred = new NetworkCredential(bindUser, bindPassword);
        }
        connection.Credential = netCred;
        connection.Bind();
        return connection;
    }

    /// <summary>Split "host" / "host:port" into (host, port). Fast read targets the raw DC → default 389.</summary>
    private static (string Host, int Port) ParseHostPort(string hostHint)
    {
        var parts = hostHint.Split(':');
        if (parts.Length == 1) return (parts[0], 389);
        if (int.TryParse(parts[1], out var p)) return (parts[0], p);
        return (parts[0], 389);
    }
}
