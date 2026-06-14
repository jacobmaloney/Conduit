using System;
using System.DirectoryServices.Protocols;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Conduit.Sync.Security;

/// <summary>
/// The single shared LDAP/LDAPS server-certificate validation policy for every
/// Conduit directory bind (AD sink, Generic LDAP source/sink). Wired identically
/// to the :636 SSL path and the :389 StartTLS upgrade path — a cert that fails
/// here on StartTLS is a HARD REJECT, never a silent downgrade to plaintext.
///
/// Policy (fail-closed by default):
///   * No options set => accept ONLY when SslPolicyErrors == None.
///   * RemoteCertificateNotAvailable => ALWAYS reject (no override).
///   * RemoteCertificateNameMismatch => NEVER suppressed by a pin (hostname is
///     always enforced); only AllowUntrustedCertificate=true can override it.
///   * ExpectedServerCertificateThumbprint (SHA-256 pin) => accept a chain that
///     fails ONLY because the root is untrusted, IFF the presented cert's
///     SHA-256 thumbprint matches the pin exactly.
///   * AllowUntrustedCertificate=true => last-resort, all-or-nothing, lab-only
///     blanket accept (still rejects NotAvailable).
///
/// Every bypass (pin-accept or blanket-accept) is logged at WARNING, every bind.
/// Every decision logs Subject/Issuer/SHA-256 thumbprint/NotBefore/NotAfter/
/// SslPolicyErrors — never raw cert bytes.
/// </summary>
public static class LdapServerCertificateValidator
{
    /// <summary>
    /// Builds the verify callback for <c>SessionOptions.VerifyServerCertificate</c>.
    /// The same delegate must be assigned before the SSL bind AND before
    /// StartTransportLayerSecurity on the same connection.
    /// </summary>
    public static VerifyServerCertificateCallback Build(
        ILogger logger,
        string tenant,
        string host,
        bool allowUntrustedCertificate,
        string? expectedThumbprintSha256)
    {
        var pin = NormalizeThumbprint(expectedThumbprintSha256);

        return (connection, certificate) =>
        {
            using var cert = new X509Certificate2(certificate);
            var thumb = Sha256Thumbprint(cert);

            // S.DS.P hands us only the leaf cert. Build a chain (with revocation
            // on for the strict path) so we can both compute SslPolicyErrors AND
            // tell an UNTRUSTED-ROOT failure apart from expiry/revocation/EKU.
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            var chainBuilt = chain.Build(cert);
            var nameMismatch = !HostMatchesCertificate(cert, host);
            // Only reached on the pin path (the strict path requires chainBuilt),
            // so this asks exactly: is the chain failure ONLY an unreachable trust
            // anchor (untrusted root / partial chain)?
            var trustOnlyFailure = OnlyUntrustedRootOrPartialChain(chain);

            var sslErrors = SslPolicyErrors.None;
            if (!chainBuilt) sslErrors |= SslPolicyErrors.RemoteCertificateChainErrors;
            if (nameMismatch) sslErrors |= SslPolicyErrors.RemoteCertificateNameMismatch;

            if (chainBuilt && !nameMismatch)
            {
                LogDecision(logger, LogLevel.Debug, "ACCEPT", "platform-trusted chain",
                    tenant, host, cert, thumb, sslErrors);
                return true;
            }

            // Pinning: accept ONLY when the sole defect is an untrusted root /
            // partial chain (NOT expiry, revocation, or wrong EKU), the hostname
            // still matches, and the SHA-256 thumbprint matches the pin exactly.
            if (pin is not null && !nameMismatch && trustOnlyFailure &&
                string.Equals(thumb, pin, StringComparison.OrdinalIgnoreCase))
            {
                LogDecision(logger, LogLevel.Warning, "ACCEPT",
                    "thumbprint pin matched (untrusted root accepted by ExpectedServerCertificateThumbprint)",
                    tenant, host, cert, thumb, sslErrors);
                return true;
            }

            // Last-resort blanket bypass — lab only.
            if (allowUntrustedCertificate)
            {
                LogDecision(logger, LogLevel.Warning, "ACCEPT",
                    "AllowUntrustedCertificate=true blanket bypass (lab-only, all-or-nothing)",
                    tenant, host, cert, thumb, sslErrors);
                return true;
            }

            LogDecision(logger, LogLevel.Error, "REJECT",
                pin is not null
                    ? "fail-closed (pin did not match, hostname mismatch, or a non-trust defect e.g. expired/revoked)"
                    : "fail-closed (untrusted/invalid chain; set ExpectedServerCertificateThumbprint to pin)",
                tenant, host, cert, thumb, sslErrors);
            return false;
        };
    }

    /// <summary>
    /// True only when the chain's failure is the bypassable "trust anchor not
    /// available" condition — i.e. an untrusted root / partial chain. Because the
    /// issuing CA is, by definition, not reachable in that state, its CRL/OCSP
    /// cannot be retrieved either, so RevocationStatusUnknown / OfflineRevocation
    /// are downstream consequences of the same untrusted root and are tolerated
    /// HERE (and only here, on the pin path). A hard <c>Revoked</c> status, an
    /// expired cert (<c>NotTimeValid</c>), wrong EKU, name constraints, or any
    /// other independent defect returns false, so a pin can never accept such a
    /// certificate.
    /// </summary>
    private static bool OnlyUntrustedRootOrPartialChain(X509Chain chain)
    {
        var sawTrustAnchorDefect = false;
        foreach (var status in chain.ChainStatus)
        {
            switch (status.Status)
            {
                case X509ChainStatusFlags.NoError:
                    continue;
                case X509ChainStatusFlags.UntrustedRoot:
                case X509ChainStatusFlags.PartialChain:
                    sawTrustAnchorDefect = true;
                    continue;
                // Downstream of an unreachable trust anchor — tolerated only on
                // the pin path. Never tolerate an affirmative Revoked status.
                case X509ChainStatusFlags.RevocationStatusUnknown:
                case X509ChainStatusFlags.OfflineRevocation:
                    continue;
                default:
                    return false;
            }
        }
        return sawTrustAnchorDefect;
    }

    private static bool HostMatchesCertificate(X509Certificate2 cert, string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        // SAN (preferred). MatchesHostname enforces DNS-name / wildcard rules.
        try
        {
            if (cert.MatchesHostname(host, allowWildcards: true, allowCommonName: true))
                return true;
        }
        catch
        {
            // Fall through to CN comparison if the platform rejects the host form.
        }

        var cn = cert.GetNameInfo(X509NameType.DnsName, forIssuer: false);
        return !string.IsNullOrEmpty(cn) && string.Equals(cn, host, StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha256Thumbprint(X509Certificate2 cert)
        => Convert.ToHexString(SHA256.HashData(cert.RawData));

    private static string? NormalizeThumbprint(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) return null;
        var cleaned = thumbprint.Replace(":", string.Empty).Replace(" ", string.Empty).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static void LogDecision(
        ILogger logger, LogLevel level, string decision, string reason,
        string tenant, string host, X509Certificate2 cert, string thumb, SslPolicyErrors errors)
    {
        logger.Log(level,
            "LDAP server certificate {Decision} for tenant={Tenant} host={Host}: {Reason}. " +
            "Subject={Subject} Issuer={Issuer} Sha256Thumbprint={Thumbprint} " +
            "NotBefore={NotBefore:o} NotAfter={NotAfter:o} SslPolicyErrors={SslPolicyErrors}",
            decision, tenant, host, reason,
            cert.Subject, cert.Issuer, thumb,
            cert.NotBefore, cert.NotAfter, errors);
    }
}
