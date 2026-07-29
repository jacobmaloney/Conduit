using System.Text;
using Conduit.Connectors.SqlDiscovery;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// Locks the SQL Server Browser (UDP/1434) catalogue parser — the fragile part of finding
/// named instances and their dynamic ports that a TCP/1433 scan cannot see.
/// </summary>
public class SqlBrowserProbeTests
{
    [Fact]
    public void Parses_a_single_default_instance()
    {
        const string cat = "ServerName;WINHOST;InstanceName;MSSQLSERVER;IsClustered;No;Version;15.0.2000.5;tcp;1433;;";
        var list = SqlBrowserProbe.Parse(cat, "fallback");

        var i = Assert.Single(list);
        Assert.Equal("WINHOST", i.ServerName);
        Assert.Equal("MSSQLSERVER", i.InstanceName);
        Assert.Equal(1433, i.TcpPort);
        Assert.Equal("15.0.2000.5", i.Version);
        Assert.False(i.IsClustered);
    }

    [Fact]
    public void Parses_multiple_instances_including_a_dynamic_port()
    {
        // A default instance on 1433 plus a NAMED instance on a dynamic port 49812 —
        // the named one is exactly what a 1433-only scan misses.
        const string cat =
            "ServerName;WINHOST;InstanceName;MSSQLSERVER;IsClustered;No;Version;15.0.2000.5;tcp;1433;np;\\\\WINHOST\\pipe\\sql\\query;;" +
            "ServerName;WINHOST;InstanceName;SQLEXPRESS;IsClustered;No;Version;15.0.2000.5;tcp;49812;;";
        var list = SqlBrowserProbe.Parse(cat, "fallback");

        Assert.Equal(2, list.Count);
        Assert.Equal("MSSQLSERVER", list[0].InstanceName);
        Assert.Equal(1433, list[0].TcpPort);
        Assert.Equal("SQLEXPRESS", list[1].InstanceName);
        Assert.Equal(49812, list[1].TcpPort);
    }

    [Fact]
    public void Detects_clustered_flag()
    {
        const string cat = "ServerName;CLUS;InstanceName;PROD;IsClustered;Yes;Version;16.0.1000.6;tcp;1433;;";
        var i = Assert.Single(SqlBrowserProbe.Parse(cat, "fallback"));
        Assert.True(i.IsClustered);
    }

    [Fact]
    public void Named_pipe_only_instance_has_no_tcp_port()
    {
        const string cat = "ServerName;OLDBOX;InstanceName;NPONLY;IsClustered;No;Version;10.50.6000.34;np;\\\\OLDBOX\\pipe\\sql;;";
        var i = Assert.Single(SqlBrowserProbe.Parse(cat, "fallback"));
        Assert.Null(i.TcpPort);
        Assert.Equal("NPONLY", i.InstanceName);
    }

    [Fact]
    public void Empty_or_garbage_catalogue_yields_nothing()
    {
        Assert.Empty(SqlBrowserProbe.Parse("", "fallback"));
        Assert.Empty(SqlBrowserProbe.Parse("   ", "fallback"));
    }

    [Fact]
    public void ParseResponse_requires_the_svr_resp_header()
    {
        // No 0x05 header → not a Browser reply → nothing.
        Assert.Empty(SqlBrowserProbe.ParseResponse(new byte[] { 0x00, 0x01, 0x02 }, "h"));
        Assert.Empty(SqlBrowserProbe.ParseResponse(System.Array.Empty<byte>(), "h"));
    }

    [Fact]
    public void ParseResponse_reads_header_length_then_catalogue()
    {
        const string cat = "ServerName;H;InstanceName;MSSQLSERVER;IsClustered;No;Version;15.0.2000.5;tcp;1433;;";
        var body = Encoding.ASCII.GetBytes(cat);
        var datagram = new byte[3 + body.Length];
        datagram[0] = 0x05;
        datagram[1] = (byte)(body.Length & 0xFF);
        datagram[2] = (byte)((body.Length >> 8) & 0xFF);
        System.Array.Copy(body, 0, datagram, 3, body.Length);

        var i = Assert.Single(SqlBrowserProbe.ParseResponse(datagram, "h"));
        Assert.Equal("MSSQLSERVER", i.InstanceName);
        Assert.Equal(1433, i.TcpPort);
    }
}
