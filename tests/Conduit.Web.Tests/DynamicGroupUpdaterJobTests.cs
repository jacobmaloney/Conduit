using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Conduit.Connectors.ActiveDirectory;
using Conduit.Sync.Connectors;
using Conduit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// DYNAMIC-GROUPS-05 (IdentityCenter UC034-D09): the agent's clock over the dynamic groups. The directory, the marks and
/// the report channel are behind three small interfaces, so the clock's own decisions are exercised whole: what is due
/// and when, the baseline look, the watch, the pass over the groups, own groups only, what is reported and in what
/// shape, and that one connection's failure never stops the tick.
/// </summary>
public class DynamicGroupUpdaterJobTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly Guid Ours = Guid.Parse("0786a4f0-1111-4111-8111-000000000001");
    private static readonly Guid Theirs = Guid.Parse("0786a4f0-2222-4222-8222-000000000002");
    private static readonly Guid Foreign = Guid.Parse("0786a4f0-3333-4333-8333-000000000003");
    private static readonly Guid OtherService = Guid.Parse("c0ffee00-0000-4000-8000-00000000cafe");
    private static readonly DateTime Now = new(2026, 11, 03, 12, 00, 00, DateTimeKind.Utc);

    private sealed class FakeState : IDynamicGroupClockState
    {
        public readonly Dictionary<string, DateTime> Marks = new(StringComparer.Ordinal);
        public Task<DateTime?> ReadAsync(string key) => Task.FromResult(Marks.TryGetValue(key, out var v) ? v : (DateTime?)null);
        public Task WriteAsync(string key, DateTime utc) { Marks[key] = utc; return Task.CompletedTask; }
    }

    private sealed class FakeReporter : IAgentWriteReporter
    {
        public readonly List<(string CommandType, string Payload, string Result, bool Success, string Message)> Reports = new();
        public bool Accept = true;
        public Task<bool> ReportAsync(string commandType, string payloadJson, string resultJson, bool success, string message, CancellationToken ct)
        {
            Reports.Add((commandType, payloadJson, resultJson, success, message));
            return Task.FromResult(Accept);
        }
    }

    private sealed class FakeWork : IDynamicGroupClockWork
    {
        public List<DynamicGroupClockTenant> Tenants = new() { new DynamicGroupClockTenant(TenantA, "Lab AD") };
        public Dictionary<Guid, string?> ConnectionNames = new() { [TenantA] = "corp-local" };
        public Dictionary<Guid, List<DynamicGroupListing>> Groups = new();
        public Dictionary<Guid, bool?> Changed = new();
        public Dictionary<Guid, (DynamicGroupRebuild? Rebuild, string? Refusal)> Rebuilds = new();
        public string? ListError;
        public Guid? ThrowsFor;
        public int ChangeQuestions;
        public DateTime? AskedSince;
        public readonly List<Guid> Rebuilt = new();

        public Task<IReadOnlyList<DynamicGroupClockTenant>> ActiveDirectoryTenantsAsync() => Task.FromResult<IReadOnlyList<DynamicGroupClockTenant>>(Tenants);
        public Task<string?> SourceConnectionNameAsync(Guid tenantId) => Task.FromResult(ConnectionNames.TryGetValue(tenantId, out var n) ? n : null);
        public Task<(IReadOnlyList<DynamicGroupListing>? Groups, string? Error)> ListDynamicGroupsAsync(Guid tenantId, CancellationToken ct)
        {
            if (ThrowsFor == tenantId) throw new InvalidOperationException("the directory is down");
            if (ListError is not null) return Task.FromResult<(IReadOnlyList<DynamicGroupListing>?, string?)>((null, ListError));
            return Task.FromResult<(IReadOnlyList<DynamicGroupListing>?, string?)>((Groups.TryGetValue(tenantId, out var g) ? g : new List<DynamicGroupListing>(), null));
        }
        public Task<bool?> AnyDirectoryChangeSinceAsync(Guid tenantId, DateTime sinceUtc, CancellationToken ct)
        {
            ChangeQuestions++;
            AskedSince = sinceUtc;
            return Task.FromResult(Changed.TryGetValue(tenantId, out var c) ? c : false);
        }
        public Task<(DynamicGroupRebuild? Rebuild, string? Refusal)> RebuildOnClockAsync(Guid tenantId, Guid groupGuid, CancellationToken ct)
        {
            Rebuilt.Add(groupGuid);
            return Task.FromResult(Rebuilds.TryGetValue(groupGuid, out var r) ? r : (Unchanged(), null));
        }
    }

    private static DynamicGroupListing Listing(Guid guid, Guid? service, string? error = null) =>
        new(guid.ToString(), $"CN={guid:N},OU=DynamicGroups,DC=corp,DC=local", guid.ToString("N"), service, error);

    private static DynamicGroupRebuild Unchanged() => new()
    {
        Evaluation = new DynamicGroupEvaluation { GroupName = "quiet", IsDynamic = true },
        Changes = new[] { new DynamicGroupChange { Operation = "Add", DistinguishedName = "CN=A", Outcome = "Skipped" } },
        Skipped = 1,
    };

    private static DynamicGroupRebuild Changed(int adds = 1, int removes = 1, int failed = 0) => new()
    {
        Evaluation = new DynamicGroupEvaluation { GroupName = "busy", IsDynamic = true, DesiredMembers = new[] { "CN=A" } },
        Changes = Enumerable.Range(0, adds).Select(i => new DynamicGroupChange { Operation = "Add", DistinguishedName = $"CN=Add{i}", ObjectGuid = Guid.NewGuid().ToString(), Outcome = "Applied" })
            .Concat(Enumerable.Range(0, removes).Select(i => new DynamicGroupChange { Operation = "Remove", DistinguishedName = $"CN=Remove{i}", ObjectGuid = Guid.NewGuid().ToString(), Outcome = "Applied" }))
            .Concat(Enumerable.Range(0, failed).Select(i => new DynamicGroupChange { Operation = "Remove", DistinguishedName = $"CN=Stuck{i}", Outcome = "Failed", Error = "insufficient rights" }))
            .ToList(),
        AppliedAdds = adds, AppliedRemoves = removes, Failed = failed,
    };

    /// <summary>The scheduled pass is due: the last one ran yesterday evening and the look is current.</summary>
    private static void ScheduledPassDue(FakeState state, Guid tenant)
    {
        state.Marks[DynamicGroupUpdaterJob.RebuildKey(tenant)] = new DateTime(2026, 11, 02, 22, 00, 00, DateTimeKind.Utc);
        state.Marks[DynamicGroupUpdaterJob.WatchKey(tenant)] = Now;
    }

    private static (DynamicGroupUpdaterJob Job, FakeWork Work, FakeState State, FakeReporter Reporter) Build(DynamicGroupClockOptions? options = null)
    {
        var work = new FakeWork();
        var state = new FakeState();
        var reporter = new FakeReporter();
        var job = new DynamicGroupUpdaterJob(work, state, reporter, Options.Create(options ?? new DynamicGroupClockOptions()), NullLogger<DynamicGroupUpdaterJob>.Instance);
        return (job, work, state, reporter);
    }

    // ── What is due ──────────────────────────────────────────────────────────

    [Fact]
    public void A_connection_never_rebuilt_waits_for_the_next_scheduled_instant_and_the_watch_covers_it()
    {
        // As the service's daily task: a fresh connection is rebuilt at the next 03:00, not at enrolment; the watch is due now.
        var trigger = DynamicGroupUpdaterJob.DueTrigger(new DynamicGroupClockOptions(), Now, lastRebuildUtc: null, lastWatchUtc: null, out var watchDue);
        Assert.Equal(DynamicGroupUpdaterJob.TriggerDirectoryChange, trigger);
        Assert.True(watchDue);
        Assert.Equal(DynamicGroupUpdaterJob.TriggerSchedule, DynamicGroupUpdaterJob.DueTrigger(new DynamicGroupClockOptions(), new DateTime(2026, 11, 04, 03, 00, 30, DateTimeKind.Utc), null, Now, out _));
    }

    [Fact]
    public void The_scheduled_pass_is_due_when_the_cron_instant_has_passed_since_the_last_one()
    {
        var options = new DynamicGroupClockOptions { RebuildSchedule = "0 3 * * *", ChangeWatchMinutes = 0 };
        var yesterdayEvening = new DateTime(2026, 11, 02, 22, 00, 00, DateTimeKind.Utc);
        Assert.Equal(DynamicGroupUpdaterJob.TriggerSchedule, DynamicGroupUpdaterJob.DueTrigger(options, Now, yesterdayEvening, Now, out _));           // 03:00 passed since
        Assert.Null(DynamicGroupUpdaterJob.DueTrigger(options, Now, new DateTime(2026, 11, 03, 03, 05, 00, DateTimeKind.Utc), Now, out _));      // already ran after 03:00
        Assert.Null(DynamicGroupUpdaterJob.DueTrigger(new DynamicGroupClockOptions { RebuildSchedule = "", ChangeWatchMinutes = 0 }, Now, null, null, out _)); // no schedule, no watch
    }

    [Fact]
    public void The_watch_is_due_on_its_interval_and_only_when_no_scheduled_pass_is()
    {
        var options = new DynamicGroupClockOptions { ChangeWatchMinutes = 5 };
        var rebuiltToday = new DateTime(2026, 11, 03, 03, 01, 00, DateTimeKind.Utc);
        Assert.Equal(DynamicGroupUpdaterJob.TriggerDirectoryChange, DynamicGroupUpdaterJob.DueTrigger(options, Now, rebuiltToday, null, out var due));
        Assert.True(due);
        Assert.Equal(DynamicGroupUpdaterJob.TriggerDirectoryChange, DynamicGroupUpdaterJob.DueTrigger(options, Now, rebuiltToday, Now.AddMinutes(-5), out _));
        Assert.Null(DynamicGroupUpdaterJob.DueTrigger(options, Now, rebuiltToday, Now.AddMinutes(-4), out due));
        Assert.False(due);
        Assert.Null(DynamicGroupUpdaterJob.DueTrigger(new DynamicGroupClockOptions { ChangeWatchMinutes = 0 }, Now, rebuiltToday, null, out _));
    }

    // ── The tick ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disabled_the_clock_asks_nothing_and_writes_nothing()
    {
        var (job, work, state, reporter) = Build(new DynamicGroupClockOptions { Enabled = false });
        ScheduledPassDue(state, TenantA);
        work.Groups[TenantA] = new() { Listing(Ours, DynamicGroupRules.IdentityCenterService) };
        await job.RunAsync(Now, CancellationToken.None);
        Assert.Empty(work.Rebuilt);
        Assert.NotEqual(Now, state.Marks[DynamicGroupUpdaterJob.RebuildKey(TenantA)]);
        Assert.Empty(reporter.Reports);
    }

    [Fact]
    public async Task The_scheduled_pass_rebuilds_the_own_groups_reports_each_that_changed_and_moves_both_marks()
    {
        var (job, work, state, reporter) = Build();
        ScheduledPassDue(state, TenantA);
        work.Groups[TenantA] = new()
        {
            Listing(Ours, DynamicGroupRules.IdentityCenterService),
            Listing(Theirs, DynamicGroupRules.IdentityCenterService),
            Listing(Foreign, OtherService),
        };
        work.Rebuilds[Ours] = (Changed(adds: 2, removes: 1), null);
        work.Rebuilds[Theirs] = (Unchanged(), null);

        await job.RunAsync(Now, CancellationToken.None);

        Assert.Equal(new[] { Ours, Theirs }, work.Rebuilt);                       // the other service's group is left to its own clock
        Assert.Equal(Now, state.Marks[DynamicGroupUpdaterJob.RebuildKey(TenantA)]);
        Assert.Equal(Now, state.Marks[DynamicGroupUpdaterJob.WatchKey(TenantA)]); // a full pass is also a look

        var report = Assert.Single(reporter.Reports);                              // the unchanged group is not reported
        Assert.Equal("RebuildDynamicGroup", report.CommandType);
        Assert.True(report.Success);
        Assert.Equal("'busy' rebuilt: 2 added, 1 removed of 1 by the rules.", report.Message);
        using var payload = JsonDocument.Parse(report.Payload);
        Assert.Equal(1, payload.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("RebuildDynamicGroup", payload.RootElement.GetProperty("operation").GetString());
        Assert.Equal("corp-local", payload.RootElement.GetProperty("sourceConnectionName").GetString());
        Assert.Equal(Ours.ToString(), payload.RootElement.GetProperty("objectGuid").GetString());
        Assert.Equal("Schedule", payload.RootElement.GetProperty("trigger").GetString());
        Assert.False(payload.RootElement.TryGetProperty("connectionId", out _));   // the server adds its own
        using var result = JsonDocument.Parse(report.Result);
        Assert.Equal("RebuildDynamicGroup", result.RootElement.GetProperty("operation").GetString());
        Assert.Equal(3, result.RootElement.GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public async Task With_own_groups_only_off_the_other_services_groups_are_rebuilt_too()
    {
        var (job, work, state, _) = Build(new DynamicGroupClockOptions { OwnGroupsOnly = false });
        ScheduledPassDue(state, TenantA);
        work.Groups[TenantA] = new() { Listing(Ours, DynamicGroupRules.IdentityCenterService), Listing(Foreign, OtherService) };
        await job.RunAsync(Now, CancellationToken.None);
        Assert.Equal(new[] { Ours, Foreign }, work.Rebuilt);
    }

    [Fact]
    public async Task The_first_look_sets_the_baseline_without_asking_the_directory()
    {
        var (job, work, state, _) = Build();
        state.Marks[DynamicGroupUpdaterJob.RebuildKey(TenantA)] = Now.AddHours(-1);   // the scheduled pass is not due
        work.Groups[TenantA] = new() { Listing(Ours, DynamicGroupRules.IdentityCenterService) };

        await job.RunAsync(Now, CancellationToken.None);

        Assert.Equal(0, work.ChangeQuestions);
        Assert.Empty(work.Rebuilt);
        Assert.Equal(Now, state.Marks[DynamicGroupUpdaterJob.WatchKey(TenantA)]);
    }

    [Fact]
    public async Task A_change_since_the_last_look_runs_the_rebuild_as_a_directory_change_and_moves_the_look()
    {
        var (job, work, state, reporter) = Build();
        state.Marks[DynamicGroupUpdaterJob.RebuildKey(TenantA)] = Now.AddHours(-1);
        state.Marks[DynamicGroupUpdaterJob.WatchKey(TenantA)] = Now.AddMinutes(-10);
        work.Groups[TenantA] = new() { Listing(Ours, DynamicGroupRules.IdentityCenterService) };
        work.Changed[TenantA] = true;
        work.Rebuilds[Ours] = (Changed(adds: 0, removes: 1), null);

        await job.RunAsync(Now, CancellationToken.None);

        Assert.Equal(1, work.ChangeQuestions);
        Assert.Equal(Now.AddMinutes(-10) - DynamicGroupUpdaterJob.WatchSkew, work.AskedSince);   // the look reaches back past the mark
        Assert.Equal(new[] { Ours }, work.Rebuilt);
        Assert.Equal(Now, state.Marks[DynamicGroupUpdaterJob.WatchKey(TenantA)]);
        Assert.Equal(Now.AddHours(-1), state.Marks[DynamicGroupUpdaterJob.RebuildKey(TenantA)]);   // the scheduled mark is the schedule's
        var report = Assert.Single(reporter.Reports);
        Assert.Contains("\"trigger\":\"DirectoryChange\"", report.Payload);
    }

    [Fact]
    public async Task No_change_moves_the_look_and_rebuilds_nothing()
    {
        var (job, work, state, _) = Build();
        state.Marks[DynamicGroupUpdaterJob.RebuildKey(TenantA)] = Now.AddHours(-1);
        state.Marks[DynamicGroupUpdaterJob.WatchKey(TenantA)] = Now.AddMinutes(-10);
        work.Groups[TenantA] = new() { Listing(Ours, DynamicGroupRules.IdentityCenterService) };
        work.Changed[TenantA] = false;

        await job.RunAsync(Now, CancellationToken.None);

        Assert.Empty(work.Rebuilt);
        Assert.Equal(Now, state.Marks[DynamicGroupUpdaterJob.WatchKey(TenantA)]);
    }

    [Fact]
    public async Task When_the_directory_cannot_be_asked_the_look_stays_where_it_was()
    {
        var (job, work, state, _) = Build();
        state.Marks[DynamicGroupUpdaterJob.RebuildKey(TenantA)] = Now.AddHours(-1);
        state.Marks[DynamicGroupUpdaterJob.WatchKey(TenantA)] = Now.AddMinutes(-10);
        work.Changed[TenantA] = null;

        await job.RunAsync(Now, CancellationToken.None);

        Assert.Empty(work.Rebuilt);
        Assert.Equal(Now.AddMinutes(-10), state.Marks[DynamicGroupUpdaterJob.WatchKey(TenantA)]);
    }

    [Fact]
    public async Task A_listing_that_fails_leaves_the_marks_so_the_pass_runs_again_next_tick()
    {
        var (job, work, state, _) = Build();
        ScheduledPassDue(state, TenantA);
        work.ListError = "The directory connection could not be opened";
        await job.RunAsync(Now, CancellationToken.None);
        Assert.Equal(new DateTime(2026, 11, 02, 22, 00, 00, DateTimeKind.Utc), state.Marks[DynamicGroupUpdaterJob.RebuildKey(TenantA)]);
        Assert.Empty(work.Rebuilt);
    }

    [Fact]
    public async Task A_refused_group_a_failed_group_and_a_broken_definition_are_counted_and_the_pass_goes_on()
    {
        var (job, work, _, reporter) = Build();
        var broken = Guid.Parse("0786a4f0-4444-4444-8444-000000000004");
        var stuck = Guid.Parse("0786a4f0-5555-4555-8555-000000000005");
        work.Groups[TenantA] = new()
        {
            Listing(broken, null, error: "The definition does not parse."),
            Listing(Theirs, DynamicGroupRules.IdentityCenterService),
            Listing(stuck, DynamicGroupRules.IdentityCenterService),
            Listing(Ours, DynamicGroupRules.IdentityCenterService),
        };
        work.Rebuilds[Theirs] = (null, "refusing to change a privileged group (Domain Admins).");
        work.Rebuilds[stuck] = (DynamicGroupRebuild.Fail("The group vanished."), null);
        work.Rebuilds[Ours] = (Changed(), null);

        var pass = await job.RebuildAllAsync(new DynamicGroupClockTenant(TenantA, "Lab AD"), DynamicGroupUpdaterJob.TriggerSchedule, CancellationToken.None);

        Assert.NotNull(pass);
        Assert.Equal(4, pass!.Groups);
        Assert.Equal(1, pass.Rebuilt);
        Assert.Equal(2, pass.Refused);   // the broken definition and the privileged group
        Assert.Equal(1, pass.Failed);
        Assert.Equal(0, pass.Skipped);
        Assert.Single(reporter.Reports);
        Assert.DoesNotContain(broken, work.Rebuilt);
    }

    [Fact]
    public async Task A_rebuild_with_a_failed_change_is_reported_as_failed_with_the_changes()
    {
        var (job, work, state, reporter) = Build();
        ScheduledPassDue(state, TenantA);
        work.Groups[TenantA] = new() { Listing(Ours, DynamicGroupRules.IdentityCenterService) };
        work.Rebuilds[Ours] = (Changed(adds: 1, removes: 0, failed: 1), null);
        await job.RunAsync(Now, CancellationToken.None);
        var report = Assert.Single(reporter.Reports);
        Assert.False(report.Success);
        Assert.Contains("1 change(s) failed", report.Message);
    }

    [Fact]
    public async Task Without_a_connection_name_the_rebuild_happens_but_nothing_is_reported()
    {
        var (job, work, state, reporter) = Build();
        ScheduledPassDue(state, TenantA);
        work.ConnectionNames[TenantA] = null;
        work.Groups[TenantA] = new() { Listing(Ours, DynamicGroupRules.IdentityCenterService) };
        work.Rebuilds[Ours] = (Changed(), null);
        await job.RunAsync(Now, CancellationToken.None);
        Assert.Equal(new[] { Ours }, work.Rebuilt);
        Assert.Empty(reporter.Reports);
        Assert.Equal(Now, state.Marks[DynamicGroupUpdaterJob.RebuildKey(TenantA)]);
    }

    [Fact]
    public async Task One_connection_throwing_does_not_stop_the_others()
    {
        var (job, work, state, _) = Build();
        work.Tenants = new() { new DynamicGroupClockTenant(TenantA, "Lab AD"), new DynamicGroupClockTenant(TenantB, "Other AD") };
        ScheduledPassDue(state, TenantA);
        ScheduledPassDue(state, TenantB);
        work.ThrowsFor = TenantA;
        work.Groups[TenantB] = new() { Listing(Ours, DynamicGroupRules.IdentityCenterService) };

        await job.RunAsync(Now, CancellationToken.None);

        Assert.Equal(new[] { Ours }, work.Rebuilt);
        Assert.NotEqual(Now, state.Marks[DynamicGroupUpdaterJob.RebuildKey(TenantA)]);
        Assert.Equal(Now, state.Marks[DynamicGroupUpdaterJob.RebuildKey(TenantB)]);
    }

    [Fact]
    public void The_report_payload_is_the_dispatchers_shape_plus_the_trigger()
    {
        using var doc = JsonDocument.Parse(DynamicGroupUpdaterJob.ReportPayload("corp-local", Ours, DynamicGroupUpdaterJob.TriggerDirectoryChange));
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "schemaVersion", "operation", "sourceConnectionName", "objectGuid", "trigger" }, names);
    }

    // ── Source pins ──────────────────────────────────────────────────────────

    [Fact]
    public void The_clock_runs_on_the_one_due_computation_the_one_executor_and_the_one_scheduler()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", ".."));
        string Read(string relative) => File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

        var job = Read("src/Conduit.Web/Services/DynamicGroupUpdaterJob.cs");
        Assert.Contains("ScheduledSyncRunnerJob.IsDue(options.RebuildSchedule, lastRebuildUtc, nowUtc)", job);   // the sync projects' cron, not a second one
        Assert.Contains("AdAgentDynamicGroupExecutor.BuildRebuildResultJson(rebuild, AdAgentDynamicGroupExecutor.OpRebuild)", job);
        Assert.Contains("/api/agent/commands/report", job);
        Assert.DoesNotContain("new LdapConnection", job);                                                     // the directory stays behind the executor

        var executor = Read("src/Conduit.Web/Services/AdAgentDynamicGroupExecutor.cs");
        Assert.Contains("public sealed class AdAgentDynamicGroupExecutor : IDynamicGroupClockWork", executor);
        Assert.Contains("private static async Task<string?> RefuseWriteAsync(IConnectorAdapter adapter, Guid tenantId, Guid groupGuid, CancellationToken ct)", executor);
        Assert.Equal(2, executor.Split("await RefuseWriteAsync(").Length - 1);                                // the commands and the clock, one guard

        var evaluator = Read("src/Conduit.Connectors.ActiveDirectory/ActiveDirectoryDynamicGroupEvaluator.cs");
        Assert.Contains("ActiveDirectorySink.ReadDefaultNamingContext(connection)", evaluator);
        Assert.Contains("ActiveDirectorySource.ToGeneralizedTime(sinceUtc)", evaluator);
        Assert.Contains("(whenChanged>=", evaluator);
        Assert.Contains("new PageResultRequestControl(1)", evaluator);                                         // one entry answers the watch

        var program = Read("src/Conduit.Web/Program.cs");
        Assert.Contains("s => s.GetRequiredService<Conduit.Web.Services.DynamicGroupUpdaterJob>()", program);
        Assert.Contains("builder.Services.AddScoped<Conduit.Web.Services.IDynamicGroupClockWork>(sp => sp.GetRequiredService<Conduit.Web.Services.AdAgentDynamicGroupExecutor>());", program);
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
