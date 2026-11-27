using Conduit.Core.SyncModels;
using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Sync.Orchestration;
using Conduit.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// UC017-F47: a sync project read one container at a time is delete-detected across the plan. No container tombstones
/// on its own; once every container of the plan (IdentityCenter's ParentJobId) reports a complete read, the group is
/// finalized exactly once against the step's baseline (the last complete group of the same plan) intersected with the
/// step's registry. Every gate leaves the sink untouched, and the default mode only reports.
/// </summary>
public class PartitionGroupDeleteDetectionTests
{
    private static readonly Guid Project = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid Step = Guid.Parse("cccccccc-0000-0000-0000-00000000000c");
    private static readonly Guid PreviousGroup = Guid.Parse("dddddddd-0000-0000-0000-00000000000d");
    private static readonly Guid Group = Guid.Parse("eeeeeeee-0000-0000-0000-00000000000e");

    // ── payload ────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_plan_item_IdentityCenter_writes_is_read_back_as_the_group()
    {
        const string payload = """{"Partition":{"Index":2,"Count":19,"Label":"OU=Sales,DC=example,DC=com","IncludedBaseDNs":["OU=Sales,DC=example,DC=com"],"OneLevel":false,"CarriesTombstones":false,"ParentJobId":"bbbbbbbb-0000-0000-0000-00000000000b"}}""";
        var read = IcPartitionPayload.ReadPartition(payload)!;
        Assert.Equal(Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b"), read.ParentJobId);
        Assert.Equal((2, 19), (read.Index, read.Count));
    }

    [Theory]
    [InlineData("""{"Partition":{"Index":1,"Count":2,"IncludedBaseDNs":["OU=a"]}}""")]
    [InlineData("""{"Partition":{"Index":1,"Count":2,"IncludedBaseDNs":["OU=a"],"ParentJobId":"not-a-guid"}}""")]
    [InlineData("""{"Partition":{"Index":1,"Count":2,"IncludedBaseDNs":["OU=a"],"ParentJobId":"00000000-0000-0000-0000-000000000000"}}""")]
    [InlineData("""{"Partition":{"Index":1,"Count":2,"IncludedBaseDNs":["OU=a"],"ParentJobId":7}}""")]
    public void A_container_without_a_usable_plan_item_still_runs_but_is_no_group(string payload)
    {
        var read = IcPartitionPayload.ReadPartition(payload);
        Assert.NotNull(read);
        Assert.Null(read!.ParentJobId);
    }

    // ── settings ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, null, PartitionDeleteDetectionMode.ReportOnly, 10)]
    [InlineData("Off", null, PartitionDeleteDetectionMode.Off, 10)]
    [InlineData("enforce", "25", PartitionDeleteDetectionMode.Enforce, 25)]
    [InlineData("reportonly", "0", PartitionDeleteDetectionMode.ReportOnly, 10)]
    [InlineData("delete-everything", "250", PartitionDeleteDetectionMode.ReportOnly, 10)]
    [InlineData("2", "x", PartitionDeleteDetectionMode.ReportOnly, 10)]
    [InlineData("Off,Enforce", null, PartitionDeleteDetectionMode.ReportOnly, 10)]
    [InlineData("9", null, PartitionDeleteDetectionMode.ReportOnly, 10)]
    public void The_mode_defaults_to_report_only_and_an_unknown_value_never_enforces(string? mode, string? percent, PartitionDeleteDetectionMode expected, int expectedPercent)
    {
        var values = new Dictionary<string, string?>();
        if (mode is not null) values["Sync:PartitionDeleteDetection"] = mode;
        if (percent is not null) values["Sync:PartitionDeleteMaxPercent"] = percent;
        var settings = PartitionDeleteDetectionSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        Assert.Equal(expected, settings.Mode);
        Assert.Equal(expectedPercent, settings.MaxDeletePercent);
    }

    [Theory]
    [InlineData(0, 10, 5)]
    [InlineData(20, 10, 5)]
    [InlineData(100, 10, 10)]
    [InlineData(101, 10, 11)]
    [InlineData(2000, 10, 200)]
    public void The_cap_is_five_or_the_percentage_of_prior_whichever_is_larger(int prior, int percent, int cap) =>
        Assert.Equal(cap, PartitionGroupFinalizer.DeleteCap(prior, percent));

    [Fact]
    public void The_plan_fingerprint_follows_the_container_list_not_the_order_containers_finished()
    {
        var a = Results(Group, 3).ToList();
        var shuffled = new[] { a[2], a[0], a[1] };
        Assert.Equal(PartitionGroupFinalizer.PlanFingerprint(a), PartitionGroupFinalizer.PlanFingerprint(shuffled));
        var other = Results(Group, 3, dnPrefix: "OU=Other").ToList();
        Assert.NotEqual(PartitionGroupFinalizer.PlanFingerprint(a), PartitionGroupFinalizer.PlanFingerprint(other));
        Assert.Equal("2/3|subtree|ou=sales,dc=example,dc=com",
            PartitionGroupFinalizer.ContainerKey(new SyncRunPartition(2, 3, "Sales", new[] { "OU=Sales, DC=example,DC=com" }, false, false)));
    }

    // ── gates ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Eighteen_of_nineteen_containers_wait_and_nothing_is_claimed_or_emitted()
    {
        var h = Harness.WithBaseline(count: 19);
        foreach (var r in Results(Group, 19).Take(18)) h.Store.Results.Add(r);
        h.Store.Seen[(Group, Step)] = new HashSet<string>(new[] { "u1" });

        var result = await h.FinalizeAsync(PartitionDeleteDetectionMode.Enforce);

        Assert.Equal(PartitionGroupOutcomes.Waiting, result.Outcome);
        Assert.Empty(h.Sink.Calls);
        Assert.Empty(h.Store.Claims);
        Assert.Contains(h.Logs, l => l.Contains("waits for 1 more of 19"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task One_failed_or_partial_container_stops_the_plan_and_nothing_is_emitted(bool succeeded, bool complete)
    {
        var h = Harness.WithBaseline(count: 19);
        var results = Results(Group, 19).ToList();
        results[7].Succeeded = succeeded;
        results[7].AllStepsCompleteRead = complete;
        h.Store.Results.AddRange(results);
        h.Store.Seen[(Group, Step)] = new HashSet<string>(new[] { "u1" });

        var result = await h.FinalizeAsync(PartitionDeleteDetectionMode.Enforce);

        Assert.Equal(PartitionGroupOutcomes.PartitionIncomplete, result.Outcome);
        Assert.Empty(h.Sink.Calls);
        Assert.Empty(h.Pruned);
        Assert.False(h.Store.Seen.ContainsKey((Group, Step)));
        Assert.Equal(PreviousGroup, h.Store.Baselines[(Project, Step)].GroupId);
        Assert.Equal(PartitionGroupOutcomes.PartitionIncomplete, h.Store.Completions[Group].Outcome);
    }

    [Fact]
    public async Task A_complete_plan_emits_exactly_prior_minus_the_union_once_even_when_finalized_twice()
    {
        var h = Harness.WithBaseline(count: 19, baselineIds: Ids(1, 100).Append("orphan-not-in-registry"));
        // The registry also holds an id no container of the plan ever read (CN=Builtin, a legacy row): never emitted.
        h.Registry.UnionWith(Ids(1, 100));
        h.Registry.Add("builtin-administrators");
        h.Store.Results.AddRange(Results(Group, 19));
        h.Store.Seen[(Group, Step)] = new HashSet<string>(Ids(1, 100).Where(id => id is not "u7" and not "u42"));

        var first = await h.FinalizeAsync(PartitionDeleteDetectionMode.Enforce);
        var second = await h.FinalizeAsync(PartitionDeleteDetectionMode.Enforce);

        Assert.Equal(PartitionGroupOutcomes.Finalized, first.Outcome);
        Assert.Equal(PartitionGroupOutcomes.AlreadyFinalized, second.Outcome);
        var call = Assert.Single(h.Sink.Calls);
        Assert.Equal("domain.local", call.Source);
        Assert.Equal(new[] { "u42", "u7" }, call.Ids.OrderBy(i => i, StringComparer.Ordinal));
        Assert.Equal(new[] { "u42", "u7" }, h.Pruned.Single().Ids.OrderBy(i => i, StringComparer.Ordinal));
        Assert.Equal(Step, h.Pruned.Single().StepId);
        Assert.Equal(PartitionGroupOutcomes.Emitted, Assert.Single(first.Steps).Outcome);
        // The plan is the new baseline; the replaced baseline's ids are dropped.
        Assert.Equal(Group, h.Store.Baselines[(Project, Step)].GroupId);
        Assert.True(h.Store.Seen.ContainsKey((Group, Step)));
        Assert.False(h.Store.Seen.ContainsKey((PreviousGroup, Step)));
    }

    [Fact]
    public async Task More_disappeared_than_the_cap_is_refused_and_the_baseline_is_kept()
    {
        var h = Harness.WithBaseline(count: 19, baselineIds: Ids(1, 100));
        h.Registry.UnionWith(Ids(1, 100));
        h.Store.Results.AddRange(Results(Group, 19));
        h.Store.Seen[(Group, Step)] = new HashSet<string>(Ids(12, 100)); // 11 of 100 gone, cap 10

        var result = await h.FinalizeAsync(PartitionDeleteDetectionMode.Enforce, percent: 10);

        Assert.Equal(PartitionGroupOutcomes.RefusedOverCap, Assert.Single(result.Steps).Outcome);
        Assert.Empty(h.Sink.Calls);
        Assert.Empty(h.Pruned);
        Assert.Equal(PreviousGroup, h.Store.Baselines[(Project, Step)].GroupId);
        Assert.Contains(h.Logs, l => l.Contains("REFUSED") && l.Contains("11 of 100") && l.Contains("limit of 10"));
    }

    [Fact]
    public async Task An_empty_prior_set_emits_nothing()
    {
        var h = Harness.WithBaseline(count: 19, baselineIds: Ids(1, 10));
        // The registry holds none of the baseline's ids (skip-unchanged never registered them): nothing to compare.
        h.Store.Results.AddRange(Results(Group, 19));
        h.Store.Seen[(Group, Step)] = new HashSet<string>(Ids(1, 5));

        var result = await h.FinalizeAsync(PartitionDeleteDetectionMode.Enforce);

        Assert.Equal(PartitionGroupOutcomes.NoPrior, Assert.Single(result.Steps).Outcome);
        Assert.Empty(h.Sink.Calls);
    }

    [Fact]
    public async Task A_plan_that_read_nothing_while_prior_exists_is_refused()
    {
        var h = Harness.WithBaseline(count: 19, baselineIds: Ids(1, 10));
        h.Registry.UnionWith(Ids(1, 10));
        h.Store.Results.AddRange(Results(Group, 19));

        var result = await h.FinalizeAsync(PartitionDeleteDetectionMode.Enforce);

        Assert.Equal(PartitionGroupOutcomes.RefusedEmptyRead, Assert.Single(result.Steps).Outcome);
        Assert.Empty(h.Sink.Calls);
        Assert.Equal(PreviousGroup, h.Store.Baselines[(Project, Step)].GroupId);
    }

    [Fact]
    public async Task A_changed_container_list_rebaselines_and_emits_nothing()
    {
        var h = Harness.WithBaseline(count: 19, baselineIds: Ids(1, 100), fingerprint: "a-different-plan");
        h.Registry.UnionWith(Ids(1, 100));
        h.Store.Results.AddRange(Results(Group, 19));
        h.Store.Seen[(Group, Step)] = new HashSet<string>(Ids(1, 98));

        var result = await h.FinalizeAsync(PartitionDeleteDetectionMode.Enforce);

        Assert.Equal(PartitionGroupOutcomes.Rebaselined, Assert.Single(result.Steps).Outcome);
        Assert.Empty(h.Sink.Calls);
        Assert.Equal(Group, h.Store.Baselines[(Project, Step)].GroupId);
        Assert.Contains(h.Logs, l => l.Contains("container list changed"));
    }

    [Fact]
    public async Task The_first_complete_plan_is_the_baseline_and_emits_nothing()
    {
        var h = new Harness();
        h.Registry.UnionWith(Ids(1, 100));
        h.Store.Results.AddRange(Results(Group, 3));
        h.Store.Seen[(Group, Step)] = new HashSet<string>(Ids(1, 50));

        var result = await h.FinalizeAsync(PartitionDeleteDetectionMode.Enforce);

        Assert.Equal(PartitionGroupOutcomes.Baseline, Assert.Single(result.Steps).Outcome);
        Assert.Empty(h.Sink.Calls);
        Assert.Equal(Group, h.Store.Baselines[(Project, Step)].GroupId);
    }

    [Fact]
    public async Task A_step_one_container_did_not_arm_takes_no_part()
    {
        var h = Harness.WithBaseline(count: 3, baselineIds: Ids(1, 10));
        h.Registry.UnionWith(Ids(1, 10));
        var results = Results(Group, 3).ToList();
        results[1].ArmedStepIds = null;
        h.Store.Results.AddRange(results);
        h.Store.Seen[(Group, Step)] = new HashSet<string>(Ids(1, 9));

        var result = await h.FinalizeAsync(PartitionDeleteDetectionMode.Enforce);

        Assert.Equal(PartitionGroupOutcomes.Finalized, result.Outcome);
        Assert.Empty(result.Steps);
        Assert.Empty(h.Sink.Calls);
    }

    [Fact]
    public async Task Report_only_computes_and_logs_but_emits_and_prunes_nothing()
    {
        var h = Harness.WithBaseline(count: 19, baselineIds: Ids(1, 100));
        h.Registry.UnionWith(Ids(1, 100));
        h.Store.Results.AddRange(Results(Group, 19));
        h.Store.Seen[(Group, Step)] = new HashSet<string>(Ids(1, 97));

        var result = await h.FinalizeAsync(PartitionDeleteDetectionMode.ReportOnly);

        var step = Assert.Single(result.Steps);
        Assert.Equal(PartitionGroupOutcomes.Reported, step.Outcome);
        Assert.Equal(new[] { "u100", "u98", "u99" }, step.Disappeared);
        Assert.Empty(h.Sink.Calls);
        Assert.Empty(h.Pruned);
        Assert.Contains(h.Logs, l => l.Contains("report only") && l.Contains("would tombstone 3 of 100") && l.Contains("u98"));
        Assert.Contains("\"outcome\":\"Reported\"", h.Store.Completions[Group].Detail);
        // A report does not move the baseline, so switching to Enforce acts on everything reported.
        Assert.Equal(PreviousGroup, h.Store.Baselines[(Project, Step)].GroupId);
    }

    [Fact]
    public async Task Report_only_samples_at_most_twenty_ids()
    {
        var h = Harness.WithBaseline(count: 3, baselineIds: Ids(1, 1000));
        h.Registry.UnionWith(Ids(1, 1000));
        h.Store.Results.AddRange(Results(Group, 3));
        h.Store.Seen[(Group, Step)] = new HashSet<string>(Ids(51, 1000));

        await h.FinalizeAsync(PartitionDeleteDetectionMode.ReportOnly);

        var line = Assert.Single(h.Logs, l => l.Contains("would tombstone 50 of 1000"));
        Assert.Equal(20, line[(line.IndexOf("Sample: ", StringComparison.Ordinal) + 8)..].Split(", ").Length);
    }

    [Fact]
    public async Task Off_does_nothing_at_all()
    {
        var h = Harness.WithBaseline(count: 3, baselineIds: Ids(1, 10));
        h.Registry.UnionWith(Ids(1, 10));
        h.Store.Results.AddRange(Results(Group, 3));
        h.Store.Seen[(Group, Step)] = new HashSet<string>(Ids(1, 5));

        var result = await h.FinalizeAsync(PartitionDeleteDetectionMode.Off);

        Assert.Equal(PartitionGroupOutcomes.Off, result.Outcome);
        Assert.Empty(h.Store.Claims);
        Assert.Empty(h.Sink.Calls);
    }

    [Fact]
    public async Task Cancellation_does_nothing()
    {
        var h = Harness.WithBaseline(count: 3, baselineIds: Ids(1, 10));
        h.Registry.UnionWith(Ids(1, 10));
        h.Store.Results.AddRange(Results(Group, 3));
        h.Store.Seen[(Group, Step)] = new HashSet<string>(Ids(1, 9));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var result = await h.FinalizeAsync(PartitionDeleteDetectionMode.Enforce, ct: cancelled.Token);

        Assert.Equal(PartitionGroupOutcomes.Cancelled, result.Outcome);
        Assert.Empty(h.Store.Claims);
        Assert.Empty(h.Sink.Calls);
    }

    [Fact]
    public async Task A_sink_that_aborts_keeps_the_baseline_so_the_next_plan_retries()
    {
        var h = Harness.WithBaseline(count: 3, baselineIds: Ids(1, 100));
        h.Registry.UnionWith(Ids(1, 100));
        h.Store.Results.AddRange(Results(Group, 3));
        h.Store.Seen[(Group, Step)] = new HashSet<string>(Ids(1, 98));
        h.Sink.Abort = true;

        var result = await h.FinalizeAsync(PartitionDeleteDetectionMode.Enforce);

        Assert.Equal(PartitionGroupOutcomes.EmitFailed, Assert.Single(result.Steps).Outcome);
        Assert.Single(h.Sink.Calls);
        Assert.Empty(h.Pruned);
        Assert.Equal(PreviousGroup, h.Store.Baselines[(Project, Step)].GroupId);
    }

    // ── wiring ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Migration_38_adds_the_four_tables_idempotently()
    {
        var migrator = new DatabaseMigrator("Server=offline;Database=offline;", NullLogger<DatabaseMigrator>.Instance);
        var m38 = migrator.GetRequiredMigrations(new SchemaAnalysisResult { CurrentVersion = 37 }).First();
        Assert.Equal(38, m38.Version);
        foreach (var table in new[] { "PartitionSeenIds", "PartitionRunResults", "PartitionGroupFinalizations", "PartitionStepBaselines" })
            Assert.Contains($"IF OBJECT_ID('dbo.{table}','U') IS NULL", m38.SqlScript);
        Assert.Contains("CONSTRAINT [PK_PartitionGroupFinalizations] PRIMARY KEY CLUSTERED ([GroupId])", m38.SqlScript);
    }

    [Fact]
    public void The_orchestrator_holds_a_container_read_for_the_plan_and_finalizes_at_run_end()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Conduit.Sync", "Orchestration", "SyncProjectOrchestrator.cs"));
        Assert.Contains("_partitionGroups.RecordSeenAsync(project.Id, group.GroupId, step.Id, seenSourceIds)", source);
        Assert.Contains("await FinishPartitionGroupAsync(project, run.Id, group, status, cancellationToken);", source);
        Assert.Contains("new PartitionGroupFinalizer(_partitionGroups).FinalizeAsync(request, ct)", source);
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Conduit.Web", "Program.cs"));
        Assert.Contains("AddScoped<Conduit.DataAccess.Repositories.IPartitionGroupRepository, Conduit.DataAccess.Repositories.PartitionGroupRepository>()", program);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────

    private static IEnumerable<string> Ids(int from, int to) => Enumerable.Range(from, to - from + 1).Select(i => "u" + i);

    private static IEnumerable<PartitionRunResult> Results(Guid group, int count, string dnPrefix = "OU=Sales") =>
        Enumerable.Range(1, count).Select(i => new PartitionRunResult
        {
            GroupId = group,
            PartitionIndex = i,
            PartitionCount = count,
            RunId = Guid.NewGuid(),
            ProjectId = Project,
            Succeeded = true,
            AllStepsCompleteRead = true,
            ContainerKey = PartitionGroupFinalizer.ContainerKey(new SyncRunPartition(i, count, "c" + i,
                new[] { i == 1 ? "DC=example,DC=com" : $"{dnPrefix}{i},DC=example,DC=com" }, i == 1, i == 1)),
            ArmedStepIds = Step.ToString("N"),
        });

    [Theory]
    [InlineData("src/Conduit.Connectors.ActiveDirectory/ActiveDirectoryContainerBrowser.cs")]
    [InlineData("src/Conduit.Connectors.ActiveRoles/ActiveRolesContainerBrowser.cs")]
    public void The_container_browse_lists_CN_Builtin_so_a_split_run_reads_the_built_in_groups(string browser)
    {
        // A run split by container is planned from this browse; CN=Builtin (a builtinDomain) was not listed, so no
        // partition ever read the built-in groups and the catalog's 29 were frozen at their last unsplit read.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), browser.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Contains("\"(|(objectClass=organizationalUnit)(objectClass=container)(objectClass=builtinDomain))\"", source);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conduit.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Conduit.sln not found above the test output.");
    }

    private sealed class Harness
    {
        public FakeStore Store { get; } = new();
        public FakeSink Sink { get; } = new();
        public HashSet<string> Registry { get; } = new(StringComparer.Ordinal);
        public List<(Guid StepId, IReadOnlyCollection<string> Ids)> Pruned { get; } = new();
        public List<string> Logs { get; } = new();

        /// <summary>A previous complete group of the same plan (same fingerprint unless told otherwise) is the step's baseline.</summary>
        public static Harness WithBaseline(int count, IEnumerable<string>? baselineIds = null, string? fingerprint = null)
        {
            var h = new Harness();
            h.Store.Baselines[(Project, Step)] = new PartitionStepBaseline(Project, Step, PreviousGroup,
                fingerprint ?? PartitionGroupFinalizer.PlanFingerprint(Results(Group, count)));
            h.Store.Seen[(PreviousGroup, Step)] = new HashSet<string>(baselineIds ?? Ids(1, 10), StringComparer.Ordinal);
            return h;
        }

        public Task<PartitionGroupResult> FinalizeAsync(PartitionDeleteDetectionMode mode, int percent = 10, CancellationToken ct = default) =>
            new PartitionGroupFinalizer(Store).FinalizeAsync(new PartitionGroupFinalizeRequest(
                Project, Group, new PartitionDeleteDetectionSettings(mode, percent), Sink, "domain.local",
                _ => Task.FromResult<IReadOnlyCollection<string>>(Registry.ToList()),
                (stepId, ids) => { Pruned.Add((stepId, ids.ToList())); return Task.CompletedTask; },
                (level, message) => { Logs.Add($"{level}: {message}"); return Task.CompletedTask; }), ct);
    }

    private sealed class FakeSink : ITombstoneEmittingSink
    {
        public List<(string Source, IReadOnlyList<string> Ids)> Calls { get; } = new();
        public bool Abort { get; set; }

        public Task<TombstoneEmitResult> EmitTombstonesAsync(string source, IReadOnlyList<string> sourceUniqueIds, CancellationToken cancellationToken)
        {
            Calls.Add((source, sourceUniqueIds.ToList()));
            return Task.FromResult(Abort
                ? new TombstoneEmitResult { Succeeded = true, Aborted = true, AbortReason = "cap", Requested = sourceUniqueIds.Count }
                : new TombstoneEmitResult
                {
                    Succeeded = true,
                    Requested = sourceUniqueIds.Count,
                    Matched = sourceUniqueIds.Count,
                    SoftDeleted = sourceUniqueIds.Count,
                    PrunableIds = sourceUniqueIds.ToList(),
                });
        }
    }

    private sealed class FakeStore : IPartitionGroupRepository
    {
        public List<PartitionRunResult> Results { get; } = new();
        public Dictionary<(Guid Group, Guid Step), HashSet<string>> Seen { get; } = new();
        public HashSet<Guid> Claims { get; } = new();
        public Dictionary<Guid, (string Outcome, string? Detail)> Completions { get; } = new();
        public Dictionary<(Guid Project, Guid Step), PartitionStepBaseline> Baselines { get; } = new();

        public Task RecordSeenAsync(Guid projectId, Guid groupId, Guid stepId, IReadOnlyCollection<string> externalIds)
        {
            if (!Seen.TryGetValue((groupId, stepId), out var set)) Seen[(groupId, stepId)] = set = new(StringComparer.Ordinal);
            set.UnionWith(externalIds);
            return Task.CompletedTask;
        }

        public Task<HashSet<string>> LoadSeenAsync(Guid groupId, Guid stepId) =>
            Task.FromResult(Seen.TryGetValue((groupId, stepId), out var set) ? new HashSet<string>(set, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal));

        public Task RecordResultAsync(PartitionRunResult result) { Results.Add(result); return Task.CompletedTask; }

        public Task<IReadOnlyList<PartitionRunResult>> GetResultsAsync(Guid groupId) =>
            Task.FromResult<IReadOnlyList<PartitionRunResult>>(Results.Where(r => r.GroupId == groupId).ToList());

        public Task<bool> TryClaimAsync(Guid groupId, Guid projectId, string planFingerprint) => Task.FromResult(Claims.Add(groupId));

        public Task CompleteAsync(Guid groupId, string outcome, string? detailJson) { Completions[groupId] = (outcome, detailJson); return Task.CompletedTask; }

        public Task<PartitionStepBaseline?> GetBaselineAsync(Guid projectId, Guid stepId) =>
            Task.FromResult(Baselines.TryGetValue((projectId, stepId), out var b) ? b : null);

        public Task AdvanceBaselineAsync(Guid projectId, Guid stepId, Guid groupId, string planFingerprint)
        {
            if (Baselines.TryGetValue((projectId, stepId), out var old) && old.GroupId != groupId) Seen.Remove((old.GroupId, stepId));
            Baselines[(projectId, stepId)] = new PartitionStepBaseline(projectId, stepId, groupId, planFingerprint);
            return Task.CompletedTask;
        }

        public Task ReleaseSeenAsync(Guid groupId, IReadOnlyCollection<Guid> keepStepIds)
        {
            foreach (var key in Seen.Keys.Where(k => k.Group == groupId && !keepStepIds.Contains(k.Step)).ToList()) Seen.Remove(key);
            return Task.CompletedTask;
        }

        public Task<int> PurgeStaleAsync(DateTime olderThanUtc) => Task.FromResult(0);
    }
}
