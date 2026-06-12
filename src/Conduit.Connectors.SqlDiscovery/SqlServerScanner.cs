using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace Conduit.Connectors.SqlDiscovery;

/// <summary>One TCP endpoint candidate on a scan target (named instance OR port).</summary>
internal sealed class SqlEndpoint
{
    public string? Instance { get; init; }
    public int? Port { get; init; }

    public string ToDataSource(string host)
    {
        var ds = host;
        if (!string.IsNullOrEmpty(Instance)) ds = string.Concat(ds, "\\", Instance);
        if (Port is { } p) ds = string.Concat(ds, ",", p.ToString(CultureInfo.InvariantCulture));
        return ds;
    }
}

internal enum ScanStatus { Success, AuthFailed, Unreachable, Timeout, Failed }

/// <summary>License-relevant facts read from one live SQL Server instance.</summary>
internal sealed class SqlServerFacts
{
    public string? MachineName { get; init; }
    public string InstanceName { get; init; } = "MSSQLSERVER";
    public string EditionRaw { get; init; } = string.Empty;
    public string Edition { get; init; } = string.Empty;
    public string ProductVersion { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public int? CpuCount { get; init; }
    public int? MemoryGb { get; init; }
    public int? TcpPort { get; init; }
    public string DatabasesJson { get; init; } = "[]";
    public int DatabaseCount { get; init; }
    /// <summary>Null when login collection is disabled or the query failed.</summary>
    public string? LoginsJson { get; init; }
    public int LoginCount { get; init; }
}

internal sealed class ScanOutcome
{
    public ScanStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public SqlServerFacts? Facts { get; init; }
    /// <summary>The endpoint that produced this outcome (the successful one, or the last tried).</summary>
    public SqlEndpoint? Endpoint { get; init; }
}

/// <summary>
/// Connects to a single SQL Server instance and reads SERVERPROPERTY /
/// sys.dm_os_sys_info / sys.databases facts. The connection string is built
/// ONLY via SqlConnectionStringBuilder — discovered hostnames and instance
/// names are untrusted input and are never concatenated into a string.
/// Every failure is classified into a finding (AuthFailed / Unreachable /
/// Timeout / Failed); this type never throws except on cancellation.
/// </summary>
internal static class SqlServerScanner
{
    public static async Task<ScanOutcome> ScanAsync(
        string host,
        IReadOnlyList<SqlEndpoint> endpoints,
        SqlDiscoveryConfig config,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        ScanOutcome? last = null;

        foreach (var endpoint in endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = await ScanEndpointAsync(host, endpoint, config, cancellationToken);
            if (attempt.Status == ScanStatus.Success)
                return attempt;
            last = attempt;
            // Auth failures will not improve on another endpoint of the same host
            // with the same credential — stop probing instead of locking out.
            if (attempt.Status == ScanStatus.AuthFailed)
                break;
        }

        sw.Stop();
        return last ?? new ScanOutcome
        {
            Status = ScanStatus.Failed,
            Message = "No endpoint candidates to scan.",
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static async Task<ScanOutcome> ScanEndpointAsync(
        string host,
        SqlEndpoint endpoint,
        SqlDiscoveryConfig config,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        // Fast TCP pre-probe for port-addressed endpoints (explicit port, or the
        // default instance's 1433). A dead host otherwise costs ~40s while
        // SqlClient walks its TCP→Named Pipes phases regardless of ConnectTimeout
        // — at 1,000 servers that is the difference between a 5-minute and a
        // 40-minute sweep. Named-instance endpoints skip the probe (their port
        // comes from the Browser service at connect time).
        if (string.IsNullOrEmpty(endpoint.Instance))
        {
            var probePort = endpoint.Port ?? 1433;
            var probeError = await ProbeTcpAsync(host, probePort, config.ConnectTimeoutSeconds, cancellationToken);
            if (probeError is not null)
            {
                sw.Stop();
                return new ScanOutcome
                {
                    Status = ScanStatus.Unreachable,
                    Message = $"TCP {probePort} unreachable: {probeError}",
                    DurationMs = sw.ElapsedMilliseconds,
                    Endpoint = endpoint
                };
            }
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = endpoint.ToDataSource(host),
            InitialCatalog = "master",
            ConnectTimeout = config.ConnectTimeoutSeconds,
            TrustServerCertificate = config.TrustServerCertificate,
            // A 1,000-server sweep must not retain a connection pool per host.
            Pooling = false,
            ApplicationName = "Conduit SQL Discovery"
        };
        if (string.Equals(config.AuthType, "WindowsAuth", StringComparison.OrdinalIgnoreCase))
        {
            builder.IntegratedSecurity = true;
            // No password on the wire (Kerberos/NTLM); Optional keeps older
            // lab servers scannable. Servers that enforce encryption still get it.
            builder.Encrypt = SqlConnectionEncryptOption.Optional;
        }
        else
        {
            // SqlAuth sends the scan credential to a DISCOVERED (untrusted) host
            // — TLS is mandatory so a rogue endpoint cannot harvest it in cleartext.
            builder.Encrypt = SqlConnectionEncryptOption.Mandatory;
            builder.UserID = config.SqlUsername ?? string.Empty;
            builder.Password = config.SqlPassword ?? string.Empty;
        }

        try
        {
            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync(cancellationToken);

            var facts = await ReadFactsAsync(conn, endpoint, config.CollectLogins, cancellationToken);
            sw.Stop();
            return new ScanOutcome
            {
                Status = ScanStatus.Success,
                Message = $"{facts.Edition} {facts.ProductVersion}, {(facts.CpuCount?.ToString(CultureInfo.InvariantCulture) ?? "?")} cores, {(facts.MemoryGb?.ToString(CultureInfo.InvariantCulture) ?? "?")} GB, {facts.DatabaseCount} database(s)",
                DurationMs = sw.ElapsedMilliseconds,
                Facts = facts,
                Endpoint = endpoint
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqlException ex)
        {
            sw.Stop();
            return new ScanOutcome
            {
                Status = Classify(ex),
                Message = FirstLine(ex.Message),
                DurationMs = sw.ElapsedMilliseconds,
                Endpoint = endpoint
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ScanOutcome
            {
                Status = ScanStatus.Failed,
                Message = FirstLine(ex.Message),
                DurationMs = sw.ElapsedMilliseconds,
                Endpoint = endpoint
            };
        }
    }

    /// <summary>Returns null when the TCP port answers within the timeout, else a short reason.</summary>
    private static async Task<string?> ProbeTcpAsync(string host, int port, int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            await client.ConnectAsync(host, port, timeoutCts.Token);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return $"no answer within {timeoutSeconds}s";
        }
        catch (Exception ex)
        {
            return FirstLine(ex.Message);
        }
    }

    private static ScanStatus Classify(SqlException ex)
    {
        // 18456/18452/18470: login failed / not associated / account disabled.
        foreach (SqlError err in ex.Errors)
        {
            if (err.Number is 18456 or 18452 or 18470 or 4060)
                return ScanStatus.AuthFailed;
        }
        // -2 is the client-side timeout sentinel.
        if (ex.Number == -2) return ScanStatus.Timeout;
        // 0/53/26/40/10060/10061: network-path / instance-not-found family.
        return ScanStatus.Unreachable;
    }

    private static string FirstLine(string message)
    {
        var idx = message.IndexOf('\n');
        var line = idx >= 0 ? message[..idx] : message;
        return line.Trim();
    }

    private static async Task<SqlServerFacts> ReadFactsAsync(SqlConnection conn, SqlEndpoint endpoint, bool collectLogins, CancellationToken ct)
    {
        string? machineName = null, instanceName = null, editionRaw = null, productVersion = null, versionBanner = null;

        const string serverPropsSql = @"
SELECT CONVERT(nvarchar(128), SERVERPROPERTY('MachineName'))     AS MachineName,
       CONVERT(nvarchar(128), SERVERPROPERTY('InstanceName'))    AS InstanceName,
       CONVERT(nvarchar(128), SERVERPROPERTY('Edition'))         AS Edition,
       CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'))  AS ProductVersion,
       @@VERSION                                                 AS VersionBanner;";
        await using (var cmd = new SqlCommand(serverPropsSql, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                machineName = reader.IsDBNull(0) ? null : reader.GetString(0);
                instanceName = reader.IsDBNull(1) ? null : reader.GetString(1);
                editionRaw = reader.IsDBNull(2) ? null : reader.GetString(2);
                productVersion = reader.IsDBNull(3) ? null : reader.GetString(3);
                versionBanner = reader.IsDBNull(4) ? null : reader.GetString(4);
            }
        }

        int? cpuCount = null, memoryGb = null;
        try
        {
            const string sysInfoSql =
                "SELECT cpu_count, physical_memory_kb FROM sys.dm_os_sys_info;";
            await using var cmd = new SqlCommand(sysInfoSql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                cpuCount = reader.IsDBNull(0) ? null : reader.GetInt32(0);
                if (!reader.IsDBNull(1))
                    memoryGb = (int)Math.Max(1, Math.Round(reader.GetInt64(1) / 1048576.0));
            }
        }
        catch (SqlException)
        {
            // Pre-2012 servers expose physical_memory_in_bytes instead.
            try
            {
                const string legacySql =
                    "SELECT cpu_count, physical_memory_in_bytes FROM sys.dm_os_sys_info;";
                await using var cmd = new SqlCommand(legacySql, conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    cpuCount = reader.IsDBNull(0) ? null : reader.GetInt32(0);
                    if (!reader.IsDBNull(1))
                        memoryGb = (int)Math.Max(1, Math.Round(reader.GetInt64(1) / 1073741824.0));
                }
            }
            catch (SqlException) { /* findings without sizing are still findings */ }
        }

        int? tcpPort = endpoint.Port;
        try
        {
            const string portSql =
                "SELECT TOP 1 local_tcp_port FROM sys.dm_exec_connections WHERE session_id = @@SPID;";
            await using var cmd = new SqlCommand(portSql, conn);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is int p && p > 0) tcpPort = p;
        }
        catch (SqlException) { /* keep the configured/default port */ }

        var databases = new List<object>();
        var dbCount = 0;
        try
        {
            const string dbSql = @"
SELECT d.name,
       d.recovery_model_desc,
       d.compatibility_level,
       d.state_desc,
       CASE WHEN d.database_id <= 4 THEN 1 ELSE 0 END AS is_system,
       CAST(SUM(CAST(mf.size AS bigint)) * 8.0 / 1048576.0 AS float) AS size_gb
FROM sys.databases d
LEFT JOIN sys.master_files mf ON mf.database_id = d.database_id
GROUP BY d.name, d.recovery_model_desc, d.compatibility_level, d.state_desc, d.database_id
ORDER BY d.name;";
            await using var cmd = new SqlCommand(dbSql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                dbCount++;
                databases.Add(new
                {
                    name = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    sizeGb = reader.IsDBNull(5) ? 0.0 : Math.Round(reader.GetDouble(5), 2),
                    recoveryModel = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    compatibilityLevel = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                    isSystemDb = !reader.IsDBNull(4) && Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture) == 1,
                    state = reader.IsDBNull(3) ? string.Empty : reader.GetString(3)
                });
            }
        }
        catch (SqlException) { /* database inventory is best-effort */ }

        // Server-level logins for the licensing "who can access" picture. Names,
        // type, and enabled state ONLY — never sids or password hashes. The
        // ##MS_*## certificate-mapped service principals are noise, not access.
        string? loginsJson = null;
        var loginCount = 0;
        if (collectLogins)
        {
            try
            {
                const string loginSql = @"
SELECT name, type_desc, is_disabled
FROM sys.server_principals
WHERE type IN ('S','U','G')
  AND name NOT LIKE '##%##'
ORDER BY name;";
                var logins = new List<object>();
                await using var cmd = new SqlCommand(loginSql, conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    loginCount++;
                    logins.Add(new
                    {
                        name = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                        type = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        isDisabled = !reader.IsDBNull(2) && reader.GetBoolean(2)
                    });
                }
                loginsJson = System.Text.Json.JsonSerializer.Serialize(logins);
            }
            catch (SqlException) { /* login inventory is best-effort */ }
        }

        return new SqlServerFacts
        {
            MachineName = machineName,
            InstanceName = string.IsNullOrWhiteSpace(instanceName) ? "MSSQLSERVER" : instanceName!,
            EditionRaw = editionRaw ?? string.Empty,
            Edition = NormalizeEdition(editionRaw),
            ProductVersion = productVersion ?? string.Empty,
            OperatingSystem = ParseOperatingSystem(versionBanner),
            CpuCount = cpuCount,
            MemoryGb = memoryGb,
            TcpPort = tcpPort,
            DatabasesJson = System.Text.Json.JsonSerializer.Serialize(databases),
            DatabaseCount = dbCount,
            LoginsJson = loginsJson,
            LoginCount = loginCount
        };
    }

    /// <summary>
    /// IC's cost model exact-matches the bare edition token: "Enterprise Edition:
    /// Core-based Licensing (64-bit)" must come out as "Enterprise".
    /// </summary>
    internal static string NormalizeEdition(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var trimmed = raw.Trim();
        foreach (var known in new[] { "Enterprise", "Standard", "Developer", "Express", "Web", "Workgroup", "Datacenter", "Business Intelligence", "Azure" })
        {
            if (trimmed.StartsWith(known, StringComparison.OrdinalIgnoreCase))
                return known;
        }
        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }

    /// <summary>
    /// Extracts the OS from the @@VERSION banner ("... on Windows Server 2022
    /// Standard 10.0 &lt;X64&gt; ..."). IC keys on the word "Server" being present,
    /// so non-matching or unparseable banners fall back to a Server-containing
    /// placeholder, and non-Windows results get an explicit "(SQL Server host)"
    /// suffix.
    /// </summary>
    internal static string ParseOperatingSystem(string? versionBanner)
    {
        const string fallback = "Windows Server (SQL host)";
        if (string.IsNullOrWhiteSpace(versionBanner)) return fallback;

        var idx = versionBanner.LastIndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return fallback;

        var os = versionBanner[(idx + 4)..].Trim();
        var newline = os.IndexOf('\n');
        if (newline >= 0) os = os[..newline].Trim();
        var angle = os.IndexOf('<');
        if (angle > 0) os = os[..angle].Trim();
        var build = os.IndexOf("(Build", StringComparison.OrdinalIgnoreCase);
        if (build > 0) os = os[..build].Trim();

        if (os.Length == 0) return fallback;
        return os.Contains("Server", StringComparison.OrdinalIgnoreCase)
            ? os
            : string.Concat(os, " (SQL Server host)");
    }
}
