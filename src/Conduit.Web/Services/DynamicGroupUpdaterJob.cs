using System.Text;
using System.Text.Json;
using Conduit.Connectors.ActiveDirectory;
using Conduit.DataAccess.Repositories;
using Conduit.Scheduling;
using Conduit.Sync.Connectors;
using Conduit.Sync.Orchestration;
using Microsoft.Extensions.Options;

namespace Conduit.Web.Services;

/// <summary>
/// DYNAMIC-GROUPS-05 (IdentityCenter UC034-D09 under D12): the clock a dynamic group runs on, owned by the agent that
/// holds the bind. Two evaluators, as the service it replaces: a scheduled rebuild of every dynamic group this product
/// originated (the daily updater; its schedule is a cron expression in UTC as a sync project's is, default 03:00; a
/// connection never rebuilt waits for the next instant, as the service's task does), and a change-driven re-apply (a watch every few minutes that asks the directory whether anything changed since the last
/// look; when it did, the same rebuild runs, so a member added or removed by hand is undone within the watch interval
/// rather than the next day). Every rebuild that changed something is reported to IdentityCenter as a completed
/// RebuildDynamicGroup command of the agent's own, so the catalog and the history follow through the one settler.
/// Per connection: the scheduled pass and the watch's last look are kept in SystemConfiguration. Never throws out of a
/// tick; a connection that cannot be read is logged and tried again next minute.
/// </summary>
public sealed class DynamicGroupUpdaterJob : IScheduledJob
{
    public const string TriggerSchedule = "Schedule";
    public const string TriggerDirectoryChange = "DirectoryChange";
    /// <summary>whenChanged is kept to the second and set on each domain controller as the change lands; the watch looks back this far past its last look.</summary>
    public static readonly TimeSpan WatchSkew = TimeSpan.FromMinutes(1);

    private readonly IDynamicGroupClockWork _work;
    private readonly IDynamicGroupClockState _state;
    private readonly IAgentWriteReporter _reporter;
    private readonly DynamicGroupClockOptions _options;
    private readonly ILogger<DynamicGroupUpdaterJob> _logger;

    public DynamicGroupUpdaterJob(
        IDynamicGroupClockWork work,
        IDynamicGroupClockState state,
        IAgentWriteReporter reporter,
        IOptions<DynamicGroupClockOptions> options,
        ILogger<DynamicGroupUpdaterJob> logger)
    {
        _work = work;
        _state = state;
        _reporter = reporter;
        _options = options.Value;
        _logger = logger;
    }

    public string JobName => "DynamicGroupUpdater";
    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public static string RebuildKey(Guid tenantId) => string.Concat("DynamicGroups:Clock:", tenantId.ToString(), ":LastRebuildUtc");
    public static string WatchKey(Guid tenantId) => string.Concat("DynamicGroups:Clock:", tenantId.ToString(), ":LastWatchUtc");

    public Task ExecuteAsync(CancellationToken cancellationToken) => RunAsync(DateTime.UtcNow, cancellationToken);

    /// <summary>One tick: for every active Active Directory connection, decide whether a pass is due and run it.</summary>
    public async Task RunAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;
        IReadOnlyList<DynamicGroupClockTenant> tenants;
        try { tenants = await _work.ActiveDirectoryTenantsAsync(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dynamic group clock: the connections could not be listed this tick.");
            return;
        }

        foreach (var tenant in tenants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await PassIfDueAsync(tenant, nowUtc, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dynamic group clock: the pass over {Connection} threw; it runs again next tick.", tenant.Name);
            }
        }
    }

    /// <summary>Which pass a connection is due, if any: the scheduled rebuild first, else the change watch.</summary>
    public static string? DueTrigger(DynamicGroupClockOptions options, DateTime nowUtc, DateTime? lastRebuildUtc, DateTime? lastWatchUtc, out bool watchDue)
    {
        watchDue = false;
        if (ScheduledSyncRunnerJob.IsDue(options.RebuildSchedule, lastRebuildUtc, nowUtc)) return TriggerSchedule;
        if (options.ChangeWatchMinutes <= 0) return null;
        watchDue = lastWatchUtc is null || nowUtc - lastWatchUtc.Value >= TimeSpan.FromMinutes(options.ChangeWatchMinutes);
        return watchDue ? TriggerDirectoryChange : null;
    }

    private async Task PassIfDueAsync(DynamicGroupClockTenant tenant, DateTime nowUtc, CancellationToken ct)
    {
        var lastRebuild = await _state.ReadAsync(RebuildKey(tenant.Id));
        var lastWatch = await _state.ReadAsync(WatchKey(tenant.Id));
        var trigger = DueTrigger(_options, nowUtc, lastRebuild, lastWatch, out _);
        if (trigger is null) return;

        if (trigger == TriggerDirectoryChange)
        {
            // The first look only sets the baseline: from here any change runs the rebuild, and the schedule runs it at its hour.
            if (lastWatch is null)
            {
                await _state.WriteAsync(WatchKey(tenant.Id), nowUtc);
                return;
            }
            var changed = await _work.AnyDirectoryChangeSinceAsync(tenant.Id, lastWatch.Value - WatchSkew, ct);
            if (changed is null)
            {
                _logger.LogDebug("Dynamic group clock: {Connection} could not be asked for changes; the watch stays where it was.", tenant.Name);
                return;
            }
            await _state.WriteAsync(WatchKey(tenant.Id), nowUtc);
            if (changed == false) return;
        }

        var summary = await RebuildAllAsync(tenant, trigger, ct);
        if (summary is null) return;   // the groups could not be listed: nothing advances, the pass runs again next tick

        if (trigger == TriggerSchedule)
        {
            await _state.WriteAsync(RebuildKey(tenant.Id), nowUtc);
            await _state.WriteAsync(WatchKey(tenant.Id), nowUtc);   // a full pass is also a look
        }
        _logger.LogInformation("Dynamic group clock ({Trigger}) on {Connection}: {Groups} group(s); {Rebuilt} rebuilt, {Unchanged} unchanged, {Skipped} not this product's, {Refused} refused, {Failed} failed.",
            trigger, tenant.Name, summary.Groups, summary.Rebuilt, summary.Unchanged, summary.Skipped, summary.Refused, summary.Failed);
    }

    /// <summary>Rebuilds every dynamic group of the connection this product originated and reports each that changed; null when the groups could not be listed.</summary>
    public async Task<DynamicGroupClockPass?> RebuildAllAsync(DynamicGroupClockTenant tenant, string trigger, CancellationToken ct)
    {
        var (groups, error) = await _work.ListDynamicGroupsAsync(tenant.Id, ct);
        if (groups is null)
        {
            _logger.LogWarning("Dynamic group clock: the dynamic groups of {Connection} could not be listed ({Error}); the pass runs again next tick.", tenant.Name, error);
            return null;
        }

        var connectionName = await _work.SourceConnectionNameAsync(tenant.Id);
        var pass = new DynamicGroupClockPass { Groups = groups.Count };
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (group.Error is not null || !Guid.TryParse(group.ObjectGuid, out var guid))
            {
                pass.Refused++;
                _logger.LogWarning("Dynamic group clock: {Group} on {Connection} is left alone: {Reason}", group.DistinguishedName, tenant.Name, group.Error ?? "its objectGUID could not be read");
                continue;
            }
            if (_options.OwnGroupsOnly && group.OriginatingService != DynamicGroupRules.IdentityCenterService)
            {
                pass.Skipped++;
                continue;
            }

            var (rebuild, refusal) = await _work.RebuildOnClockAsync(tenant.Id, guid, ct);
            if (rebuild is null)
            {
                pass.Refused++;
                _logger.LogWarning("Dynamic group clock: {Group} on {Connection} was not rebuilt: {Reason}", group.DistinguishedName, tenant.Name, refusal);
                continue;
            }
            if (rebuild.ErrorMessage is not null)
            {
                pass.Failed++;
                _logger.LogWarning("Dynamic group clock: {Group} on {Connection} could not be rebuilt: {Reason}", group.DistinguishedName, tenant.Name, AdAgentCreateExecutor.SanitizeLdapError(rebuild.ErrorMessage));
                continue;
            }
            if (rebuild.Changes.All(c => string.Equals(c.Outcome, "Skipped", StringComparison.Ordinal)))
            {
                pass.Unchanged++;
                continue;
            }

            pass.Rebuilt++;
            if (connectionName is null)
            {
                _logger.LogWarning("Dynamic group clock: {Group} on {Connection} was rebuilt but the connection has no IdentityCenter name to report under; the catalog follows on the next sync.", group.DistinguishedName, tenant.Name);
                continue;
            }
            var payload = ReportPayload(connectionName, guid, trigger);
            var result = AdAgentDynamicGroupExecutor.BuildRebuildResultJson(rebuild, AdAgentDynamicGroupExecutor.OpRebuild);
            var reported = await _reporter.ReportAsync(AdAgentDynamicGroupExecutor.OpRebuild, payload, result, rebuild.Failed == 0, AdAgentDynamicGroupExecutor.DescribeRebuild(rebuild), ct);
            if (!reported)
                _logger.LogWarning("Dynamic group clock: the rebuild of {Group} on {Connection} could not be reported to IdentityCenter; the catalog follows on the next sync.", group.DistinguishedName, tenant.Name);
        }
        return pass;
    }

    /// <summary>The payload of a reported rebuild: the shape IdentityCenter dispatches itself, plus what set the clock off. The server adds its connection id.</summary>
    public static string ReportPayload(string sourceConnectionName, Guid groupGuid, string trigger) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            operation = AdAgentDynamicGroupExecutor.OpRebuild,
            sourceConnectionName,
            objectGuid = groupGuid.ToString(),
            trigger,
        });
}

/// <summary>The <c>DynamicGroups</c> configuration section.</summary>
public sealed class DynamicGroupClockOptions
{
    public const string Section = "DynamicGroups";
    public bool Enabled { get; set; } = true;
    /// <summary>When every dynamic group is rebuilt whether or not anything changed: a cron expression in UTC, as a sync project's schedule (default daily at 03:00); blank turns the scheduled pass off.</summary>
    public string? RebuildSchedule { get; set; } = "0 3 * * *";
    /// <summary>How often the directory is asked whether anything changed since the last look; a change runs the rebuild. 0 turns the watch off.</summary>
    public int ChangeWatchMinutes { get; set; } = 5;
    /// <summary>Rebuild only the groups whose definition names this product as the originating service, as the service it replaces does; off, every dynamic group on the connection.</summary>
    public bool OwnGroupsOnly { get; set; } = true;
}

public sealed record DynamicGroupClockTenant(Guid Id, string Name);

public sealed class DynamicGroupClockPass
{
    public int Groups { get; set; }
    public int Rebuilt { get; set; }
    public int Unchanged { get; set; }
    public int Skipped { get; set; }
    public int Refused { get; set; }
    public int Failed { get; set; }
}

/// <summary>What the clock asks of the directory, behind the executor's gates (the same evaluator and the same write guard as the agent commands).</summary>
public interface IDynamicGroupClockWork
{
    Task<IReadOnlyList<DynamicGroupClockTenant>> ActiveDirectoryTenantsAsync();
    Task<string?> SourceConnectionNameAsync(Guid tenantId);
    Task<(IReadOnlyList<DynamicGroupListing>? Groups, string? Error)> ListDynamicGroupsAsync(Guid tenantId, CancellationToken ct);
    /// <summary>True or false when the directory answered; null when it could not be asked.</summary>
    Task<bool?> AnyDirectoryChangeSinceAsync(Guid tenantId, DateTime sinceUtc, CancellationToken ct);
    Task<(DynamicGroupRebuild? Rebuild, string? Refusal)> RebuildOnClockAsync(Guid tenantId, Guid groupGuid, CancellationToken ct);
}

/// <summary>The clock's two marks per connection, as UTC instants under a key.</summary>
public interface IDynamicGroupClockState
{
    Task<DateTime?> ReadAsync(string key);
    Task WriteAsync(string key, DateTime utc);
}

/// <summary>The marks in the SystemConfiguration key/value table, as an ISO 8601 UTC string.</summary>
public sealed class SystemConfigurationClockState : IDynamicGroupClockState
{
    private readonly SystemConfigurationRepository _configuration;
    public SystemConfigurationClockState(SystemConfigurationRepository configuration) => _configuration = configuration;

    public async Task<DateTime?> ReadAsync(string key)
    {
        var text = await _configuration.GetValueAsync(key);
        return DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var utc) ? utc : null;
    }

    public Task WriteAsync(string key, DateTime utc) =>
        _configuration.UpsertAsync(key, utc.ToUniversalTime().ToString("O"), "DateTime", "Dynamic group clock mark, written by the updater job.");
}

/// <summary>A write the agent made on its own clock, told to IdentityCenter as a completed command of the agent's own.</summary>
public interface IAgentWriteReporter
{
    /// <summary>True when at least one IdentityCenter endpoint recorded the report.</summary>
    Task<bool> ReportAsync(string commandType, string payloadJson, string resultJson, bool success, string message, CancellationToken ct);
}

/// <summary>
/// POST /api/agent/commands/report on every IdentityCenter endpoint this installation reaches, on the channel key (the
/// per-agent key, which carries the agent_id the server records the command under). An older server answers 404 and the
/// report is dropped with a debug line; the catalog then follows on the next sync. Never throws.
/// </summary>
public sealed class IcAgentWriteReporter : IAgentWriteReporter
{
    private readonly IcEndpointDirectory _endpoints;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<IcAgentWriteReporter> _logger;

    public IcAgentWriteReporter(IcEndpointDirectory endpoints, IHttpClientFactory httpFactory, ILogger<IcAgentWriteReporter> logger)
    {
        _endpoints = endpoints;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<bool> ReportAsync(string commandType, string payloadJson, string resultJson, bool success, string message, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { commandType, payloadJson, resultJson, success, message });
        var recorded = false;
        List<IcEndpoint> endpoints;
        try { endpoints = await _endpoints.DiscoverAsync(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Agent write report: the IdentityCenter endpoints could not be read.");
            return false;
        }

        foreach (var endpoint in endpoints)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var client = _httpFactory.CreateClient("IcAgentCommandPoller");
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.Remove("X-API-Key");
                client.DefaultRequestHeaders.Add("X-API-Key", endpoint.ChannelKey);
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync($"{endpoint.BaseUrl}/api/agent/commands/report", content, ct);
                if (response.IsSuccessStatusCode) { recorded = true; continue; }
                _logger.LogDebug("Agent write report at {BaseUrl} returned HTTP {Status}.", endpoint.BaseUrl, (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _logger.LogDebug("Agent write report at {BaseUrl} unreachable ({Message}).", endpoint.BaseUrl, ex.Message);
            }
        }
        return recorded;
    }
}
