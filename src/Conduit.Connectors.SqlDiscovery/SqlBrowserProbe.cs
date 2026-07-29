using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit.Connectors.SqlDiscovery;

/// <summary>One instance reported by the SQL Server Browser on a host.</summary>
public sealed record SqlBrowserInstance(
    string ServerName,
    string InstanceName,
    int? TcpPort,
    string? Version,
    bool IsClustered);

/// <summary>
/// Enumerates SQL Server instances on a host via the SQL Server Browser (UDP/1434).
/// This is how you find NAMED instances and their DYNAMIC ports — the ones a bare
/// TCP/1433 scan is blind to, because a named instance listens on an ephemeral port
/// the Browser hands out. A single 0x02 datagram asks "list every instance here"; the
/// Browser answers with a semicolon-delimited catalogue.
///
/// Protocol (MS-SQLR): client sends one byte 0x02 (CLNT_UCAST_EX) to UDP 1434; server
/// replies 0x05, a 2-byte little-endian length, then the ASCII catalogue:
///   ServerName;HOST;InstanceName;MSSQLSERVER;IsClustered;No;Version;15.0.2000.5;tcp;1433;;InstanceName;SQL2;...
/// Instances are separated by ";;". The wire round-trip needs a live Browser and open
/// UDP/1434; the fragile part — parsing that catalogue — is a pure, unit-tested function.
/// </summary>
public static class SqlBrowserProbe
{
    private const int BrowserPort = 1434;
    private const byte ClntUcastEx = 0x02;
    private const byte SvrResp = 0x05;

    /// <summary>
    /// Sends the enumerate-all datagram to <paramref name="host"/> and parses the reply.
    /// Returns an empty list on timeout / no Browser / closed UDP — a host with no Browser
    /// is a normal, non-exceptional outcome.
    /// </summary>
    public static async Task<IReadOnlyList<SqlBrowserInstance>> QueryAsync(
        string host, int timeoutMs, CancellationToken ct)
    {
        using var udp = new UdpClient();
        try
        {
            udp.Client.ReceiveTimeout = timeoutMs;
            await udp.SendAsync(new[] { ClntUcastEx }, 1, host, BrowserPort).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Array.Empty<SqlBrowserInstance>(); // no Browser answered in time
            }

            return ParseResponse(result.Buffer, host);
        }
        catch (SocketException)
        {
            return Array.Empty<SqlBrowserInstance>();
        }
    }

    /// <summary>Parses a raw Browser datagram (0x05, 2-byte length, ASCII catalogue).</summary>
    public static IReadOnlyList<SqlBrowserInstance> ParseResponse(byte[] datagram, string fallbackHost)
    {
        if (datagram is null || datagram.Length < 3 || datagram[0] != SvrResp)
            return Array.Empty<SqlBrowserInstance>();

        // Bytes 1-2 are a little-endian length; the catalogue follows. Be lenient: if the
        // declared length overruns the buffer, take whatever actually arrived.
        var declared = datagram[1] | (datagram[2] << 8);
        var available = datagram.Length - 3;
        var take = Math.Min(declared, available);
        if (take <= 0) return Array.Empty<SqlBrowserInstance>();

        var catalogue = Encoding.ASCII.GetString(datagram, 3, take);
        return Parse(catalogue, fallbackHost);
    }

    /// <summary>
    /// Parses the semicolon-delimited catalogue string into instances. Public and pure so
    /// the token handling is unit-tested without any socket.
    /// </summary>
    public static IReadOnlyList<SqlBrowserInstance> Parse(string catalogue, string fallbackHost)
    {
        var instances = new List<SqlBrowserInstance>();
        if (string.IsNullOrWhiteSpace(catalogue)) return instances;

        // ";;" is the documented separator between instance records; within a record the
        // fields are "key;value;key;value;...". Splitting on ";;" first, then walking pairs,
        // avoids any dependence on token parity across the record boundary.
        var records = catalogue.Split(new[] { ";;" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var record in records)
        {
            var t = record.Split(';');
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < t.Length; i += 2)
            {
                if (!string.IsNullOrEmpty(t[i]))
                    fields[t[i]] = t[i + 1];
            }
            if (fields.Count == 0) continue;

            var server = fields.GetValueOrDefault("ServerName", fallbackHost);
            var inst = fields.GetValueOrDefault("InstanceName", "MSSQLSERVER");
            int? port = int.TryParse(fields.GetValueOrDefault("tcp"), out var p) ? p : null;
            var version = fields.GetValueOrDefault("Version");
            var clustered = string.Equals(fields.GetValueOrDefault("IsClustered"), "Yes", StringComparison.OrdinalIgnoreCase);
            instances.Add(new SqlBrowserInstance(server, inst, port, version, clustered));
        }

        return instances;
    }
}
