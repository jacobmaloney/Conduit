using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.ActiveRoles;

/// <summary>
/// Phase 2 fast-read REAL-attribute source. Paged raw-AD LDAP enumeration via
/// <c>System.DirectoryServices.Protocols</c> against a domain controller — NOT the
/// AR Administration Service. This is the millisecond-latency path: it bypasses
/// ARS entirely for the real (directory-backed) attributes; the virtual attributes
/// are joined in separately from <c>CVSAValues</c> (see <see cref="CvsaValueReader"/>).
///
/// Forked from <c>Conduit.Connectors.ActiveDirectory.ActiveDirectorySource</c> —
/// same paged-results control (RFC 2696), same <see cref="ReferralChasingOptions.None"/>
/// performance fix (commit 20cf600), same Negotiate bind and attribute extraction.
/// Trimmed to a full read (no tombstone pass); the watermark is tracked so the
/// source can optionally surface a whenChanged cursor.
/// </summary>
internal sealed class FastAdReader
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _bindUser;
    private readonly string _bindPassword;
    private readonly ILogger _logger;

    /// <summary>
    /// Structural attributes ALWAYS unioned into the requested projection so a
    /// trimmed read can never starve SourceId (objectGUID), the cursor
    /// (whenChanged), or class gating. Mirrors the AD source's floor.
    /// </summary>
    private static readonly string[] StructuralAttributes =
    {
        "objectGUID",
        "objectClass",
        "objectCategory",
        "whenChanged",
        "sAMAccountName",
        "userPrincipalName",
        "distinguishedName",
    };

    public FastAdReader(string host, int port, string bindUser, string bindPassword, ILogger logger)
    {
        _host = host;
        _port = port;
        _bindUser = bindUser;
        _bindPassword = bindPassword;
        _logger = logger;
    }

    /// <summary>Highest whenChanged seen during the last enumeration (UTC). Null until observed.</summary>
    public DateTime? MaxWhenChanged { get; private set; }

    /// <summary>
    /// True only when the most recent enumeration drained every included base to
    /// its natural empty-cookie terminus with no truncation/error. Mirrors the AD
    /// source's complete-read sentinel so a future incremental/tombstone path can
    /// gate on it. Reset at the start of each <see cref="ReadAsync"/>.
    /// </summary>
    public bool WasCompleteRead { get; private set; }

    public LdapConnection CreateBoundConnection()
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(_host, _port))
        {
            AuthType = AuthType.Negotiate
        };
        connection.SessionOptions.ProtocolVersion = 3;
        // Same single-domain perf fix as the AD connector: never chase referrals
        // (commit 20cf600 — .All adds seconds-to-minutes of dead latency per page).
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

        NetworkCredential netCred;
        if (_bindUser.Contains('\\'))
        {
            var parts = _bindUser.Split('\\', 2);
            netCred = new NetworkCredential(parts[1], _bindPassword) { Domain = parts[0] };
        }
        else
        {
            netCred = new NetworkCredential(_bindUser, _bindPassword);
        }
        connection.Credential = netCred;
        connection.Bind();
        return connection;
    }

    public string? ResolveDefaultNamingContext(LdapConnection connection)
    {
        try
        {
            var rootReq = new SearchRequest("", "(objectClass=*)", SearchScope.Base,
                new[] { "defaultNamingContext", "rootDomainNamingContext" });
            var rootResp = (SearchResponse)connection.SendRequest(rootReq);
            if (rootResp.Entries.Count == 0) return null;
            var attrs = rootResp.Entries[0].Attributes;
            if (attrs.Contains("defaultNamingContext"))
            {
                var v = attrs["defaultNamingContext"][0]?.ToString();
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            if (attrs.Contains("rootDomainNamingContext"))
            {
                var v = attrs["rootDomainNamingContext"][0]?.ToString();
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ARS fast read: RootDSE defaultNamingContext probe failed on {Host}", _host);
            return null;
        }
    }

    /// <summary>
    /// Paged subtree enumeration of the included bases. Emits a ConnectorObject per
    /// entry with the REAL attributes only (VAs are joined by the caller). SourceId
    /// = objectGUID (matching the Conduit AD source). S.DS.Protocols is synchronous,
    /// so this async iterator yields without awaiting (CS1998 suppressed) — the
    /// shape matches Conduit's AD source for consistency.
    /// </summary>
#pragma warning disable CS1998
    public async IAsyncEnumerable<ConnectorObject> ReadAsync(
        string objectClass,
        SyncProjectScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        MaxWhenChanged = null;
        WasCompleteRead = false;

        var baseFilter = !string.IsNullOrWhiteSpace(scope.LdapFilter)
            ? scope.LdapFilter!
            : DefaultFilterForClass(objectClass);

        var includedBases = OptimizeBases(scope.GetIncludedBaseList());
        var excludedBases = scope.GetExcludedBaseList();

        using var connection = CreateBoundConnection();

        if (includedBases.Count == 0)
        {
            var rootDn = ResolveDefaultNamingContext(connection);
            if (string.IsNullOrWhiteSpace(rootDn))
                throw new InvalidOperationException(
                    "Active Roles fast read has no included Base DN and the DC did not advertise a defaultNamingContext. Pick a container in the scope.");
            includedBases = new List<string> { rootDn! };
        }

        // Attribute projection: mapped attrs + structural floor. VAs in the mapped
        // set are simply not returned by raw AD (they're virtual) — the caller joins
        // them from CVSAValues — so requesting them here is harmless.
        string[]? attributeList = null;
        if (scope.RequestedAttributes is { Count: > 0 })
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in scope.RequestedAttributes)
                if (!string.IsNullOrWhiteSpace(a)) set.Add(a.Trim());
            foreach (var s in StructuralAttributes) set.Add(s);
            attributeList = set.ToArray();
        }

        var pageSize = scope.PageSize > 0 ? scope.PageSize : 1000;
        var max = scope.MaxObjects ?? int.MaxValue;
        var emitted = 0;

        for (var baseIdx = 0; baseIdx < includedBases.Count; baseIdx++)
        {
            var searchBase = includedBases[baseIdx];
            var isLastBase = baseIdx == includedBases.Count - 1;
            var pageControl = new PageResultRequestControl(pageSize);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = new SearchRequest(searchBase, baseFilter, SearchScope.Subtree, attributeList);
                request.Controls.Add(pageControl);

                SearchResponse response;
                try
                {
                    response = (SearchResponse)connection.SendRequest(request);
                }
                catch (DirectoryOperationException ex)
                {
                    _logger.LogError(ex, "ARS fast read: LDAP search failed (base {Base}, filter {Filter})", searchBase, baseFilter);
                    throw;
                }

                foreach (SearchResultEntry entry in response.Entries)
                {
                    if (emitted >= max) yield break; // truncated — leave WasCompleteRead false

                    if (IsInExcludedScope(entry.DistinguishedName, excludedBases)) continue;

                    ObserveWhenChanged(entry);
                    yield return EntryToConnectorObject(entry, objectClass);
                    emitted++;
                }

                var responseControl = FindPageResponseControl(response.Controls);
                if (responseControl is null || responseControl.Cookie.Length == 0)
                {
                    if (isLastBase) WasCompleteRead = true;
                    break;
                }
                pageControl.Cookie = responseControl.Cookie;
            }
        }
    }
#pragma warning restore CS1998

    private void ObserveWhenChanged(SearchResultEntry entry)
    {
        if (!entry.Attributes.Contains("whenChanged")) return;
        var raw = entry.Attributes["whenChanged"][0]?.ToString();
        if (string.IsNullOrEmpty(raw) || raw.Length < 14) return;
        if (DateTime.TryParseExact(raw.Substring(0, 14), "yyyyMMddHHmmss", null,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            if (MaxWhenChanged is null || dt > MaxWhenChanged.Value) MaxWhenChanged = dt;
        }
    }

    private static ConnectorObject EntryToConnectorObject(SearchResultEntry entry, string objectClass)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["distinguishedName"] = entry.DistinguishedName
        };

        foreach (string name in entry.Attributes.AttributeNames!)
        {
            var attr = entry.Attributes[name];
            if (attr.Count == 0) { attrs[name] = null; continue; }
            if (attr.Count == 1) attrs[name] = attr[0];
            else
            {
                var list = new List<object?>(attr.Count);
                for (var i = 0; i < attr.Count; i++) list.Add(attr[i]);
                attrs[name] = list;
            }
        }

        var sourceId = attrs.TryGetValue("objectGUID", out var g) && g is byte[] bytes && bytes.Length == 16
            ? new Guid(bytes).ToString()
            : entry.DistinguishedName;

        return new ConnectorObject
        {
            SourceId = sourceId,
            ObjectClass = string.IsNullOrWhiteSpace(objectClass) ? "user" : objectClass,
            Attributes = attrs
        };
    }

    private static List<string> OptimizeBases(List<string> bases)
    {
        var optimized = new List<string>();
        foreach (var b in bases.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).OrderBy(s => s.Length))
        {
            var redundant = optimized.Any(parent =>
                b.EndsWith(parent, StringComparison.OrdinalIgnoreCase) && b.Length > parent.Length);
            if (!redundant) optimized.Add(b);
        }
        return optimized;
    }

    private static bool IsInExcludedScope(string? dn, List<string> excludedBases)
    {
        if (string.IsNullOrEmpty(dn) || excludedBases.Count == 0) return false;
        foreach (var excluded in excludedBases)
            if (!string.IsNullOrWhiteSpace(excluded) && dn.EndsWith(excluded.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string DefaultFilterForClass(string objectClass) => objectClass.ToLowerInvariant() switch
    {
        "user"     => "(&(objectClass=user)(objectCategory=person))",
        "group"    => "(objectCategory=group)",
        "computer" => "(objectCategory=computer)",
        "contact"  => "(objectClass=contact)",
        _          => "(objectClass=*)"
    };

    private static PageResultResponseControl? FindPageResponseControl(DirectoryControl[] controls)
    {
        foreach (var c in controls)
            if (c is PageResultResponseControl p) return p;
        return null;
    }
}
