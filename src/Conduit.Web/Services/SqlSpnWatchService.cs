using System.Text.Json;
using Conduit.Connectors.SqlDiscovery;
using Conduit.Core.Models;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Security;
using Conduit.Web.Models;

namespace Conduit.Web.Services;

/// <summary>
/// Near-real-time SQL Server spin-up detection. On a tight timer (default 5 min) this
/// runs a cheap, AD-read-only SPN sweep — the same MSSQLSvc SPN query SQL registers
/// when an instance starts — and diffs it against the last sweep it remembered
/// (<see cref="SqlSpnSnapshotRepository"/>). A host key that appears for the first time
/// IS a newly-registered SQL instance; a key that vanished is a retirement.
///
/// On a genuine new instance it (1) writes an audit record so the event is visible and
/// (2) kicks the existing SQL Discovery Sync Project once, so the instance is fully
/// interrogated and pushed to IdentityCenter within minutes instead of on the nightly
/// sweep. This is the difference between "we noticed on tomorrow's scan" and "we caught
/// it the moment it came online" — and it needs NO SQL credential, only AD read.
///
/// Design: first sight of a connection (empty remembered set) seeds the baseline WITHOUT
/// firing new-instance events — otherwise every pre-existing server would look brand new
/// on first boot. Real deltas only start from the second sweep. Fail-soft throughout: a
/// bad AD bind or query for one connection is logged and skipped; nothing here may ever
/// crash the host.
/// </summary>
public sealed class SqlSpnWatchService : BackgroundService
{
    private const string LdapCredentialName = "ldap";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SqlDiscoveryRunner _discoveryRunner;
    private readonly IConfiguration _config;
    private readonly ILogger<SqlSpnWatchService> _logger;

    public SqlSpnWatchService(
        IServiceScopeFactory scopeFactory,
        SqlDiscoveryRunner discoveryRunner,
        IConfiguration config,
        ILogger<SqlSpnWatchService> logger)
    {
        _scopeFactory = scopeFactory;
        _discoveryRunner = discoveryRunner;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("SqlSpnWatch:Enabled", true))
        {
            _logger.LogInformation("SQL SPN watcher disabled via SqlSpnWatch:Enabled=false.");
            return;
        }

        var intervalSeconds = Math.Clamp(_config.GetValue("SqlSpnWatch:IntervalSeconds", 300), 60, 3600);
        _logger.LogInformation("SQL SPN watcher started (interval {Interval}s).", intervalSeconds);

        // Let the app finish booting (DB init/migrations) before the first sweep.
        try { await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        do
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let a sweep failure take down the host — log and wait for the next tick.
                _logger.LogError(ex, "SQL SPN watcher sweep failed; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var tenantRepo = scope.ServiceProvider.GetRequiredService<TenantRepository>();
        var protector = scope.ServiceProvider.GetRequiredService<CredentialProtector>();
        var snapshotRepo = scope.ServiceProvider.GetRequiredService<SqlSpnSnapshotRepository>();
        var auditRepo = scope.ServiceProvider.GetRequiredService<AuditLogRepository>();

        var tenants = await tenantRepo.GetAllAsync(includeInactive: false);
        var adConnections = tenants
            .Where(t => string.Equals(t.SystemType, "ActiveDirectory", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(t.Domain))
            .ToList();
        if (adConnections.Count == 0) return;

        var anyNewInstance = false;

        foreach (var ad in adConnections)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var creds = await ReadLdapCredentialsAsync(protector, ad);
                if (creds is null) continue; // no usable ldap credential — nothing to sweep with

                var (host, port) = ParseHostPort(ad.Domain!);
                var hosts = await SqlSpnProbe.EnumerateAsync(host, port, creds.Value.Username, creds.Value.Password, ct);

                var current = hosts
                    .GroupBy(h => h.HostKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var known = await snapshotRepo.GetHostKeysAsync(ad.Id);
                var now = DateTime.UtcNow;

                // First sight of this connection: establish a baseline silently, no new-instance events.
                if (known.Count == 0)
                {
                    foreach (var h in current.Values)
                        await snapshotRepo.UpsertSeenAsync(ad.Id, h.HostKey, FirstSpn(h), now);
                    _logger.LogInformation(
                        "SQL SPN watcher: baseline for AD '{Ad}' — {Count} SQL host(s) recorded.", ad.Name, current.Count);
                    continue;
                }

                var newKeys = current.Keys.Where(k => !known.Contains(k)).ToList();
                var removedKeys = known.Where(k => !current.ContainsKey(k)).ToList();

                foreach (var key in newKeys)
                {
                    var h = current[key];
                    var spn = FirstSpn(h);
                    await auditRepo.InsertAsync(new AuditLog
                    {
                        Id = Guid.NewGuid(),
                        Timestamp = now,
                        Action = "SqlInstanceDiscovered",
                        ResourceType = "SqlServer",
                        ResourceId = key,
                        UserName = "System (SPN Watch)",
                        Details = $"New SQL Server registered in AD '{ad.Name}': {spn ?? key}. Detected via MSSQLSvc SPN before the scheduled scan; triggering discovery to interrogate and report it."
                    });
                    _logger.LogInformation("SQL SPN watcher: NEW instance {Spn} in AD '{Ad}'.", spn ?? key, ad.Name);
                    anyNewInstance = true;
                }

                foreach (var key in removedKeys)
                {
                    await auditRepo.InsertAsync(new AuditLog
                    {
                        Id = Guid.NewGuid(),
                        Timestamp = now,
                        Action = "SqlInstanceRetired",
                        ResourceType = "SqlServer",
                        ResourceId = key,
                        UserName = "System (SPN Watch)",
                        Details = $"SQL Server SPN no longer present in AD '{ad.Name}': {key}. The instance appears to have been retired or its SPN removed."
                    });
                    _logger.LogInformation("SQL SPN watcher: RETIRED instance {Key} in AD '{Ad}'.", key, ad.Name);
                }

                // Update the remembered set: bump/insert everything seen now, forget what's gone.
                foreach (var h in current.Values)
                    await snapshotRepo.UpsertSeenAsync(ad.Id, h.HostKey, FirstSpn(h), now);
                await snapshotRepo.RemoveAsync(ad.Id, removedKeys);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SQL SPN watcher: sweep of AD '{Ad}' failed; skipping this connection.", ad.Name);
            }
        }

        // One discovery run covers all connections' new instances — the discovery source
        // re-enumerates everything and pushes enriched facts to IdentityCenter.
        if (anyNewInstance)
        {
            var (ok, msg) = await _discoveryRunner.RunAsync(projectName: null, ct);
            _logger.LogInformation("SQL SPN watcher: triggered discovery after new instance(s) — {Ok}: {Msg}", ok, msg);
        }
    }

    private static string? FirstSpn(SpnHost h) => h.Spns.Count > 0 ? h.Spns[0] : null;

    private static async Task<(string Username, string Password)?> ReadLdapCredentialsAsync(
        CredentialProtector protector, Tenant ad)
    {
        var raw = await protector.RetrieveAsync(ad.Id, LdapCredentialName);
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var u = doc.RootElement.TryGetProperty("Username", out var uEl) ? uEl.GetString() : null;
            var p = doc.RootElement.TryGetProperty("Password", out var pEl) ? pEl.GetString() : null;
            if (string.IsNullOrEmpty(u) || p is null) return null;
            return (u, p);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string Host, int Port) ParseHostPort(string domain)
    {
        var parts = domain.Split(':');
        if (parts.Length == 1) return (parts[0], 389);
        return int.TryParse(parts[1], out var p) ? (parts[0], p) : (parts[0], 389);
    }
}
