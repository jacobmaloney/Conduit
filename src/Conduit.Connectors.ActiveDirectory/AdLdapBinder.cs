using System;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.ActiveDirectory;

/// <summary>What one bind needs. Port 636 is LDAPS; any other port tries StartTLS and otherwise binds Negotiate sign+seal.</summary>
public sealed record AdBindOptions(
    string Host,
    int Port,
    string Username,
    string Password,
    bool AllowUntrustedCertificate,
    string? ExpectedServerCertificateThumbprint,
    ReferralChasingOptions ReferralChasing = ReferralChasingOptions.None,
    TimeSpan? Timeout = null);

/// <summary>
/// THE one Active Directory bind for reads, writes, the connection test and the container browser.
///
/// Until 2026-10-16 the sink owned the only bind that understood port 636, StartTLS and the
/// per-connection certificate policy; the source (reads, the connection test) and the container
/// browser each carried a plain Negotiate bind that ignored the port's meaning. An AD connection on
/// :636 therefore tested and read as "The LDAP server is unavailable" no matter what the DC had, and
/// Jacob read that as the server going away. Three copies, one behaviour now.
///
/// Secure channel: LDAPS on 636, or a successful StartTLS upgrade elsewhere; the caller gates
/// password writes on <c>isSecure</c>. Without a secure channel the bind is Negotiate with signing
/// and sealing (what a signing-required DC accepts; strictly stronger than a plaintext simple bind).
/// </summary>
public static class AdLdapBinder
{
    public static LdapConnection Bind(AdBindOptions options, ILogger logger, string tenantId, out bool isSecure)
    {
        var host = options.Host;
        var port = options.Port;
        var connection = new LdapConnection(new LdapDirectoryIdentifier(host, port))
        {
            AuthType = AuthType.Basic
        };
        if (options.Timeout is { } timeout) connection.Timeout = timeout;
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.ReferralChasing = options.ReferralChasing;
        try
        {
            // Shared fail-closed server-certificate policy, wired identically to both the :636 SSL
            // path and the :389 StartTLS upgrade path. Wrapped so we KNOW whether a certificate was
            // actually rejected, rather than inferring it from the exception type below:
            // TlsOperationException means both "your validator said no" and "this server does not
            // offer StartTLS at all", and those need opposite handling.
            var certificateRejected = false;
            var verify = Conduit.Sync.Security.LdapServerCertificateValidator.Build(
                logger, tenantId, host, options.AllowUntrustedCertificate, options.ExpectedServerCertificateThumbprint);
            connection.SessionOptions.VerifyServerCertificate = (conn, certificate) =>
            {
                var accepted = verify(conn, certificate);
                if (!accepted) certificateRejected = true;
                return accepted;
            };
            isSecure = false;

            if (port == 636)
            {
                connection.SessionOptions.SecureSocketLayer = true;
                isSecure = true;
            }
            else
            {
                // Best-effort StartTLS upgrade. If the server does not offer it we stay plaintext and
                // bind Negotiate below; a certificate that FAILS validation is a hard reject (the
                // callback returns false, StartTransportLayerSecurity throws), never a silent downgrade.
                try
                {
                    connection.SessionOptions.StartTransportLayerSecurity(null);
                    isSecure = true;
                }
                catch (TlsOperationException) when (certificateRejected)
                {
                    throw;
                }
                catch (TlsOperationException ex)
                {
                    // A domain controller with no LDAPS certificate answers the extended operation with
                    // "unavailable" and .NET surfaces that as TlsOperationException too. Only password
                    // writes need the secure channel; the caller refuses those because isSecure stays false.
                    logger.LogDebug(ex, "StartTLS not offered by {Host}:{Port}; the connection will be plaintext. Password writes will be refused.", host, port);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "StartTLS not available on {Host}:{Port}; the connection will be plaintext. Password writes will be refused.", host, port);
                }
            }

            connection.Credential = new NetworkCredential(options.Username, options.Password);

            if (!isSecure)
            {
                // No LDAPS and no StartTLS: a simple bind would put the password on the wire in clear
                // text and a signing-required DC refuses it (strongerAuthRequired). Negotiate with
                // signing and sealing protects the channel with Kerberos/NTLM and no PKI. The secure
                // paths keep AuthType.Basic: that channel is already encrypted.
                connection.AuthType = AuthType.Negotiate;
                connection.SessionOptions.Signing = true;
                connection.SessionOptions.Sealing = true;

                // SASL needs the domain as a SEPARATE field: "domain.local\Administrator" as one string
                // is "The supplied credential is invalid" under Negotiate, the same account with the
                // domain passed separately binds (measured against the lab DC, all three spellings).
                var (bindUser, bindDomain) = SplitDownLevelLogonName(options.Username);
                if (bindDomain is not null)
                    connection.Credential = new NetworkCredential(bindUser, options.Password, bindDomain);
                logger.LogDebug("Binding to {Host}:{Port} with Negotiate sign+seal; the channel is not TLS-protected.", host, port);
            }

            connection.Bind();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Splits a down-level logon name ("DOMAIN\user", "domain.local\user") into its parts for a SASL
    /// bind. Returns a null domain for anything that is not down-level (a bare username or a UPN),
    /// because both of those already bind correctly as a single string and splitting would break them.
    /// </summary>
    public static (string User, string? Domain) SplitDownLevelLogonName(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return (username ?? string.Empty, null);

        var slash = username.IndexOf('\\');
        if (slash <= 0 || slash == username.Length - 1) return (username, null);

        var domain = username[..slash];
        var user = username[(slash + 1)..];
        return string.IsNullOrWhiteSpace(user) ? (username, null) : (user, domain);
    }

    /// <summary>"host" or "host:port"; no port means 389.</summary>
    public static (string Host, int Port) ParseHostPort(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            throw new InvalidOperationException("Tenant.Domain is required for AD connections (host or host:port).");
        var parts = domain.Split(':');
        if (parts.Length == 1) return (parts[0], 389);
        if (int.TryParse(parts[1], out var p)) return (parts[0], p);
        return (parts[0], 389);
    }

    /// <summary>
    /// The stored "ldap" credential JSON: Username and Password, plus the per-connection TLS policy
    /// (AllowUntrustedCertificate, ExpectedServerCertificateThumbprint), which defaults to fail-closed
    /// when absent. Null when the document does not carry a usable username and password.
    /// </summary>
    public static (string Username, string Password, bool AllowUntrustedCertificate, string? ExpectedServerCertificateThumbprint)? ParseCredentialJson(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var u = doc.RootElement.TryGetProperty("Username", out var uEl) ? uEl.GetString() : null;
            var p = doc.RootElement.TryGetProperty("Password", out var pEl) ? pEl.GetString() : null;
            if (string.IsNullOrEmpty(u) || p is null) return null;
            var allowUntrusted = doc.RootElement.TryGetProperty("AllowUntrustedCertificate", out var aEl)
                && aEl.ValueKind == JsonValueKind.True;
            var pin = doc.RootElement.TryGetProperty("ExpectedServerCertificateThumbprint", out var tEl)
                ? tEl.GetString() : null;
            return (u, p, allowUntrusted, string.IsNullOrWhiteSpace(pin) ? null : pin);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
