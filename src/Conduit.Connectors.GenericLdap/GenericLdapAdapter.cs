using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Text.Json;
using System.Threading.Tasks;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.GenericLdap;

/// <summary>
/// Generic LDAP adapter — RFC 4511 directories (OpenLDAP, 389-DS, ApacheDS,
/// custom). No AD-specific semantics: no unicodePwd, no userAccountControl,
/// no defaultNamingContext fallback for base DN. Caller must supply BaseDN
/// explicitly via SyncProjectScope.
///
/// Credentials stored under "ldap": { ServerUrl, BindDN, BindPassword,
/// UseTls? }. ServerUrl is host:port or ldap(s)://host:port.
/// </summary>
public sealed class GenericLdapAdapter : IConnectorAdapter
{
    public string SystemType => "GenericLdap";
    public string DisplayName => "Generic LDAP";
    public bool SupportsSource => true;
    public bool SupportsSink => true;

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes { get; } = new[]
    {
        new CredentialTypeInfo
        {
            Name = "ldap",
            DisplayName = "LDAP Bind",
            Description = "Simple bind against an LDAP v3 server. ldaps:// for TLS, ldap:// for plaintext (or with StartTLS).",
            Fields = new[]
            {
                new CredentialFieldSpec { Key = "ServerUrl", Label = "Server URL", IsRequired = true, Placeholder = "ldap://host:389 or ldaps://host:636" },
                new CredentialFieldSpec { Key = "BindDN", Label = "Bind DN", IsRequired = true, Placeholder = "cn=admin,dc=example,dc=org" },
                new CredentialFieldSpec { Key = "BindPassword", Label = "Bind Password", IsRequired = true, IsSecret = true },
                new CredentialFieldSpec { Key = "UseTls", Label = "Use StartTLS", IsBoolean = true },
                new CredentialFieldSpec
                {
                    Key = "ExpectedServerCertificateThumbprint",
                    Label = "Pinned server certificate SHA-256 thumbprint",
                    Placeholder = "hex, e.g. A1B2C3… (preferred over AllowUntrustedCertificate)",
                    Help = "When set, accepts an untrusted-root chain only if the server certificate's SHA-256 thumbprint matches exactly. Hostname is still enforced."
                },
                new CredentialFieldSpec
                {
                    Key = "AllowUntrustedCertificate",
                    Label = "Allow untrusted server certificate (LAB ONLY)",
                    IsBoolean = true,
                    Help = "Last-resort blanket bypass of certificate validation. Defaults to off. Prefer thumbprint pinning."
                },
            }
        }
    };

    private readonly TenantRepository _tenantRepo;
    private readonly CredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public GenericLdapAdapter(TenantRepository tenantRepo, CredentialProtector protector, ILoggerFactory loggerFactory)
    {
        _tenantRepo = tenantRepo;
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new GenericLdapSource(tenantId, _protector, _loggerFactory.CreateLogger<GenericLdapSource>());

    public IConnectorSink? CreateSink(Guid tenantId) =>
        new GenericLdapSink(tenantId, _protector, _loggerFactory.CreateLogger<GenericLdapSink>());
}

internal sealed record GenericLdapCredentials(
    string ServerUrl,
    string BindDN,
    string BindPassword,
    bool UseTls,
    bool AllowUntrustedCertificate,
    string? ExpectedServerCertificateThumbprint);

internal static class GenericLdapCredentialReader
{
    public const string CredentialName = "ldap";

    public static async Task<GenericLdapCredentials?> ReadAsync(CredentialProtector p, Guid tenantId)
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
            var url = doc.RootElement.TryGetProperty("ServerUrl", out var uEl) ? uEl.GetString() : null;
            var dn = doc.RootElement.TryGetProperty("BindDN", out var dEl) ? dEl.GetString() : null;
            var pwd = doc.RootElement.TryGetProperty("BindPassword", out var pEl) ? pEl.GetString() : null;
            var tls = doc.RootElement.TryGetProperty("UseTls", out var tEl) && tEl.ValueKind == JsonValueKind.True;
            var allowUntrusted = doc.RootElement.TryGetProperty("AllowUntrustedCertificate", out var aEl)
                && aEl.ValueKind == JsonValueKind.True;
            var pin = doc.RootElement.TryGetProperty("ExpectedServerCertificateThumbprint", out var pinEl)
                ? pinEl.GetString() : null;
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(dn) || pwd is null) return null;
            return new GenericLdapCredentials(url!, dn!, pwd!, tls, allowUntrusted, pin);
        }
        catch { return null; }
    }

    /// <summary>
    /// Applies the shared fail-closed server-certificate policy and the requested
    /// transport security to an LDAP connection. Wires the SAME validator callback
    /// to both the ldaps:// (:636 SSL) path and the StartTLS upgrade path. A cert
    /// rejected during StartTLS is a hard reject (TlsOperationException), never a
    /// silent plaintext downgrade.
    /// </summary>
    public static void ApplyTls(
        Microsoft.Extensions.Logging.ILogger logger,
        Guid tenantId,
        LdapConnection conn,
        string host,
        bool secure,
        bool useTls,
        bool allowUntrustedCertificate,
        string? expectedThumbprintSha256)
    {
        conn.SessionOptions.VerifyServerCertificate =
            Conduit.Sync.Security.LdapServerCertificateValidator.Build(
                logger, tenantId.ToString(), host, allowUntrustedCertificate, expectedThumbprintSha256);

        if (secure)
        {
            conn.SessionOptions.SecureSocketLayer = true;
        }
        else if (useTls)
        {
            conn.SessionOptions.StartTransportLayerSecurity(null);
        }
    }

    public static (string Host, int Port, bool Secure) ParseServerUrl(string serverUrl)
    {
        var s = serverUrl.Trim();
        var secure = false;
        if (s.StartsWith("ldaps://", StringComparison.OrdinalIgnoreCase)) { secure = true; s = s.Substring(8); }
        else if (s.StartsWith("ldap://", StringComparison.OrdinalIgnoreCase)) { s = s.Substring(7); }
        var slash = s.IndexOf('/');
        if (slash >= 0) s = s.Substring(0, slash);
        var parts = s.Split(':');
        if (parts.Length == 1) return (parts[0], secure ? 636 : 389, secure);
        if (int.TryParse(parts[1], out var port)) return (parts[0], port, secure);
        return (parts[0], secure ? 636 : 389, secure);
    }
}
