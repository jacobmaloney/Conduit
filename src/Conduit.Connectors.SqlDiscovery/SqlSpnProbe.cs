using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit.Connectors.SqlDiscovery;

/// <summary>One SQL host as seen by an AD SPN sweep.</summary>
public sealed record SpnHost(string HostKey, string? ObjectGuid, IReadOnlyList<string> Spns);

/// <summary>
/// Standalone, read-only enumeration of SQL Server hosts from Active Directory by
/// their <c>MSSQLSvc/*</c> service principal names. SQL registers this SPN when an
/// instance starts, so the set of hosts returned here IS the set of running SQL
/// instances the directory knows about — which is why a diff of two sweeps detects
/// a spin-up (or a retirement) without touching a single SQL Server or needing any
/// SQL credential (AD read is enough).
///
/// This is the cheap signal behind <see cref="Conduit.Web"/>'s SPN watcher. It is a
/// deliberate, compact sibling of the SPN sweep inside <c>SqlDiscoverySource</c>
/// (which additionally interrogates each instance); kept separate so the watcher
/// can poll AD frequently without dragging in the full discovery pipeline. Same
/// hard-won LDAP hygiene: Negotiate bind, NO referral chasing (chasing turned a
/// seconds-long read into minutes — commit 20cf600), paged, attribute-projected.
/// </summary>
public static class SqlSpnProbe
{
    private const string SpnFilter = "(&(objectCategory=computer)(servicePrincipalName=MSSQLSvc/*))";

    /// <summary>
    /// Binds to <paramref name="host"/>:<paramref name="port"/> as <paramref name="username"/>
    /// and returns every computer object carrying an MSSQLSvc SPN. The blocking
    /// LDAP work runs on the thread pool so callers can await it.
    /// </summary>
    public static Task<IReadOnlyList<SpnHost>> EnumerateAsync(
        string host, int port, string username, string password, CancellationToken ct)
    {
        return Task.Run<IReadOnlyList<SpnHost>>(() =>
        {
            using var connection = CreateBoundConnection(host, port, username, password);

            var baseDn = ResolveDefaultNamingContext(connection)
                ?? throw new InvalidOperationException(
                    $"AD host '{host}' did not advertise a defaultNamingContext on RootDSE.");

            var attrs = new[] { "objectGUID", "dNSHostName", "cn", "servicePrincipalName" };
            var results = new List<SpnHost>();
            var pageControl = new PageResultRequestControl(500);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var request = new SearchRequest(baseDn, SpnFilter, SearchScope.Subtree, attrs);
                request.Controls.Add(pageControl);
                var response = (SearchResponse)connection.SendRequest(request);

                foreach (SearchResultEntry entry in response.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var parsed = ToSpnHost(entry);
                    if (parsed is not null) results.Add(parsed);
                }

                var responseControl = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                if (responseControl is null || responseControl.Cookie.Length == 0) break;
                pageControl.Cookie = responseControl.Cookie;
            }

            return results;
        }, ct);
    }

    private static SpnHost? ToSpnHost(SearchResultEntry entry)
    {
        string? objectGuid = null;
        if (entry.Attributes.Contains("objectGUID")
            && entry.Attributes["objectGUID"][0] is byte[] bytes && bytes.Length == 16)
        {
            objectGuid = new Guid(bytes).ToString();
        }

        var dns = entry.Attributes.Contains("dNSHostName") ? entry.Attributes["dNSHostName"][0]?.ToString() : null;
        var cn = entry.Attributes.Contains("cn") ? entry.Attributes["cn"][0]?.ToString() : null;

        // HostKey is the stable identity we diff on. Prefer objectGUID (survives a
        // rename), then FQDN, then cn. Skip an entry we cannot key at all.
        var hostKey = objectGuid ?? dns ?? cn;
        if (string.IsNullOrWhiteSpace(hostKey)) return null;

        var spns = new List<string>();
        if (entry.Attributes.Contains("servicePrincipalName"))
        {
            foreach (var v in entry.Attributes["servicePrincipalName"].GetValues(typeof(string)))
            {
                if (v is string s && s.StartsWith("MSSQLSvc/", StringComparison.OrdinalIgnoreCase))
                    spns.Add(s);
            }
        }

        return new SpnHost(hostKey, objectGuid, spns);
    }

    private static LdapConnection CreateBoundConnection(string host, int port, string username, string password)
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(host, port))
        {
            AuthType = AuthType.Negotiate
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

        NetworkCredential netCred;
        if (username.Contains('\\'))
        {
            var parts = username.Split('\\', 2);
            netCred = new NetworkCredential(parts[1], password) { Domain = parts[0] };
        }
        else
        {
            netCred = new NetworkCredential(username, password);
        }
        connection.Credential = netCred;
        connection.Bind();
        return connection;
    }

    private static string? ResolveDefaultNamingContext(LdapConnection connection)
    {
        var rootReq = new SearchRequest("", "(objectClass=*)", SearchScope.Base,
            new[] { "defaultNamingContext", "rootDomainNamingContext" });
        var rootResp = (SearchResponse)connection.SendRequest(rootReq);
        if (rootResp.Entries.Count == 0) return null;
        var attrs = rootResp.Entries[0].Attributes;
        if (attrs.Contains("defaultNamingContext")
            && attrs["defaultNamingContext"][0]?.ToString() is { Length: > 0 } dnc)
            return dnc;
        if (attrs.Contains("rootDomainNamingContext")
            && attrs["rootDomainNamingContext"][0]?.ToString() is { Length: > 0 } rdnc)
            return rdnc;
        return null;
    }
}
