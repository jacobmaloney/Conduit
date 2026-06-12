using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Conduit.Core.SyncModels;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Sync.Orchestration;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.SqlDiscovery;

/// <summary>
/// SQL Discovery source. Enumerates SQL Server hosts (AD MSSQLSvc/* SPNs and/or
/// a manual instance list), scans each with bounded parallelism, and emits ONE
/// "computer" ConnectorObject per successfully scanned host carrying the
/// license-relevant facts IC's License Center keys on (sqlServerEdition,
/// cpuCores, sqlDatabasesJson, servicePrincipalName, ...).
///
/// Resilience contract: per-server try/catch isolation — one dead server NEVER
/// kills the run. Failures are FINDINGS, logged per server to SyncRunLogs via
/// <see cref="SourceRunLogContext"/>, and the run completes with partial
/// results. This source NEVER emits deletions and never reports a complete
/// read (a failed scan is indistinguishable from a decommission), and its
/// adapter additionally declares SuppressDeleteDetection so the orchestrator's
/// diff-based tombstoning is disabled by construction.
///
/// Identity convention:
///   - SPN-enumerated domain hosts: SourceId = AD objectGUID and
///     _sourceConnection = the AD connection's name — the SAME (Source,
///     SourceUniqueId) pair the existing AD-sync-to-IC path stamps, so the scan
///     ENRICHES the already-synced computer object instead of duplicating it.
///   - Manual instance-list hosts: SourceId = "sqldisc:&lt;host&gt;:&lt;port&gt;[\instance]"
///     (stable) and _sourceConnection = the configured discovery source name
///     (default "SQLDiscovery"; auto-seeds a connection IC-side).
///
/// IMPORTANT: SQL Discovery projects should keep their Mapping step EMPTY
/// (zero attribute mappings = pass-through). The emission contract keys are
/// fixed; mapping rows would drop the per-object _sourceConnection stamp.
/// </summary>
public sealed class SqlDiscoverySource : IConnectorSource
{
    private readonly Guid _tenantId;
    private readonly TenantRepository _tenantRepo;
    private readonly CredentialProtector _protector;
    private readonly ILogger<SqlDiscoverySource> _logger;

    public SqlDiscoverySource(
        Guid tenantId,
        TenantRepository tenantRepo,
        CredentialProtector protector,
        ILogger<SqlDiscoverySource> logger)
    {
        _tenantId = tenantId;
        _tenantRepo = tenantRepo;
        _protector = protector;
        _logger = logger;
    }

    /// <summary>One enumerated host to scan: identity + endpoint candidates.</summary>
    private sealed class ScanTarget
    {
        public string Host { get; init; } = string.Empty;
        public string ShortName { get; init; } = string.Empty;
        public string SourceId { get; init; } = string.Empty;
        public string SourceConnection { get; init; } = string.Empty;
        public bool FromSpn { get; init; }
        public List<SqlEndpoint> Endpoints { get; init; } = new();
        public string Label => Endpoints.Count > 0 ? Endpoints[0].ToDataSource(Host) : Host;
    }

    private sealed class TargetOutcome
    {
        public ScanTarget Target { get; init; } = null!;
        public ScanOutcome Outcome { get; init; } = null!;
    }

    public async IAsyncEnumerable<ConnectorObject> ReadAsync(
        string objectClass,
        SyncProjectScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!string.Equals(objectClass, "computer", StringComparison.OrdinalIgnoreCase))
        {
            await SourceRunLogContext.LogAsync("Warning",
                $"SQL Discovery only emits objectClass 'computer'; step class '{objectClass}' produces nothing.");
            yield break;
        }

        var config = await SqlDiscoveryConfigReader.ReadAsync(_protector, _tenantId)
            ?? throw new InvalidOperationException(
                $"No 'sqldiscovery' credential stored for tenant {_tenantId}. Configure the SQL Discovery connection before running.");

        var targets = await BuildTargetsAsync(config, cancellationToken);
        await SourceRunLogContext.LogAsync("Info",
            $"SQL Discovery: {targets.Count} target host(s) enumerated (mode={config.Mode}, parallelism={config.Parallelism}, connectTimeout={config.ConnectTimeoutSeconds}s).");
        if (targets.Count == 0)
            yield break;

        // Bounded-parallel scan pump. Workers scan; this (single) consumer logs
        // each per-server outcome to SyncRunLogs and yields the successes. One
        // dead server is one Warning finding — never a run failure.
        var channel = Channel.CreateUnbounded<TargetOutcome>(
            new UnboundedChannelOptions { SingleReader = true });
        var semaphore = new SemaphoreSlim(config.Parallelism);

        var workers = targets.Select(async target =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                ScanOutcome outcome;
                try
                {
                    outcome = await SqlServerScanner.ScanAsync(target.Host, target.Endpoints, config, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    outcome = new ScanOutcome { Status = ScanStatus.Failed, Message = ex.Message };
                }
                await channel.Writer.WriteAsync(new TargetOutcome { Target = target, Outcome = outcome }, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        _ = Task.WhenAll(workers).ContinueWith(
            t => channel.Writer.TryComplete(t.IsFaulted ? t.Exception!.GetBaseException() : null),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        int ok = 0, failedFindings = 0;
        await foreach (var result in channel.Reader.ReadAllAsync(cancellationToken))
        {
            var target = result.Target;
            var outcome = result.Outcome;

            if (outcome.Status == ScanStatus.Success && outcome.Facts is not null)
            {
                ok++;
                await SourceRunLogContext.LogAsync("Info",
                    $"SQL Discovery: {target.Label} → Success ({outcome.Message}) [{outcome.DurationMs} ms]");
                yield return await ToConnectorObjectAsync(target, outcome, cancellationToken);
            }
            else
            {
                failedFindings++;
                await SourceRunLogContext.LogAsync("Warning",
                    $"SQL Discovery: {target.Label} → {outcome.Status}: {outcome.Message} [{outcome.DurationMs} ms]");
                // A failed attempt is still EVIDENCE: emit the scan-status stamp so
                // IC can surface the unreachable host instead of silently keeping
                // last-good (or no) data. Never carries edition/version/cores and
                // never touches sqlLastScannedAt — that keeps its last-GOOD meaning.
                yield return ToFailureConnectorObject(target, outcome);
            }
        }

        await SourceRunLogContext.LogAsync("Info",
            $"SQL Discovery: sweep finished. {ok} scanned, {failedFindings} unreachable/failed finding(s) of {targets.Count} target(s). Failed scans are findings, never deletions.");
    }

    // EnumerateAsync deliberately NOT overridden: the IConnectorSource default
    // wraps ReadAsync with WasCompleteRead = false (the safe default), which is
    // exactly right here — this source must never green-light delete detection.

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = await SqlDiscoveryConfigReader.ReadAsync(_protector, _tenantId);
            if (config is null)
                return new ConnectorTestResult { IsSuccessful = false, Message = "No 'sqldiscovery' credential stored." };

            var parts = new List<string>();

            if (config.Mode is SqlDiscoveryMode.Spn or SqlDiscoveryMode.Both)
            {
                var (adTenant, creds, error) = await ResolveAdConnectionAsync(config);
                if (error is not null)
                    return new ConnectorTestResult { IsSuccessful = false, Message = error };
                var spnHosts = await EnumerateSpnHostsAsync(adTenant!, creds!, cancellationToken);
                parts.Add($"SPN enumeration via '{adTenant!.Name}': {spnHosts.Count} SQL host(s) found");
            }

            if (config.Mode is SqlDiscoveryMode.InstanceList or SqlDiscoveryMode.Both)
            {
                if (config.InstanceList.Count == 0)
                {
                    parts.Add("instance list is empty");
                }
                else
                {
                    var first = config.InstanceList[0];
                    var endpoint = new SqlEndpoint { Instance = first.Instance, Port = first.Port };
                    var probe = await SqlServerScanner.ScanAsync(first.Host, new[] { endpoint }, config, cancellationToken);
                    parts.Add(probe.Status == ScanStatus.Success
                        ? $"probe {endpoint.ToDataSource(first.Host)} OK ({probe.Message})"
                        : $"probe {endpoint.ToDataSource(first.Host)} → {probe.Status}: {probe.Message}");
                    parts.Add($"{config.InstanceList.Count} manual instance(s) configured");
                }
            }

            return new ConnectorTestResult { IsSuccessful = true, Message = string.Join("; ", parts) + "." };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

    // ─── Target enumeration ─────────────────────────────────────────────────

    private async Task<List<ScanTarget>> BuildTargetsAsync(SqlDiscoveryConfig config, CancellationToken ct)
    {
        var targets = new List<ScanTarget>();
        var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (config.Mode is SqlDiscoveryMode.Spn or SqlDiscoveryMode.Both)
        {
            var (adTenant, creds, error) = await ResolveAdConnectionAsync(config);
            if (error is not null)
            {
                // SPN enumeration trouble is a finding, not a run-killer — the
                // instance list (if any) still gets scanned.
                await SourceRunLogContext.LogAsync("Warning", $"SQL Discovery: SPN enumeration skipped — {error}");
                _logger.LogWarning("SQL Discovery SPN enumeration skipped for tenant {TenantId}: {Error}", _tenantId, error);
            }
            else
            {
                try
                {
                    var spnHosts = await EnumerateSpnHostsAsync(adTenant!, creds!, ct);
                    foreach (var host in spnHosts)
                    {
                        if (seenHosts.Add(host.Host))
                            targets.Add(host);
                    }
                    await SourceRunLogContext.LogAsync("Info",
                        $"SQL Discovery: SPN enumeration via '{adTenant!.Name}' found {spnHosts.Count} SQL host(s).");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    await SourceRunLogContext.LogAsync("Warning",
                        $"SQL Discovery: SPN enumeration failed ({ex.Message}) — continuing with the manual instance list.");
                    _logger.LogWarning(ex, "SQL Discovery SPN enumeration failed for tenant {TenantId}", _tenantId);
                }
            }
        }

        if (config.Mode is SqlDiscoveryMode.InstanceList or SqlDiscoveryMode.Both)
        {
            foreach (var entry in config.InstanceList)
            {
                // SPN identity wins when the same host appears in both modes —
                // the AD objectGUID identity is the one that enriches in IC.
                if (!seenHosts.Add(entry.Host)) continue;

                var port = entry.Port;
                var idPort = port ?? 1433;
                var sourceId = string.IsNullOrEmpty(entry.Instance)
                    ? $"sqldisc:{entry.Host.ToLowerInvariant()}:{idPort}"
                    : $"sqldisc:{entry.Host.ToLowerInvariant()}:{idPort}\\{entry.Instance.ToLowerInvariant()}";

                targets.Add(new ScanTarget
                {
                    Host = entry.Host,
                    ShortName = ShortHostName(entry.Host),
                    SourceId = sourceId,
                    SourceConnection = config.DiscoverySourceName,
                    FromSpn = false,
                    Endpoints = { new SqlEndpoint { Instance = entry.Instance, Port = port } }
                });
            }
        }

        return targets;
    }

    private async Task<(Core.Models.Tenant? Tenant, AdCredentials? Creds, string? Error)> ResolveAdConnectionAsync(SqlDiscoveryConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.AdConnectionName))
            return (null, null, "no AD connection name configured (set 'AD connection name' on the SQL Discovery connection).");

        var tenants = await _tenantRepo.GetAllAsync(includeInactive: true);
        var adTenant = tenants.FirstOrDefault(t =>
            string.Equals(t.Name, config.AdConnectionName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.SystemType, "ActiveDirectory", StringComparison.OrdinalIgnoreCase));
        if (adTenant is null)
            return (null, null, $"no Active Directory connection named '{config.AdConnectionName}' exists.");
        if (string.IsNullOrWhiteSpace(adTenant.Domain))
            return (null, null, $"AD connection '{adTenant.Name}' has no Domain (LDAP host) configured.");

        var raw = await _protector.RetrieveAsync(adTenant.Id, "ldap");
        if (string.IsNullOrEmpty(raw))
            return (null, null, $"AD connection '{adTenant.Name}' has no stored 'ldap' credential.");

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var u = doc.RootElement.TryGetProperty("Username", out var uEl) ? uEl.GetString() : null;
            var p = doc.RootElement.TryGetProperty("Password", out var pEl) ? pEl.GetString() : null;
            if (string.IsNullOrEmpty(u) || p is null)
                return (null, null, $"AD connection '{adTenant.Name}' 'ldap' credential is missing Username/Password.");
            return (adTenant, new AdCredentials(u, p), null);
        }
        catch (JsonException)
        {
            return (null, null, $"AD connection '{adTenant.Name}' 'ldap' credential blob is malformed.");
        }
    }

    private sealed record AdCredentials(string Username, string Password);

    /// <summary>
    /// LDAP SPN sweep: (servicePrincipalName=MSSQLSvc/*) over computer accounts,
    /// paged, with attribute projection and ReferralChasingOptions.None — chasing
    /// referrals made AD reads take minutes (see the 20cf600 perf lesson).
    /// </summary>
    private Task<List<ScanTarget>> EnumerateSpnHostsAsync(
        Core.Models.Tenant adTenant,
        AdCredentials creds,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var (host, port) = ParseHostPort(adTenant.Domain!);
            using var connection = CreateBoundConnection(host, port, creds);

            var baseDn = ResolveDefaultNamingContext(connection)
                ?? throw new InvalidOperationException(
                    $"AD '{adTenant.Name}' did not advertise a defaultNamingContext on RootDSE.");

            const string filter = "(&(objectCategory=computer)(servicePrincipalName=MSSQLSvc/*))";
            var attrs = new[] { "objectGUID", "dNSHostName", "cn", "servicePrincipalName" };

            var results = new List<ScanTarget>();
            var pageControl = new PageResultRequestControl(500);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var request = new SearchRequest(baseDn, filter, SearchScope.Subtree, attrs);
                request.Controls.Add(pageControl);
                var response = (SearchResponse)connection.SendRequest(request);

                foreach (SearchResultEntry entry in response.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var target = SpnEntryToTarget(entry, adTenant.Name);
                    if (target is not null) results.Add(target);
                }

                var responseControl = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                if (responseControl is null || responseControl.Cookie.Length == 0) break;
                pageControl.Cookie = responseControl.Cookie;
            }
            return results;
        }, ct);
    }

    private static ScanTarget? SpnEntryToTarget(SearchResultEntry entry, string adConnectionName)
    {
        string? sourceId = null;
        if (entry.Attributes.Contains("objectGUID")
            && entry.Attributes["objectGUID"][0] is byte[] bytes && bytes.Length == 16)
        {
            sourceId = new Guid(bytes).ToString();
        }
        if (string.IsNullOrEmpty(sourceId)) return null;

        var dnsHostName = entry.Attributes.Contains("dNSHostName")
            ? entry.Attributes["dNSHostName"][0]?.ToString()
            : null;
        var cn = entry.Attributes.Contains("cn")
            ? entry.Attributes["cn"][0]?.ToString()
            : null;
        var hostForScan = !string.IsNullOrWhiteSpace(dnsHostName) ? dnsHostName! : cn;
        if (string.IsNullOrWhiteSpace(hostForScan)) return null;

        // Parse every MSSQLSvc SPN into endpoint candidates. Numeric suffix =
        // TCP port; non-numeric suffix = named instance; no suffix = default.
        var ports = new List<int>();
        var instances = new List<string>();
        if (entry.Attributes.Contains("servicePrincipalName"))
        {
            var spnAttr = entry.Attributes["servicePrincipalName"];
            for (var i = 0; i < spnAttr.Count; i++)
            {
                var spn = spnAttr[i]?.ToString();
                if (spn is null || !spn.StartsWith("MSSQLSvc/", StringComparison.OrdinalIgnoreCase)) continue;
                var rest = spn["MSSQLSvc/".Length..];
                var colon = rest.LastIndexOf(':');
                if (colon < 0) continue;
                var suffix = rest[(colon + 1)..].Trim();
                if (suffix.Length == 0) continue;
                if (int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536)
                {
                    if (!ports.Contains(p)) ports.Add(p);
                }
                else if (!instances.Contains(suffix, StringComparer.OrdinalIgnoreCase))
                {
                    instances.Add(suffix);
                }
            }
        }

        // Candidate order: explicit ports first (1433 leading), then named
        // instances, then a bare default. First successful endpoint wins; one
        // object is emitted per HOST.
        var endpoints = new List<SqlEndpoint>();
        foreach (var p in ports.OrderBy(p => p == 1433 ? 0 : 1).ThenBy(p => p))
            endpoints.Add(new SqlEndpoint { Port = p });
        foreach (var inst in instances)
            endpoints.Add(new SqlEndpoint { Instance = inst });
        if (endpoints.Count == 0)
            endpoints.Add(new SqlEndpoint());

        return new ScanTarget
        {
            Host = hostForScan,
            ShortName = !string.IsNullOrWhiteSpace(cn) ? cn! : ShortHostName(hostForScan),
            SourceId = sourceId,
            SourceConnection = adConnectionName,
            FromSpn = true,
            Endpoints = endpoints
        };
    }

    // ─── Emission ───────────────────────────────────────────────────────────

    private static async Task<ConnectorObject> ToConnectorObjectAsync(ScanTarget target, ScanOutcome outcome, CancellationToken ct)
    {
        var facts = outcome.Facts!;
        var hostIsIp = IPAddress.TryParse(target.Host, out _);
        var shortName = !string.IsNullOrWhiteSpace(facts.MachineName)
            ? facts.MachineName!
            : target.ShortName;
        var fqdn = hostIsIp
            ? (facts.MachineName ?? target.Host)
            : target.Host;
        var port = facts.TcpPort ?? outcome.Endpoint?.Port ?? 1433;
        var ip = hostIsIp ? target.Host : await ResolveIpv4Async(target.Host);

        var nowUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["CN"] = shortName,
            ["DisplayName"] = fqdn,
            ["IsActive"] = "true",
            // The LIKE '%MSSQLSvc%' trigger IC's SQL Servers page and License
            // Center Infrastructure tab key on — emitted for instance-list hosts too.
            ["servicePrincipalName"] = $"MSSQLSvc/{fqdn}:{port.ToString(CultureInfo.InvariantCulture)}",
            ["sqlServerEdition"] = facts.Edition,
            ["sqlServerVersion"] = facts.ProductVersion,
            ["sqlInstanceName"] = facts.InstanceName,
            ["sqlServerPort"] = port.ToString(CultureInfo.InvariantCulture),
            ["operatingSystem"] = facts.OperatingSystem,
            ["dNSHostName"] = fqdn,
            ["sqlDatabasesJson"] = facts.DatabasesJson,
            // sqlLastScannedAt updates ONLY on success — "when did we last get
            // good data". sqlLastScanAttemptAt updates on EVERY attempt.
            ["sqlLastScannedAt"] = nowUtc,
            ["sqlScanStatus"] = "Success",
            ["sqlLastScanAttemptAt"] = nowUtc,
            // Clears any sqlScanError a previous failed sweep stamped.
            ["sqlScanError"] = string.Empty,
            // Internal routing stamp, lifted out by the IC sink (never written as
            // a real attribute): AD connection name for SPN hosts (enrich the
            // existing computer object), discovery source name for manual hosts.
            ["_sourceConnection"] = target.SourceConnection
        };
        if (facts.CpuCount is { } cpu)
            attrs["cpuCores"] = cpu.ToString(CultureInfo.InvariantCulture);
        if (facts.MemoryGb is { } mem)
            attrs["memoryGB"] = mem.ToString(CultureInfo.InvariantCulture);
        if (facts.LoginsJson is not null)
            attrs["sqlLoginsJson"] = facts.LoginsJson;
        if (!string.IsNullOrEmpty(ip))
            attrs["ipHostNumber"] = ip;

        return new ConnectorObject
        {
            SourceId = target.SourceId,
            ObjectClass = "computer",
            Attributes = attrs
        };
    }

    /// <summary>
    /// Failed-attempt emission: the three scan-status attributes plus just enough
    /// identity for the upsert. CN/DisplayName are included (same clobber surface
    /// as the success path — IC's bulk UPDATE overwrites typed columns
    /// unconditionally, so omitting them would NULL them on an existing object).
    /// Deliberately NO edition/version/cores/databases/logins and NO
    /// sqlLastScannedAt — last-good data in IC stays intact.
    /// </summary>
    private static ConnectorObject ToFailureConnectorObject(ScanTarget target, ScanOutcome outcome)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["CN"] = target.ShortName,
            ["DisplayName"] = target.Host,
            ["sqlScanStatus"] = ToScanStatusToken(outcome.Status),
            ["sqlLastScanAttemptAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["sqlScanError"] = SanitizeScanError(outcome.Message),
            ["_sourceConnection"] = target.SourceConnection
        };

        if (!target.FromSpn)
        {
            // Manual instance-list host: may have no prior IC row, so carry the
            // host/port shape a scanned row would have (minus all scan facts).
            var port = target.Endpoints.Count > 0 ? target.Endpoints[0].Port ?? 1433 : 1433;
            attrs["sqlServerPort"] = port.ToString(CultureInfo.InvariantCulture);
            attrs["servicePrincipalName"] = $"MSSQLSvc/{target.Host}:{port.ToString(CultureInfo.InvariantCulture)}";
            if (!IPAddress.TryParse(target.Host, out _))
                attrs["dNSHostName"] = target.Host;
        }

        return new ConnectorObject
        {
            SourceId = target.SourceId,
            ObjectClass = "computer",
            Attributes = attrs
        };
    }

    /// <summary>The tiny status vocabulary IC projects: Success / Unreachable / AuthFailed.</summary>
    private static string ToScanStatusToken(ScanStatus status) => status switch
    {
        ScanStatus.Success => "Success",
        ScanStatus.AuthFailed => "AuthFailed",
        _ => "Unreachable" // Unreachable, Timeout, Failed — "could not read it"
    };

    /// <summary>
    /// One short line, no newlines, bounded length. Scanner messages are built
    /// from probe reasons and first-line exception text — never from connection
    /// strings — but bound it anyway.
    /// </summary>
    private static string SanitizeScanError(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;
        var line = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length > 300 ? line[..300] : line;
    }

    private static async Task<string?> ResolveIpv4Async(string host)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var addresses = await Dns.GetHostAddressesAsync(host, cts.Token);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    // ─── LDAP plumbing (mirrors ActiveDirectorySource) ──────────────────────

    private static LdapConnection CreateBoundConnection(string host, int port, AdCredentials creds)
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(host, port))
        {
            AuthType = AuthType.Negotiate
        };
        connection.SessionOptions.ProtocolVersion = 3;
        // Never chase referrals — S.DS.P binds+searches every referral until
        // timeout, turning a seconds-long read into minutes (commit 20cf600).
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

        NetworkCredential netCred;
        if (creds.Username.Contains('\\'))
        {
            var parts = creds.Username.Split('\\', 2);
            netCred = new NetworkCredential(parts[1], creds.Password) { Domain = parts[0] };
        }
        else
        {
            netCred = new NetworkCredential(creds.Username, creds.Password);
        }
        connection.Credential = netCred;
        connection.Bind();
        return connection;
    }

    private static string? ResolveDefaultNamingContext(LdapConnection connection)
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

    private static (string Host, int Port) ParseHostPort(string domain)
    {
        var parts = domain.Split(':');
        if (parts.Length == 1) return (parts[0], 389);
        return int.TryParse(parts[1], out var p) ? (parts[0], p) : (parts[0], 389);
    }

    private static string ShortHostName(string host)
    {
        if (IPAddress.TryParse(host, out _)) return host;
        var dot = host.IndexOf('.');
        return dot > 0 ? host[..dot] : host;
    }
}
