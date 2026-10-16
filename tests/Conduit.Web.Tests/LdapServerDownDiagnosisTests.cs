using System.Net;
using System.Net.Sockets;
using Conduit.Connectors.ActiveDirectory;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// "The LDAP server is unavailable." covers three different causes. Jacob moved the lab AD connection to
/// :636 on 2026-10-16 and read it as the server going away; the DC had simply no server certificate
/// (TCP accepted, TLS handshake reset). The connection test now says which it is.
/// </summary>
public class LdapServerDownDiagnosisTests
{
    [Fact]
    public async Task A_closed_port_says_nothing_listens()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var why = await LdapServerDownDiagnosis.DescribeAsync("127.0.0.1", port, expectTls: true);
        Assert.Contains("Nothing listens on 127.0.0.1:" + port, why);
    }

    [Fact]
    public async Task An_open_port_that_resets_the_handshake_names_the_missing_certificate()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var buffer = new byte[1];
            try { await socket.GetStream().ReadAsync(buffer, 0, 1); } catch { }
        });
        try
        {
            var why = await LdapServerDownDiagnosis.DescribeAsync("127.0.0.1", port, expectTls: true);
            Assert.Contains("closed the TLS handshake", why);
            Assert.Contains("no server-authentication certificate", why);
            Assert.Contains("127.0.0.1:389", why);
        }
        finally
        {
            listener.Stop();
            await accept;
        }
    }

    [Fact]
    public async Task A_plain_port_that_accepts_but_never_binds_says_so()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var why = await LdapServerDownDiagnosis.DescribeAsync("127.0.0.1", port, expectTls: false);
            Assert.Contains("did not answer the LDAP bind", why);
        }
        finally { listener.Stop(); }
    }
}
