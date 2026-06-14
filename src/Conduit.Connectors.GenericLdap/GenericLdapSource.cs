using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.GenericLdap;

/// <summary>
/// Generic LDAP source via S.DS.Protocols with RFC 2696 paged results. BaseDN
/// is REQUIRED (no defaultNamingContext fallback — this is by design; RFC 4511
/// servers vary). Filter defaults to (objectClass=&lt;objectClass&gt;) unless
/// caller supplies one.
/// </summary>
public sealed class GenericLdapSource : IConnectorSource
{
    private readonly Guid _tenantId;
    private readonly CredentialProtector _protector;
    private readonly ILogger<GenericLdapSource> _logger;

    public GenericLdapSource(Guid tenantId, CredentialProtector protector, ILogger<GenericLdapSource> logger)
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
        var creds = await GenericLdapCredentialReader.ReadAsync(_protector, _tenantId)
            ?? throw new InvalidOperationException($"No 'ldap' credential for tenant {_tenantId}.");

        if (string.IsNullOrWhiteSpace(scope.BaseDN))
            throw new InvalidOperationException("Generic LDAP source requires SyncProjectScope.BaseDN — no defaultNamingContext fallback.");

        var filter = !string.IsNullOrWhiteSpace(scope.LdapFilter)
            ? scope.LdapFilter!
            : $"(objectClass={objectClass})";

        var (host, port, secure) = GenericLdapCredentialReader.ParseServerUrl(creds.ServerUrl);
        using var conn = new LdapConnection(new LdapDirectoryIdentifier(host, port)) { AuthType = AuthType.Basic };
        conn.SessionOptions.ProtocolVersion = 3;
        GenericLdapCredentialReader.ApplyTls(_logger, _tenantId, conn, host, secure, creds.UseTls,
            creds.AllowUntrustedCertificate, creds.ExpectedServerCertificateThumbprint);
        conn.Credential = new NetworkCredential(creds.BindDN, creds.BindPassword);
        conn.Bind();

        var pageSize = scope.PageSize > 0 ? scope.PageSize : 500;
        var pageControl = new PageResultRequestControl(pageSize);
        var emitted = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var req = new SearchRequest(scope.BaseDN, filter, SearchScope.Subtree, attributeList: null);
            req.Controls.Add(pageControl);
            SearchResponse resp;
            try { resp = (SearchResponse)conn.SendRequest(req); }
            catch (DirectoryOperationException ex)
            {
                _logger.LogError(ex, "LDAP search failed (base {Base}, filter {Filter})", scope.BaseDN, filter);
                throw;
            }
            foreach (SearchResultEntry entry in resp.Entries)
            {
                if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                emitted++;
                yield return ConvertEntry(entry, objectClass);
            }
            var responseControl = FindPageResponseControl(resp.Controls);
            if (responseControl is null || responseControl.Cookie.Length == 0) yield break;
            pageControl.Cookie = responseControl.Cookie;
        }
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var creds = await GenericLdapCredentialReader.ReadAsync(_protector, _tenantId);
            if (creds is null) return new ConnectorTestResult { IsSuccessful = false, Message = "No 'ldap' credential stored." };
            var (host, port, secure) = GenericLdapCredentialReader.ParseServerUrl(creds.ServerUrl);
            using var conn = new LdapConnection(new LdapDirectoryIdentifier(host, port)) { AuthType = AuthType.Basic };
            conn.SessionOptions.ProtocolVersion = 3;
            GenericLdapCredentialReader.ApplyTls(_logger, _tenantId, conn, host, secure, creds.UseTls,
            creds.AllowUntrustedCertificate, creds.ExpectedServerCertificateThumbprint);
            conn.Credential = new NetworkCredential(creds.BindDN, creds.BindPassword);
            conn.Bind();
            // RootDSE probe (vendor-agnostic — every RFC 4511 server returns
            // this).
            var req = new SearchRequest("", "(objectClass=*)", SearchScope.Base,
                new[] { "namingContexts", "subschemaSubentry", "supportedLDAPVersion" });
            var resp = (SearchResponse)conn.SendRequest(req);
            return new ConnectorTestResult
            {
                IsSuccessful = true,
                Message = $"Bound to {host}:{port}. RootDSE returned {resp.Entries.Count} entries."
            };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

    private static ConnectorObject ConvertEntry(SearchResultEntry entry, string objectClass)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["distinguishedName"] = entry.DistinguishedName,
            ["dn"] = entry.DistinguishedName,
            ["objectClass"] = objectClass
        };
        foreach (string name in entry.Attributes.AttributeNames!)
        {
            var attr = entry.Attributes[name];
            if (attr.Count == 0) { attrs[name] = null; continue; }
            if (attr.Count == 1) attrs[name] = attr[0];
            else
            {
                var list = new List<object?>(attr.Count);
                for (int i = 0; i < attr.Count; i++) list.Add(attr[i]);
                attrs[name] = list;
            }
        }
        // SourceId — prefer entryUUID (RFC 4530), then uid, then DN.
        string sourceId;
        if (attrs.TryGetValue("entryUUID", out var euu) && euu is string eu) sourceId = eu;
        else if (attrs.TryGetValue("uid", out var u) && u is string us) sourceId = us;
        else sourceId = entry.DistinguishedName;
        // Friendly aliases for common SCIM/AD mappings.
        if (attrs.TryGetValue("uid", out var uidv) && uidv is string uidStr) attrs["userName"] = uidStr;
        if (attrs.TryGetValue("cn", out var cnv) && cnv is string cnStr) attrs["displayName"] ??= cnStr;
        if (attrs.TryGetValue("mail", out var mv) && mv is string mStr) attrs["email"] ??= mStr;
        if (attrs.TryGetValue("givenName", out var gn) && gn is string gnStr) attrs["givenName"] ??= gnStr;
        if (attrs.TryGetValue("sn", out var sn) && sn is string snStr) { attrs["surname"] ??= snStr; attrs["familyName"] ??= snStr; }
        return new ConnectorObject
        {
            SourceId = sourceId,
            ObjectClass = objectClass,
            Attributes = attrs
        };
    }

    private static PageResultResponseControl? FindPageResponseControl(DirectoryControl[] controls)
    {
        foreach (var c in controls)
            if (c is PageResultResponseControl p) return p;
        return null;
    }
}
