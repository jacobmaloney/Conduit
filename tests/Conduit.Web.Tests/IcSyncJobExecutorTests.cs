using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Conduit.Core.SyncModels;
using Conduit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// SYNC-SERVICE-06: the Conduit half of the execution-server pin. IdentityCenter stamps
/// <c>JobQueue.TargetServerId</c> from the project and only that server may claim the job
/// (SYNC-SERVICE-05); these tests cover what this installation does with a job once it has it.
///
/// Everything here is offline: a stubbed <see cref="HttpMessageHandler"/> plays IdentityCenter's
/// jobs API and a fake <see cref="IIcSyncProjectJobRunner"/> stands in for the orchestrator, so
/// "the run never started" is an observed fact rather than a reading of the code.
/// </summary>
public class IcSyncJobExecutorTests
{
    private static readonly Guid AgentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid JobId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid IcProjectId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ConduitProjectId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid RunId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private const string BaseUrl = "https://ic.example.com";

    // ── Fakes ───────────────────────────────────────────────────────────────

    private sealed record Recorded(string Method, string Path, string Body);

    private sealed class FakeIc : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = new();

        /// <summary>Scripted claim answers, in order; the tail answer repeats.</summary>
        public Queue<Func<HttpResponseMessage>> Claims { get; } = new();

        /// <summary>Scripted GET /api/jobs/{id} bodies; the last one repeats.</summary>
        public Queue<string> JobReads { get; } = new();

        /// <summary>GET /api/agents/{id} body, or null for 404 (capability unknown).</summary>
        public string? AgentJson { get; set; }

        public int CompleteStatus { get; set; } = 200;

        /// <summary>POST /api/jobs/{id}/partitions: the status and body IdentityCenter answers a plan with.</summary>
        public int PlanStatus { get; set; } = 200;
        public string? PlanBody { get; set; }

        /// <summary>GET /api/jobs/{id}/project (ZERO-TOUCH-01): the status and body IdentityCenter answers a definition read with.</summary>
        public int DefinitionStatus { get; set; } = 200;
        public string? DefinitionBody { get; set; }
        public int DefinitionReads { get; private set; }

        private string? _lastJobRead;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(new Recorded(request.Method.Method, path, body));

            if (request.Method == HttpMethod.Get && path.StartsWith("/api/agents/", StringComparison.Ordinal))
                return AgentJson is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Ok(AgentJson);

            if (path == "/api/jobs/claim")
                return Claims.Count > 0 ? Claims.Dequeue()() : Ok(NoWorkJson);

            if (request.Method == HttpMethod.Get && path.EndsWith("/project", StringComparison.Ordinal))
            {
                DefinitionReads++;
                return new HttpResponseMessage((HttpStatusCode)DefinitionStatus) { Content = new StringContent(DefinitionBody ?? "{}") };
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/api/jobs/", StringComparison.Ordinal))
            {
                if (JobReads.Count > 0) _lastJobRead = JobReads.Dequeue();
                return _lastJobRead is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Ok(_lastJobRead);
            }

            if (path.EndsWith("/partitions", StringComparison.Ordinal))
                return new HttpResponseMessage((HttpStatusCode)PlanStatus) { Content = new StringContent(PlanBody ?? "{}") };
            if (path.EndsWith("/progress", StringComparison.Ordinal)) return Ok("{}");
            if (path.EndsWith("/complete", StringComparison.Ordinal))
                return new HttpResponseMessage((HttpStatusCode)CompleteStatus) { Content = new StringContent("{}") };

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Ok(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json) };

        public Recorded? Last(string method, string suffix) =>
            Requests.LastOrDefault(r => r.Method == method && r.Path.EndsWith(suffix, StringComparison.Ordinal));
    }

    private sealed class FakeRunner : IIcSyncProjectJobRunner
    {
        public SyncProject? Bound { get; set; }
        public int ResolveCalls { get; private set; }
        public int ExecuteCalls { get; private set; }
        public Guid? LastResolved { get; private set; }
        public SyncProject? LastExecuted { get; private set; }
        public string? LastTriggeredBy { get; private set; }
        public bool RunSawCancellation { get; private set; }

        public IcSyncRunOutcome Outcome { get; set; } =
            new(true, RunId, "Succeeded", 0, 0, 0, "ok", "{}");

        /// <summary>When true the fake reports progress once and then honours the token, like a real run.</summary>
        public bool ReportProgress { get; set; }

        /// <summary>Stands in for a run whose roll-up landed before the cancel propagated.</summary>
        public bool IgnoreCancellation { get; set; }

        public Task<SyncProject?> ResolveAsync(Guid identityCenterProjectId)
        {
            ResolveCalls++;
            LastResolved = identityCenterProjectId;
            return Task.FromResult(Bound);
        }

        public SyncRunPartition? LastPartition { get; private set; }
        public int PlanCalls { get; private set; }
        public IcPartitionPlan Plan { get; set; } = new(Array.Empty<SyncRunPartition>(), null);

        public Task<IcPartitionPlan> PlanPartitionsAsync(SyncProject project, CancellationToken ct)
        {
            PlanCalls++;
            return Task.FromResult(Plan);
        }

        public async Task<IcSyncRunOutcome> ExecuteAsync(SyncProject project, string triggeredBy,
            Func<IcSyncRunProgress, CancellationToken, Task> onProgress, CancellationToken ct, SyncRunPartition? partition = null)
        {
            ExecuteCalls++;
            LastExecuted = project;
            LastTriggeredBy = triggeredBy;
            LastPartition = partition;

            if (ReportProgress)
            {
                await onProgress(new IcSyncRunProgress(RunId, "Running", 7, 5, 2, "running"), ct);
                RunSawCancellation = ct.IsCancellationRequested;
                if (RunSawCancellation && !IgnoreCancellation)
                    return new IcSyncRunOutcome(false, RunId, "Cancelled", 7, 5, 2, "Conduit run cancelled.", "{}");
            }
            return Outcome;
        }
    }

    private sealed class FakeMirror : IIcProjectMirror
    {
        public SyncProject? Produces { get; set; }
        public string? Refusal { get; set; }
        public string Action { get; set; } = IcProjectMirrorOutcome.Created;
        public int Calls { get; private set; }
        public IcProjectDefinition? LastDefinition { get; private set; }
        public SyncProject? LastExisting { get; private set; }
        public string? LastBaseUrl { get; private set; }

        public Task<IcProjectMirrorOutcome> EnsureAsync(string baseUrl, IcProjectDefinition definition, SyncProject? existing, CancellationToken ct)
        {
            Calls++;
            LastBaseUrl = baseUrl;
            LastDefinition = definition;
            LastExisting = existing;
            return Task.FromResult(Refusal is not null ? IcProjectMirrorOutcome.Refused(Refusal) : new IcProjectMirrorOutcome(Produces ?? existing, null, Action));
        }
    }

    private const string DefinitionJson = """
        {"projectId":"33333333-3333-3333-3333-333333333333","name":"HR feed","projectType":"ObjectSync",
         "sourceConnection":{"id":"1ee0c1a0-8ecb-4b7c-b4e5-3a3b76430453","name":"Domain.local","providerType":"ActiveDirectory","transport":"Conduit","agentId":"11111111-1111-1111-1111-111111111111"},
         "workflows":[],"fingerprint":"f1"}
        """;

    private static async Task<(IcSyncJobTickResult Result, FakeIc Ic, FakeRunner Runner, FakeMirror Mirror)>
        RunWithMirrorAsync(Action<FakeIc> script, Action<FakeRunner>? arrange, Action<FakeMirror>? mirror)
    {
        var ic = new FakeIc();
        script(ic);
        var runner = new FakeRunner();
        arrange?.Invoke(runner);
        var fake = new FakeMirror();
        mirror?.Invoke(fake);
        var pump = new IcSyncJobPump(runner, new IcAgentStatusService(), NullLogger<IcSyncJobPump>.Instance, mirror: fake);
        var channel = new IcSyncJobChannel(new HttpClient(ic));
        var result = await pump.RunOnceAsync(channel, BaseUrl, AgentId, CancellationToken.None);
        return (result, ic, runner, fake);
    }

    // ── Wire fixtures ───────────────────────────────────────────────────────

    private const string NoWorkJson = """{"success":false,"message":"No jobs available"}""";

    private static string ClaimedJson(
        string jobType = "SyncProject",
        string? relatedEntityId = "33333333-3333-3333-3333-333333333333",
        string relatedEntityType = "SyncProject",
        string payload = """{\"TriggerType\":\"Manual\",\"TriggeredBy\":\"nora.quint\",\"TenantSlug\":\"acme\"}""",
        bool cancellationRequested = false) =>
        $$"""
        {
          "success": true,
          "job": {
            "id": "{{JobId}}",
            "jobType": "{{jobType}}",
            "jobName": "Manual trigger: SyncProject {{IcProjectId}}",
            "relatedEntityId": {{(relatedEntityId is null ? "null" : $"\"{relatedEntityId}\"")}},
            "relatedEntityType": "{{relatedEntityType}}",
            "payloadJson": "{{payload}}",
            "targetServerId": "{{AgentId}}",
            "cancellationRequested": {{(cancellationRequested ? "true" : "false")}}
          }
        }
        """;

    private static string JobReadJson(bool cancellationRequested) =>
        $$"""
        { "id": "{{JobId}}", "jobType": "SyncProject", "status": "Processing",
          "relatedEntityId": "{{IcProjectId}}", "relatedEntityType": "SyncProject",
          "targetServerId": "{{AgentId}}", "cancellationRequested": {{(cancellationRequested ? "true" : "false")}} }
        """;

    private static SyncProject Project(bool enabled = true, bool running = false, Guid? boundTo = null) => new()
    {
        Id = ConduitProjectId,
        Name = "HR feed",
        IsEnabled = enabled,
        IsRunning = running,
        IdentityCenterProjectId = boundTo ?? IcProjectId
    };

    private static async Task<(IcSyncJobTickResult Result, FakeIc Ic, FakeRunner Runner, IcAgentStatusService Status)>
        RunAsync(Action<FakeIc> script, Action<FakeRunner>? arrange = null)
    {
        var ic = new FakeIc();
        script(ic);
        var runner = new FakeRunner();
        arrange?.Invoke(runner);
        var status = new IcAgentStatusService();
        var pump = new IcSyncJobPump(runner, status, NullLogger<IcSyncJobPump>.Instance);
        var channel = new IcSyncJobChannel(new HttpClient(ic));
        var result = await pump.RunOnceAsync(channel, BaseUrl, AgentId, CancellationToken.None);
        return (result, ic, runner, status);
    }

    private static JsonElement BodyOf(Recorded? recorded)
    {
        Assert.NotNull(recorded);
        return JsonDocument.Parse(recorded!.Body).RootElement.Clone();
    }

    // ── The claim request ───────────────────────────────────────────────────

    [Fact]
    public async Task Claim_asks_only_for_SyncProject_work_under_this_installations_own_agent_id()
    {
        var (_, ic, _, _) = await RunAsync(_ => { });

        var claim = BodyOf(ic.Last("POST", "/api/jobs/claim"));
        Assert.Equal(AgentId, claim.GetProperty("agentId").GetGuid());
        Assert.Equal(new[] { "SyncProject" }, claim.GetProperty("supportedJobTypes").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(1, claim.GetProperty("maxJobs").GetInt32());
    }

    // ── Happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Claimed_job_resolves_the_bound_project_runs_it_and_completes_with_the_runs_real_counts()
    {
        var (result, ic, runner, status) = await RunAsync(
            ic =>
            {
                ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) });
                ic.JobReads.Enqueue(JobReadJson(cancellationRequested: false));
            },
            r =>
            {
                r.Bound = Project();
                r.Outcome = new IcSyncRunOutcome(true, RunId, "Succeeded", 120, 118, 2, "Conduit run finished", """{"runId":"55555555-5555-5555-5555-555555555555"}""");
            });

        Assert.Equal(IcSyncJobTickResult.Executed, result);
        Assert.Equal(IcProjectId, runner.LastResolved);
        Assert.Equal(ConduitProjectId, runner.LastExecuted!.Id);
        Assert.Equal(1, runner.ExecuteCalls);

        var complete = BodyOf(ic.Last("POST", "/complete"));
        Assert.Equal(JobId, complete.GetProperty("jobId").GetGuid());
        Assert.Equal(AgentId, complete.GetProperty("agentId").GetGuid());
        Assert.True(complete.GetProperty("success").GetBoolean());
        Assert.Equal(120, complete.GetProperty("itemsProcessed").GetInt32());
        Assert.Equal(118, complete.GetProperty("itemsSucceeded").GetInt32());
        Assert.Equal(2, complete.GetProperty("itemsFailed").GetInt32());
        Assert.Equal(JsonValueKind.Null, complete.GetProperty("errorMessage").ValueKind);
        Assert.Contains("55555555-5555-5555-5555-555555555555", complete.GetProperty("resultJson").GetString());

        var snapshot = Assert.Single(status.Snapshot());
        Assert.Contains("Succeeded", snapshot.LastJobOutcome);
    }

    [Fact]
    public async Task Attribution_keeps_the_IdentityCenter_actor_but_never_claims_the_run_was_triggered_locally()
    {
        var (_, _, runner, _) = await RunAsync(
            ic => ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) }),
            r => r.Bound = Project());

        Assert.Equal("IdentityCenter:nora.quint", runner.LastTriggeredBy);
    }

    [Fact]
    public void Attribution_falls_back_to_the_job_when_the_payload_names_no_actor()
    {
        var job = new IcJobQueueEntry(JobId, "SyncProject", null, IcProjectId, "SyncProject", """{"TriggerType":"Scheduled"}""", AgentId, false);
        Assert.Equal($"IdentityCenter job {JobId}", IcSyncJobPump.TriggeredBy(job));
    }

    [Fact]
    public void Attribution_strips_control_characters_out_of_the_remote_actor_label()
    {
        var job = new IcJobQueueEntry(JobId, "SyncProject", null, IcProjectId, "SyncProject",
            "{\"TriggeredBy\":\"nora\\r\\nINJECTED\"}", AgentId, false);
        Assert.Equal("IdentityCenter:noraINJECTED", IcSyncJobPump.TriggeredBy(job));
    }

    // ── Refusals: the reason, and no run ────────────────────────────────────

    [Fact]
    public async Task An_unbound_IdentityCenter_project_fails_the_job_by_name_and_never_starts_a_run()
    {
        var (result, ic, runner, status) = await RunAsync(
            ic => ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) }),
            r => r.Bound = null);

        Assert.Equal(IcSyncJobTickResult.Refused, result);
        Assert.Equal(0, runner.ExecuteCalls);

        var complete = BodyOf(ic.Last("POST", "/complete"));
        Assert.False(complete.GetProperty("success").GetBoolean());
        Assert.Equal(
            $"No Conduit sync project is bound to IdentityCenter project {IcProjectId}; bind it on this installation before pinning work here.",
            complete.GetProperty("errorMessage").GetString());
        Assert.Equal(0, complete.GetProperty("itemsProcessed").GetInt32());
        Assert.Contains("No Conduit sync project is bound to IdentityCenter project", Assert.Single(status.Snapshot()).LastJobOutcome!);
    }

    // ── ZERO-TOUCH-01: the IdentityCenter project is the one definition ─────

    [Fact]
    public async Task An_unbound_project_is_mirrored_from_its_definition_and_run_in_the_same_claim()
    {
        var mirrored = Project();
        var (result, ic, runner, mirror) = await RunWithMirrorAsync(
            ic => { ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) }); ic.DefinitionBody = DefinitionJson; },
            r => r.Bound = null,
            m => m.Produces = mirrored);

        Assert.Equal(IcSyncJobTickResult.Executed, result);
        Assert.Equal(1, ic.DefinitionReads);
        Assert.Equal(1, mirror.Calls);
        Assert.Equal(BaseUrl, mirror.LastBaseUrl);
        Assert.Null(mirror.LastExisting);
        Assert.Equal(IcProjectId, mirror.LastDefinition!.ProjectId);
        Assert.Equal("Domain.local", mirror.LastDefinition.SourceConnection!.Name);
        Assert.Same(mirrored, runner.LastExecuted);
        Assert.True(BodyOf(ic.Last("POST", "/complete")).GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task A_bound_mirror_is_offered_its_definition_again_and_a_mirror_refusal_fails_the_job_by_name()
    {
        var existing = Project();
        var (result, ic, runner, mirror) = await RunWithMirrorAsync(
            ic => { ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) }); ic.DefinitionBody = DefinitionJson; },
            r => r.Bound = existing,
            m => m.Action = IcProjectMirrorOutcome.Refreshed);
        Assert.Equal(IcSyncJobTickResult.Executed, result);
        Assert.Same(existing, mirror.LastExisting);
        Assert.Same(existing, runner.LastExecuted);

        const string reason = "IdentityCenter project 'HR feed' reads connection 'Domain.local', and this Conduit has no active connected system of that name; add the connected system here (its directory and credentials) and the next run mirrors the project.";
        var (refused, ic2, runner2, _) = await RunWithMirrorAsync(
            ic => { ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) }); ic.DefinitionBody = DefinitionJson; },
            r => r.Bound = null,
            m => m.Refusal = reason);
        Assert.Equal(IcSyncJobTickResult.Refused, refused);
        Assert.Equal(0, runner2.ExecuteCalls);
        var complete = BodyOf(ic2.Last("POST", "/complete"));
        Assert.False(complete.GetProperty("success").GetBoolean());
        Assert.Equal(reason, complete.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task A_definition_that_cannot_be_read_refuses_an_unmirrored_project_by_name_but_runs_an_existing_mirror_as_it_is()
    {
        var (refused, ic, runner, mirror) = await RunWithMirrorAsync(
            ic => { ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) }); ic.DefinitionStatus = 403; ic.DefinitionBody = """{"error":"Job is not held by server x."}"""; },
            r => r.Bound = null,
            null);
        Assert.Equal(IcSyncJobTickResult.Refused, refused);
        Assert.Equal(0, mirror.Calls);
        Assert.Equal(0, runner.ExecuteCalls);
        var reason = BodyOf(ic.Last("POST", "/complete")).GetProperty("errorMessage").GetString()!;
        Assert.StartsWith($"IdentityCenter project {IcProjectId} has no mirror on this installation yet and its definition could not be read for job {JobId}", reason);
        Assert.Contains("HTTP 403: Job is not held by server x.", reason);

        var existing = Project();
        var (ran, _, runner2, mirror2) = await RunWithMirrorAsync(
            ic => { ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) }); ic.DefinitionStatus = 503; },
            r => r.Bound = existing,
            null);
        Assert.Equal(IcSyncJobTickResult.Executed, ran);
        Assert.Equal(0, mirror2.Calls);
        Assert.Same(existing, runner2.LastExecuted);
    }

    [Fact]
    public async Task A_container_item_of_a_split_run_does_not_read_the_definition_when_its_mirror_exists()
    {
        const string partitionPayload = """{\"TriggerType\":\"Manual\",\"TriggeredBy\":\"nora.quint\",\"Partition\":{\"Index\":2,\"Count\":3,\"Label\":\"OU=Sales,DC=example,DC=com\",\"IncludedBaseDNs\":[\"OU=Sales,DC=example,DC=com\"],\"OneLevel\":false,\"CarriesTombstones\":false,\"ParentJobId\":\"bbbbbbbb-0000-0000-0000-00000000000b\"}}""";
        var existing = Project();
        var (result, ic, runner, mirror) = await RunWithMirrorAsync(
            ic => { ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson(payload: partitionPayload)) }); ic.DefinitionBody = DefinitionJson; },
            r => r.Bound = existing,
            null);
        Assert.Equal(IcSyncJobTickResult.Executed, result);
        Assert.Equal(0, ic.DefinitionReads);
        Assert.Equal(0, mirror.Calls);
        Assert.Equal(2, runner.LastPartition!.Index);

        // Without a mirror the container item still reads the definition: the mirror is created from it before the read.
        var (created, ic2, _, mirror2) = await RunWithMirrorAsync(
            ic => { ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson(payload: partitionPayload)) }); ic.DefinitionBody = DefinitionJson; },
            r => r.Bound = null,
            m => m.Produces = Project());
        Assert.Equal(IcSyncJobTickResult.Executed, created);
        Assert.Equal(1, ic2.DefinitionReads);
        Assert.Equal(1, mirror2.Calls);
    }

    [Fact]
    public async Task A_disabled_local_project_fails_the_job_by_name_and_never_starts_a_run()
    {
        var (result, ic, runner, _) = await RunAsync(
            ic => ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) }),
            r => r.Bound = Project(enabled: false));

        Assert.Equal(IcSyncJobTickResult.Refused, result);
        Assert.Equal(0, runner.ExecuteCalls);
        var message = BodyOf(ic.Last("POST", "/complete")).GetProperty("errorMessage").GetString();
        Assert.Equal(
            $"Conduit sync project 'HR feed' ({ConduitProjectId}) is bound to IdentityCenter project {IcProjectId} but is disabled on this installation; enable it there before pinning work here.",
            message);
    }

    [Fact]
    public async Task A_project_already_running_here_fails_the_job_by_name_and_never_starts_a_second_run()
    {
        var (result, ic, runner, _) = await RunAsync(
            ic => ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) }),
            r => r.Bound = Project(running: true));

        Assert.Equal(IcSyncJobTickResult.Refused, result);
        Assert.Equal(0, runner.ExecuteCalls);
        Assert.Equal(
            $"Conduit sync project 'HR feed' ({ConduitProjectId}) already has a run in progress on this installation; IdentityCenter job {JobId} was not started.",
            BodyOf(ic.Last("POST", "/complete")).GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task A_job_naming_another_entity_type_is_refused_by_name_without_resolving_anything()
    {
        var (result, ic, runner, _) = await RunAsync(
            ic => ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(ClaimedJson(relatedEntityType: "PolicyEvaluation")) }),
            r => r.Bound = Project());

        Assert.Equal(IcSyncJobTickResult.Refused, result);
        Assert.Equal(0, runner.ResolveCalls);
        Assert.Equal(0, runner.ExecuteCalls);
        Assert.Contains("is related to 'PolicyEvaluation', not SyncProject", BodyOf(ic.Last("POST", "/complete")).GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task A_job_with_no_related_entity_is_refused_by_name_without_resolving_anything()
    {
        var (result, ic, runner, _) = await RunAsync(
            ic => ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(ClaimedJson(relatedEntityId: null)) }),
            r => r.Bound = Project());

        Assert.Equal(IcSyncJobTickResult.Refused, result);
        Assert.Equal(0, runner.ResolveCalls);
        Assert.Equal(0, runner.ExecuteCalls);
        Assert.Contains("names no related entity", BodyOf(ic.Last("POST", "/complete")).GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task A_resolved_project_bound_to_a_different_IdentityCenter_project_is_refused_rather_than_run()
    {
        var other = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var (result, ic, runner, _) = await RunAsync(
            ic => ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) }),
            r => r.Bound = Project(boundTo: other));

        Assert.Equal(IcSyncJobTickResult.Refused, result);
        Assert.Equal(0, runner.ExecuteCalls);
        Assert.Contains($"is bound to {other}", BodyOf(ic.Last("POST", "/complete")).GetProperty("errorMessage").GetString());
    }

    // ── Idle and transport ──────────────────────────────────────────────────

    [Fact]
    public async Task No_job_available_is_idle_and_runs_nothing()
    {
        var (result, ic, runner, status) = await RunAsync(_ => { });

        Assert.Equal(IcSyncJobTickResult.NoWork, result);
        Assert.Equal(0, runner.ResolveCalls);
        Assert.Equal(0, runner.ExecuteCalls);
        Assert.Null(ic.Last("POST", "/complete"));
        Assert.Contains("No jobs available", Assert.Single(status.Snapshot()).LastClaimOutcome);
    }

    [Fact]
    public async Task A_500_from_the_claim_endpoint_is_survivable_and_the_next_tick_still_claims()
    {
        var ic = new FakeIc();
        ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") });
        ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) });

        var runner = new FakeRunner { Bound = Project() };
        var pump = new IcSyncJobPump(runner, new IcAgentStatusService(), NullLogger<IcSyncJobPump>.Instance);
        var channel = new IcSyncJobChannel(new HttpClient(ic));

        Assert.Equal(IcSyncJobTickResult.ChannelUnavailable, await pump.RunOnceAsync(channel, BaseUrl, AgentId, CancellationToken.None));
        Assert.Equal(0, runner.ExecuteCalls);

        Assert.Equal(IcSyncJobTickResult.Executed, await pump.RunOnceAsync(channel, BaseUrl, AgentId, CancellationToken.None));
        Assert.Equal(1, runner.ExecuteCalls);
    }

    [Fact]
    public async Task A_transport_failure_is_survivable_and_runs_nothing()
    {
        var runner = new FakeRunner { Bound = Project() };
        var pump = new IcSyncJobPump(runner, new IcAgentStatusService(), NullLogger<IcSyncJobPump>.Instance);
        var channel = new IcSyncJobChannel(new HttpClient(new ThrowingHandler()));

        Assert.Equal(IcSyncJobTickResult.ChannelUnavailable, await pump.RunOnceAsync(channel, BaseUrl, AgentId, CancellationToken.None));
        Assert.Equal(0, runner.ExecuteCalls);
    }

    [Fact]
    public async Task A_job_pinned_to_another_execution_server_is_refused_by_name_and_never_resolved()
    {
        var other = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var json = ClaimedJson().Replace($"\"targetServerId\": \"{AgentId}\"", $"\"targetServerId\": \"{other}\"");
        var (result, ic, runner, _) = await RunAsync(
            ic => ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }),
            r => r.Bound = Project());

        Assert.Equal(IcSyncJobTickResult.Refused, result);
        Assert.Equal(0, runner.ResolveCalls);
        Assert.Equal(0, runner.ExecuteCalls);
        Assert.Equal(
            $"IdentityCenter job {JobId} is pinned to execution server {other}, not to this installation ({AgentId}); nothing was executed.",
            BodyOf(ic.Last("POST", "/complete")).GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task A_resolve_that_throws_still_completes_the_job_rather_than_leaving_it_claimed_and_silent()
    {
        var ic = new FakeIc();
        ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) });
        var pump = new IcSyncJobPump(new ThrowingResolver(), new IcAgentStatusService(), NullLogger<IcSyncJobPump>.Instance);

        var result = await pump.RunOnceAsync(new IcSyncJobChannel(new HttpClient(ic)), BaseUrl, AgentId, CancellationToken.None);

        Assert.Equal(IcSyncJobTickResult.Refused, result);
        var complete = BodyOf(ic.Last("POST", "/complete"));
        Assert.False(complete.GetProperty("success").GetBoolean());
        Assert.Contains("the connection pool is exhausted", complete.GetProperty("errorMessage").GetString());
    }

    // ── SPLIT-ENUM-01: plan items and container items ───────────────────────

    private const string PlanPayload = """{\"TriggerType\":\"Manual\",\"TriggeredBy\":\"nora.quint\",\"Split\":\"Container\"}""";
    private const string ContainerPayload = """{\"TriggerType\":\"Manual\",\"TriggeredBy\":\"nora.quint\",\"Partition\":{\"Index\":3,\"Count\":12,\"Label\":\"OU=Sales,DC=example,DC=com\",\"IncludedBaseDNs\":[\"OU=Sales,DC=example,DC=com\"],\"OneLevel\":false,\"CarriesTombstones\":false,\"ParentJobId\":\"bbbbbbbb-0000-0000-0000-00000000000b\"}}""";

    private static HttpResponseMessage Claimed(string payload) => new(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson(payload: payload)) };

    private static IReadOnlyList<SyncRunPartition> ThreeContainers() => new[]
    {
        new SyncRunPartition(1, 3, "DC=example,DC=com (this level)", new[] { "DC=example,DC=com" }, true, true),
        new SyncRunPartition(2, 3, "OU=Sales,DC=example,DC=com", new[] { "OU=Sales,DC=example,DC=com" }, false, false),
        new SyncRunPartition(3, 3, "OU=Plant,DC=example,DC=com", new[] { "OU=Plant,DC=example,DC=com" }, false, false),
    };

    private static string QueuedAnswer(params string[] servers) => JsonSerializer.Serialize(new
    {
        outcome = "Queued",
        children = servers.Select((s, i) => new { jobId = Guid.NewGuid(), index = i + 1, label = "c" + (i + 1), serverName = s }).ToArray()
    });

    [Fact]
    public async Task A_plan_item_is_planned_here_and_its_containers_go_to_IdentityCenter_one_item_each()
    {
        var (result, ic, runner, _) = await RunAsync(
            ic => { ic.Claims.Enqueue(() => Claimed(PlanPayload)); ic.PlanBody = QueuedAnswer("Conduit A", "Conduit B", "Conduit A"); },
            r => { r.Bound = Project(); r.Plan = new IcPartitionPlan(ThreeContainers(), null); });

        Assert.Equal(IcSyncJobTickResult.Executed, result);
        Assert.Equal(1, runner.PlanCalls);
        Assert.Equal(0, runner.ExecuteCalls);                     // the plan item reads nothing itself

        var plan = BodyOf(ic.Last("POST", "/partitions"));
        Assert.Equal(AgentId, plan.GetProperty("agentId").GetGuid());
        var partitions = plan.GetProperty("partitions").EnumerateArray().ToList();
        Assert.Equal(3, partitions.Count);
        Assert.True(partitions[0].GetProperty("oneLevel").GetBoolean());
        Assert.True(partitions[0].GetProperty("carriesTombstones").GetBoolean());
        Assert.False(partitions[1].GetProperty("oneLevel").GetBoolean());
        Assert.Equal("OU=Sales,DC=example,DC=com", partitions[1].GetProperty("includedBaseDNs")[0].GetString());

        var complete = BodyOf(ic.Last("POST", "/complete"));
        Assert.True(complete.GetProperty("success").GetBoolean());
        var receipt = JsonDocument.Parse(complete.GetProperty("resultJson").GetString()!).RootElement;
        Assert.Equal(3, receipt.GetProperty("partitions").GetInt32());
        Assert.Contains("Split into 3 containers: Conduit A x2, Conduit B x1.", receipt.GetProperty("resultMessage").GetString());
        var progress = BodyOf(ic.Last("POST", "/progress"));
        Assert.Contains("Split into 3 containers", progress.GetProperty("progressMessage").GetString());
    }

    [Fact]
    public async Task A_plan_whose_source_has_no_containers_is_completed_unsuccessfully_with_the_reason_and_nothing_is_read()
    {
        var reason = IcSyncJobReasons.NoContainers(Project(), "EntraID");
        var (result, ic, runner, _) = await RunAsync(
            ic => ic.Claims.Enqueue(() => Claimed(PlanPayload)),
            r => { r.Bound = Project(); r.Plan = IcPartitionPlan.Refused(reason); });

        Assert.Equal(IcSyncJobTickResult.Refused, result);
        Assert.Equal(0, runner.ExecuteCalls);
        Assert.Null(ic.Last("POST", "/partitions"));
        var complete = BodyOf(ic.Last("POST", "/complete"));
        Assert.False(complete.GetProperty("success").GetBoolean());
        Assert.Equal(reason, complete.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task A_scope_with_one_container_runs_whole_and_says_so()
    {
        var (result, ic, runner, _) = await RunAsync(
            ic => ic.Claims.Enqueue(() => Claimed(PlanPayload)),
            r => { r.Bound = Project(); r.Plan = new IcPartitionPlan(ThreeContainers().Take(1).ToList(), null); });

        Assert.Equal(IcSyncJobTickResult.Executed, result);
        Assert.Equal(1, runner.ExecuteCalls);
        Assert.Null(runner.LastPartition);                        // the whole scope, not the one container one level deep
        Assert.Null(ic.Last("POST", "/partitions"));
        Assert.Contains("ran whole", BodyOf(ic.Requests.First(r => r.Path.EndsWith("/progress", StringComparison.Ordinal))).GetProperty("progressMessage").GetString());
        Assert.True(BodyOf(ic.Last("POST", "/complete")).GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task A_plan_IdentityCenter_refuses_is_completed_unsuccessfully_with_its_reason_and_nothing_is_read()
    {
        var (result, ic, runner, _) = await RunAsync(
            ic =>
            {
                ic.Claims.Enqueue(() => Claimed(PlanPayload));
                ic.PlanStatus = 409;
                ic.PlanBody = """{"outcome":"Refused","reason":"Partitions not queued: job is not claimed by this server.","children":[]}""";
            },
            r => { r.Bound = Project(); r.Plan = new IcPartitionPlan(ThreeContainers(), null); });

        Assert.Equal(IcSyncJobTickResult.Refused, result);
        Assert.Equal(0, runner.ExecuteCalls);
        var complete = BodyOf(ic.Last("POST", "/complete"));
        Assert.False(complete.GetProperty("success").GetBoolean());
        Assert.Equal(IcSyncJobReasons.PlanNotAccepted(JobId, "Partitions not queued: job is not claimed by this server."), complete.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task A_container_item_runs_the_project_reading_only_its_container()
    {
        var (result, ic, runner, _) = await RunAsync(
            ic => ic.Claims.Enqueue(() => Claimed(ContainerPayload)),
            r => r.Bound = Project());

        Assert.Equal(IcSyncJobTickResult.Executed, result);
        Assert.Equal(0, runner.PlanCalls);
        Assert.Equal(1, runner.ExecuteCalls);
        Assert.NotNull(runner.LastPartition);
        Assert.Equal((3, 12, false, false), (runner.LastPartition!.Index, runner.LastPartition.Count, runner.LastPartition.OneLevel, runner.LastPartition.CarriesTombstones));
        Assert.Equal(new[] { "OU=Sales,DC=example,DC=com" }, runner.LastPartition.IncludedBaseDNs);
        Assert.Equal("IdentityCenter:nora.quint partition 3/12", runner.LastTriggeredBy);
        Assert.Null(ic.Last("POST", "/partitions"));
    }

    [Fact]
    public async Task A_container_item_whose_partition_cannot_be_read_is_refused_not_run_as_a_whole_read()
    {
        var (result, ic, runner, _) = await RunAsync(
            ic => ic.Claims.Enqueue(() => Claimed("""{\"TriggerType\":\"Manual\",\"Partition\":{\"Index\":1}}""")),
            r => r.Bound = Project());

        Assert.Equal(IcSyncJobTickResult.Refused, result);
        Assert.Equal(0, runner.ExecuteCalls);
        var complete = BodyOf(ic.Last("POST", "/complete"));
        Assert.False(complete.GetProperty("success").GetBoolean());
        Assert.Contains("partition in the job payload could not be read", complete.GetProperty("errorMessage").GetString());
    }

    private sealed class ThrowingResolver : IIcSyncProjectJobRunner
    {
        public Task<SyncProject?> ResolveAsync(Guid identityCenterProjectId) =>
            throw new InvalidOperationException("the connection pool is exhausted");
        public Task<IcSyncRunOutcome> ExecuteAsync(SyncProject project, string triggeredBy,
            Func<IcSyncRunProgress, CancellationToken, Task> onProgress, CancellationToken ct, SyncRunPartition? partition = null) =>
            throw new InvalidOperationException("must never be reached");
        public Task<IcPartitionPlan> PlanPartitionsAsync(SyncProject project, CancellationToken ct) =>
            throw new InvalidOperationException("must never be reached");
    }

    [Fact]
    public async Task A_completion_IdentityCenter_did_not_accept_is_never_recorded_as_reported()
    {
        var (_, _, _, status) = await RunAsync(
            ic =>
            {
                ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) });
                ic.CompleteStatus = 500;
            },
            r => r.Bound = Project());

        var outcome = Assert.Single(status.Snapshot()).LastJobOutcome;
        Assert.Contains("IdentityCenter did NOT accept the completion", outcome);
    }

    [Fact]
    public async Task Remote_text_never_reaches_a_log_or_the_status_with_its_control_characters_intact()
    {
        var forged = """{"success":false,"message":"No jobs available\r\n2026-09-17 FATAL forged line"}""";
        var (_, _, _, status) = await RunAsync(ic => ic.Claims.Enqueue(() =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(forged) }));

        var outcome = Assert.Single(status.Snapshot()).LastClaimOutcome!;
        Assert.DoesNotContain("\n", outcome);
        Assert.DoesNotContain("\r", outcome);
        Assert.Contains("No jobs available", outcome);
    }

    [Fact]
    public void Remote_text_is_capped_so_one_field_cannot_write_a_megabyte_of_log()
    {
        Assert.Equal(200, IcSyncJobPump.Clean(new string('x', 5000)).Length);
        Assert.Equal(50, IcSyncJobPump.Clean(new string('x', 5000), 50).Length);
        Assert.Equal(string.Empty, IcSyncJobPump.Clean(null));
    }

    [Fact]
    public async Task A_cancelled_run_that_still_rolls_up_to_PartialSuccess_is_not_reported_as_a_success()
    {
        var (_, ic, runner, _) = await RunAsync(
            ic =>
            {
                ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) });
                ic.JobReads.Enqueue(JobReadJson(cancellationRequested: true));
            },
            r =>
            {
                r.Bound = Project();
                r.ReportProgress = true;
                // A roll-up that lands on PartialSuccess before the cancel propagates.
                r.Outcome = new IcSyncRunOutcome(true, RunId, "PartialSuccess", 9, 8, 1, "partial", "{}");
                r.IgnoreCancellation = true;
            });

        Assert.True(runner.RunSawCancellation);
        var complete = BodyOf(ic.Last("POST", "/complete"));
        Assert.False(complete.GetProperty("success").GetBoolean());
        Assert.Contains("Cancelled at IdentityCenter's request.", complete.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task A_run_that_cannot_even_be_admitted_still_completes_the_job_rather_than_leaving_it_claimed()
    {
        var ic = new FakeIc();
        ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) });
        var runner = new ThrowingRunner { Bound = Project() };
        var pump = new IcSyncJobPump(runner, new IcAgentStatusService(), NullLogger<IcSyncJobPump>.Instance);

        var result = await pump.RunOnceAsync(new IcSyncJobChannel(new HttpClient(ic)), BaseUrl, AgentId, CancellationToken.None);

        Assert.Equal(IcSyncJobTickResult.Executed, result);
        var complete = BodyOf(ic.Last("POST", "/complete"));
        Assert.False(complete.GetProperty("success").GetBoolean());
        Assert.Contains("could not execute IdentityCenter job", complete.GetProperty("errorMessage").GetString());
        Assert.Contains("the database is unreachable", complete.GetProperty("errorMessage").GetString());
    }

    private sealed class ThrowingRunner : IIcSyncProjectJobRunner
    {
        public SyncProject? Bound { get; set; }
        public Task<SyncProject?> ResolveAsync(Guid identityCenterProjectId) => Task.FromResult(Bound);
        public Task<IcSyncRunOutcome> ExecuteAsync(SyncProject project, string triggeredBy,
            Func<IcSyncRunProgress, CancellationToken, Task> onProgress, CancellationToken ct, SyncRunPartition? partition = null) =>
            throw new InvalidOperationException("the database is unreachable");
        public Task<IcPartitionPlan> PlanPartitionsAsync(SyncProject project, CancellationToken ct) =>
            throw new InvalidOperationException("the database is unreachable");
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }

    [Fact]
    public async Task A_refused_key_is_reported_as_a_permanent_condition_not_as_an_empty_queue()
    {
        var (result, _, runner, status) = await RunAsync(
            ic => ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") }));

        Assert.Equal(IcSyncJobTickResult.ChannelRejected, result);
        Assert.Equal(0, runner.ResolveCalls);
        Assert.Contains("Rejected", Assert.Single(status.Snapshot()).LastClaimOutcome);
    }

    // ── Cancellation ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_cancellation_flag_flipped_at_IdentityCenter_cancels_the_local_run_and_the_completion_is_not_a_success()
    {
        var (result, ic, runner, _) = await RunAsync(
            ic =>
            {
                ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) });
                ic.JobReads.Enqueue(JobReadJson(cancellationRequested: true));
            },
            r =>
            {
                r.Bound = Project();
                r.ReportProgress = true;
            });

        Assert.Equal(IcSyncJobTickResult.Executed, result);
        Assert.True(runner.RunSawCancellation, "the local run must observe the cancellation IdentityCenter requested");

        var complete = BodyOf(ic.Last("POST", "/complete"));
        Assert.False(complete.GetProperty("success").GetBoolean());
        Assert.Contains("Cancelled at IdentityCenter's request.", complete.GetProperty("errorMessage").GetString());

        // The progress tick is the re-read's only trigger — it is what saw the flag.
        Assert.NotNull(ic.Last("POST", "/progress"));
    }

    [Fact]
    public async Task An_unflipped_cancellation_flag_leaves_the_run_alone()
    {
        var (_, ic, runner, _) = await RunAsync(
            ic =>
            {
                ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) });
                ic.JobReads.Enqueue(JobReadJson(cancellationRequested: false));
            },
            r =>
            {
                r.Bound = Project();
                r.ReportProgress = true;
            });

        Assert.False(runner.RunSawCancellation);
        Assert.True(BodyOf(ic.Last("POST", "/complete")).GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task An_unreadable_job_re_read_is_never_mistaken_for_a_cancellation()
    {
        var (_, ic, runner, _) = await RunAsync(
            ic => ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) }),
            r =>
            {
                r.Bound = Project();
                r.ReportProgress = true;   // no JobReads scripted → the GET 404s
            });

        Assert.False(runner.RunSawCancellation);
        Assert.True(BodyOf(ic.Last("POST", "/complete")).GetProperty("success").GetBoolean());
    }

    // ── "No work, ever" ─────────────────────────────────────────────────────

    [Fact]
    public async Task An_agent_row_that_does_not_list_SyncProject_is_recorded_as_no_work_ever()
    {
        var (_, _, _, status) = await RunAsync(ic => ic.AgentJson = """{"id":"11111111-1111-1111-1111-111111111111","supportedJobTypes":"PolicyEvaluation"}""");

        var snapshot = Assert.Single(status.Snapshot());
        Assert.Equal("PolicyEvaluation", snapshot.SupportedJobTypes);
        Assert.False(snapshot.ExecutesSyncProjects);
    }

    [Fact]
    public async Task An_agent_row_that_lists_SyncProject_or_the_wildcard_is_recorded_as_eligible()
    {
        var (_, _, _, status) = await RunAsync(ic => ic.AgentJson = """{"supportedJobTypes":"PolicyEvaluation, SyncProject"}""");
        Assert.True(Assert.Single(status.Snapshot()).ExecutesSyncProjects);

        var (_, _, _, wildcard) = await RunAsync(ic => ic.AgentJson = """{"supportedJobTypes":"*"}""");
        Assert.True(Assert.Single(wildcard.Snapshot()).ExecutesSyncProjects);
    }

    [Fact]
    public async Task An_unreadable_agent_row_leaves_the_capability_unknown_rather_than_false()
    {
        var (_, _, _, status) = await RunAsync(_ => { });   // GET /api/agents/{id} → 404
        Assert.Null(Assert.Single(status.Snapshot()).ExecutesSyncProjects);
    }

    [Theory]
    [InlineData("SyncProject", true)]
    [InlineData("syncproject", true)]
    [InlineData("*", true)]
    [InlineData("PolicyEvaluation,SyncProject", true)]
    [InlineData("PolicyEvaluation", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Supported_job_type_reading_mirrors_IdentityCenters_JobTypeSupport(string? declared, bool expected) =>
        Assert.Equal(expected, IcSyncJobChannel.SupportsSyncProject(declared));

    // ── Wire contract pins ──────────────────────────────────────────────────

    /// <summary>
    /// The two repositories share no assembly, so these field names ARE the contract. They mirror
    /// IdentityCenter's ClaimJobRequest / JobProgressUpdate / CompleteJobRequest / JobQueueEntry
    /// (Software/DataAccessLibrary/Models/RemoteAgentModels.cs); ASP.NET Core serializes those
    /// camelCase and binds them case-insensitively.
    /// </summary>
    [Fact]
    public void Request_bodies_carry_exactly_IdentityCenters_field_names()
    {
        var claim = JsonDocument.Parse(IcSyncJobChannel.BuildClaimRequestJson(AgentId)).RootElement;
        Assert.Equal(new[] { "agentId", "supportedJobTypes", "maxJobs" }, claim.EnumerateObject().Select(p => p.Name).ToArray());

        var progress = JsonDocument.Parse(IcSyncJobChannel.BuildProgressJson(JobId, AgentId, 0, "m", 1, 2, 3)).RootElement;
        Assert.Equal(new[] { "jobId", "agentId", "progressPercent", "progressMessage", "itemsProcessed", "itemsSucceeded", "itemsFailed" },
            progress.EnumerateObject().Select(p => p.Name).ToArray());

        var complete = JsonDocument.Parse(IcSyncJobChannel.BuildCompleteJson(JobId, AgentId, true, 1, 2, 3, "e", "{}")).RootElement;
        Assert.Equal(new[] { "jobId", "agentId", "success", "itemsProcessed", "itemsSucceeded", "itemsFailed", "errorMessage", "resultJson" },
            complete.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void The_claimed_job_is_read_from_IdentityCenters_JobQueueEntry_field_names()
    {
        var outcome = IcSyncJobChannel.ParseClaimResponse(ClaimedJson(cancellationRequested: true));

        Assert.Equal(IcClaimResult.Claimed, outcome.Result);
        var job = outcome.Job!;
        Assert.Equal(JobId, job.Id);
        Assert.Equal("SyncProject", job.JobType);
        Assert.Equal(IcProjectId, job.RelatedEntityId);
        Assert.Equal("SyncProject", job.RelatedEntityType);
        Assert.Equal(AgentId, job.TargetServerId);
        Assert.True(job.CancellationRequested);
        Assert.Contains("TriggeredBy", job.PayloadJson);
    }

    [Theory]
    [InlineData("""{"success":false,"message":"No jobs available"}""")]
    [InlineData("""{"success":true}""")]
    public void A_claim_answer_without_a_job_is_no_work_not_a_failure(string json) =>
        Assert.Equal(IcClaimResult.NoWork, IcSyncJobChannel.ParseClaimResponse(json).Result);

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"success":true,"job":{"jobType":"SyncProject"}}""")]
    public void An_unusable_claim_answer_is_reported_unavailable_rather_than_silently_empty(string json) =>
        Assert.Equal(IcClaimResult.Unavailable, IcSyncJobChannel.ParseClaimResponse(json).Result);

    // ── Progress percent honesty ────────────────────────────────────────────

    [Fact]
    public async Task Progress_reports_the_counts_and_leaves_the_percentage_at_zero_because_it_is_unknown()
    {
        var (_, ic, _, _) = await RunAsync(
            ic =>
            {
                ic.Claims.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaimedJson()) });
                ic.JobReads.Enqueue(JobReadJson(cancellationRequested: false));
            },
            r => { r.Bound = Project(); r.ReportProgress = true; });

        var progress = BodyOf(ic.Last("POST", "/progress"));
        Assert.Equal(0, progress.GetProperty("progressPercent").GetInt32());
        Assert.Equal(7, progress.GetProperty("itemsProcessed").GetInt32());
        Assert.Equal(5, progress.GetProperty("itemsSucceeded").GetInt32());
        Assert.Equal(2, progress.GetProperty("itemsFailed").GetInt32());
    }

    // ── The extraction ──────────────────────────────────────────────────────

    [Fact]
    public void Endpoint_discovery_and_key_selection_exist_once_and_both_channels_read_them()
    {
        var directory = Read("src/Conduit.Web/Services/IcEndpointDirectory.cs");
        var poller = Read("src/Conduit.Web/Services/IcAgentCommandPollerService.cs");
        var executor = Read("src/Conduit.Web/Services/IcSyncJobExecutorService.cs");

        // The credential blob is parsed in exactly one place.
        Assert.Contains("TryGetProperty(\"AgentApiKey\"", directory);
        Assert.DoesNotContain("TryGetProperty(\"AgentApiKey\"", poller);
        Assert.DoesNotContain("TryGetProperty(\"AgentApiKey\"", executor);
        Assert.DoesNotContain("identitycenter", poller);

        Assert.Contains("_endpoints.DiscoverAsync()", poller);
        Assert.Contains("_endpoints.DiscoverAsync()", executor);

        // The enrolled identity still has exactly one owner: the heartbeat writes it, both read it.
        Assert.Contains("s.AgentId = agentId", poller);
        Assert.Contains("_status.TryGetAgentId(endpoint.BaseUrl)", executor);
        Assert.DoesNotContain("/api/agent/heartbeat", executor);
    }

    [Fact]
    public void The_directory_prefers_the_per_agent_key_whatever_order_the_connections_arrive_in()
    {
        var shared = new IcEndpoint("https://ic.example.com", "shared", null);
        var perAgent = new IcEndpoint("https://ic.example.com", "shared", "agent-key");

        var sharedFirst = new List<IcEndpoint>();
        IcEndpointDirectory.Merge(sharedFirst, shared);
        IcEndpointDirectory.Merge(sharedFirst, perAgent);

        var agentFirst = new List<IcEndpoint>();
        IcEndpointDirectory.Merge(agentFirst, perAgent);
        IcEndpointDirectory.Merge(agentFirst, shared);

        Assert.Equal("agent-key", Assert.Single(sharedFirst).AgentApiKey);
        Assert.Equal("agent-key", Assert.Single(agentFirst).AgentApiKey);
        Assert.Equal("AgentApiKey", Assert.Single(agentFirst).KeySource);
        Assert.Equal("agent-key", Assert.Single(agentFirst).ChannelKey);
    }

    [Fact]
    public void The_directory_normalizes_the_base_url_and_rejects_an_unusable_credential_blob()
    {
        var parsed = IcEndpointDirectory.Parse("""{"BaseUrl":"https://ic.example.com/","ApiKey":"k","AgentApiKey":"  "}""");
        Assert.Equal("https://ic.example.com", parsed!.BaseUrl);
        Assert.Null(parsed.AgentApiKey);
        Assert.Equal("k", parsed.ChannelKey);
        Assert.Equal("ApiKey", parsed.KeySource);

        Assert.Null(IcEndpointDirectory.Parse("""{"ApiKey":"k"}"""));
        Assert.Null(IcEndpointDirectory.Parse("""{"BaseUrl":"https://x"}"""));
        Assert.Null(IcEndpointDirectory.Parse("not json"));
        Assert.Null(IcEndpointDirectory.Parse("[]"));
    }

    [Fact]
    public void An_endpoint_never_prints_its_keys()
    {
        var text = new IcEndpoint("https://ic.example.com", "shared-secret", "agent-secret").ToString();
        Assert.Equal("https://ic.example.com (AgentApiKey)", text);
        Assert.DoesNotContain("secret", text);
    }

    private static string Read(string relative, [CallerFilePath] string file = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", relative));
}
