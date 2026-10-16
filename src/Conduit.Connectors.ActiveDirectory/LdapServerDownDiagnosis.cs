using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit.Connectors.ActiveDirectory;

/// <summary>
/// Turns "The LDAP server is unavailable." into the sentence an operator can act on. S.DS.P raises
/// that one message (ServerDown, 81) for three different situations: nothing listens on the port, the
/// port is open but the directory closed the TLS handshake (a domain controller with no
/// server-authentication certificate: port 636 accepts the TCP connection and then resets), or the
/// name resolves to the wrong host. Jacob hit the second one on 2026-10-16 after moving the lab
/// connection to :636 and read it as "the server went away". No credential is used here: a TCP
/// connect and, on 636, a TLS client hello that accepts any certificate.
/// </summary>
public static class LdapServerDownDiagnosis
{
    public static Task<string> DescribeAsync(string host, int port, CancellationToken ct = default, int timeoutMs = 5000)
        => DescribeAsync(host, port, expectTls: port == 636, ct, timeoutMs);

    /// <summary><paramref name="expectTls"/> lets a test exercise the handshake branch on an ephemeral port.</summary>
    public static async Task<string> DescribeAsync(string host, int port, bool expectTls, CancellationToken ct = default, int timeoutMs = 5000)
    {
        using var client = new TcpClient();
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectTimeout.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, connectTimeout.Token);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound)
        {
            return $"'{host}' does not resolve. Check the name (a hosts-file or DNS entry that points at the retired host looks exactly like this).";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"Nothing answered on {host}:{port} within {timeoutMs / 1000} s. Check the host and whether the directory listens on that port.";
        }
        catch (SocketException)
        {
            return $"Nothing listens on {host}:{port}. For LDAPS the domain controller must listen on 636; for plain LDAP use {host}:389.";
        }

        if (!expectTls)
            return $"{host}:{port} accepted the connection but the directory did not answer the LDAP bind. Check that this is a domain controller and that the credential is a domain account.";

        try
        {
            using var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeTimeout.CancelAfter(timeoutMs);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, handshakeTimeout.Token);
            var subject = tls.RemoteCertificate?.Subject ?? "(no subject)";
            return $"{host}:636 completed a TLS handshake (certificate {subject}) but the LDAP bind still failed. Check the credential and the certificate policy on this connection.";
        }
        catch (Exception)
        {
            return $"Port 636 on {host} is open, but the domain controller closed the TLS handshake: it has no server-authentication certificate, so LDAPS cannot start. "
                 + "Install one on the DC (a self-signed certificate naming the DC is enough for a lab) and pin its thumbprint or allow untrusted here; "
                 + $"or use {host}:389 for sync-only reads. Enable-on-create needs the certificate.";
        }
    }
}
