using System.Net;
using System.Text.Json;
using Conduit.Core.SyncModels;
using Conduit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// JOB-PLACEMENT-03 (Jacob 2026-11-06: "Conduit has or should have the ability to send emails"). IdentityCenter's mail
/// drain job, placed on this server, is claimed here and drained: claim a batch → send each → report each → complete
/// with the real counts. Offline: a stubbed handler plays IdentityCenter and a fake sender plays the relay.
/// </summary>
public class IcMailJobTests
{
    private static readonly Guid AgentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid JobId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string BaseUrl = "https://ic.example.com";

    private sealed record Recorded(string Method, string Path, string Body);

    private sealed class FakeIc : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = new();
        public string ClaimJson { get; set; } = """{"success":true,"job":null,"message":"No jobs available"}""";
        public Queue<string> MailClaims { get; } = new();
        public int SettleStatus { get; set; } = 200;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(new Recorded(request.Method.Method, path, body));
            if (path == "/api/jobs/claim") return Ok(ClaimJson);
            if (path.EndsWith("/mail/claim", StringComparison.Ordinal))
                return Ok(MailClaims.Count > 0 ? MailClaims.Dequeue() : Batch(Array.Empty<IcMailItem>()));
            if (path.EndsWith("/mail/settle", StringComparison.Ordinal)) return new HttpResponseMessage((HttpStatusCode)SettleStatus) { Content = new StringContent("{}") };
            if (path.EndsWith("/progress", StringComparison.Ordinal) || path.EndsWith("/complete", StringComparison.Ordinal)) return Ok("{}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

        public IEnumerable<Recorded> All(string suffix) => Requests.Where(r => r.Path.EndsWith(suffix, StringComparison.Ordinal));
    }

    private sealed class FakeSender : IMailRelaySender
    {
        public bool CanSignIn { get; set; }
        public Func<IcMailItem, string?> Answer { get; set; } = _ => null;
        public List<(IcMailRelay Relay, IcMailItem Item)> Sent { get; } = new();

        public Task<string?> SendAsync(IcMailRelay relay, IcMailItem item, CancellationToken ct)
        {
            Sent.Add((relay, item));
            return Task.FromResult(Answer(item));
        }
    }

    private sealed class NoSyncRunner : IIcSyncProjectJobRunner
    {
        public int Calls { get; private set; }
        public Task<SyncProject?> ResolveAsync(Guid identityCenterProjectId) { Calls++; return Task.FromResult<SyncProject?>(null); }
        public Task<IcSyncRunOutcome> ExecuteAsync(SyncProject project, string triggeredBy,
            Func<IcSyncRunProgress, CancellationToken, Task> onProgress, CancellationToken ct, SyncRunPartition? partition = null) { Calls++; throw new InvalidOperationException("no sync run here"); }
        public Task<IcPartitionPlan> PlanPartitionsAsync(SyncProject project, CancellationToken ct) { Calls++; throw new InvalidOperationException("no sync run here"); }
    }

    private const string Relay = """{"host":"192.168.1.60","port":25,"useSsl":false,"fromAddress":"noreply@identitycenter.local","fromName":"Identity Center","replyToAddress":null,"signInRequired":false}""";

    private static string Batch(IReadOnlyList<IcMailItem> items, string? unavailable = null, int expired = 0, int refused = 0) =>
        JsonSerializer.Serialize(new
        {
            relay = unavailable is null ? JsonDocument.Parse(Relay).RootElement : (JsonElement?)null,
            items = items.Select(i => new { id = i.Id, to = i.To, toName = i.ToName, subject = i.Subject, body = i.Body, isHtml = i.IsHtml }),
            expired, refused, dailyCapReached = false, unavailable
        });

    private static IcMailItem Item(string to) => new(Guid.NewGuid(), to, "Kai", "Welcome", "<p>hi</p>", true);

    private const string MailJobClaim = """
        { "success": true, "message": "Job claimed successfully",
          "job": { "id": "22222222-2222-2222-2222-222222222222", "jobType": "EmailQueueProcessing", "jobName": "EmailQueueProcessingJob",
                   "targetServerId": "11111111-1111-1111-1111-111111111111", "cancellationRequested": false } }
        """;

    private static async Task<(IcSyncJobTickResult Result, FakeIc Ic, FakeSender Sender, NoSyncRunner Runner)> RunAsync(
        Action<FakeIc> script, Action<FakeSender>? arrange = null, bool withMail = true)
    {
        var ic = new FakeIc();
        script(ic);
        var sender = new FakeSender();
        arrange?.Invoke(sender);
        var runner = new NoSyncRunner();
        var mail = withMail ? new IcMailJobRunner(sender, NullLogger<IcMailJobRunner>.Instance) : null;
        var pump = new IcSyncJobPump(runner, new IcAgentStatusService(), NullLogger<IcSyncJobPump>.Instance, mail);
        var result = await pump.RunOnceAsync(new IcSyncJobChannel(new HttpClient(ic)), BaseUrl, AgentId, CancellationToken.None);
        return (result, ic, sender, runner);
    }

    private static JsonElement Body(Recorded r) => JsonDocument.Parse(r.Body).RootElement.Clone();

    [Fact]
    public async Task With_a_mail_sender_the_claim_also_asks_for_the_mail_drain()
    {
        var (_, ic, _, _) = await RunAsync(_ => { });
        var claim = Body(ic.All("/api/jobs/claim").Single());
        Assert.Equal(new[] { "SyncProject", "EmailQueueProcessing" }, claim.GetProperty("supportedJobTypes").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task A_placed_mail_job_sends_each_message_reports_each_and_completes_with_the_real_counts()
    {
        var ok = Item("kai.mendoza@granite-lab.local");
        var bad = Item("it-onboarding@granite-lab.local");
        var (result, ic, sender, runner) = await RunAsync(
            ic => { ic.ClaimJson = MailJobClaim; ic.MailClaims.Enqueue(Batch(new[] { ok, bad }, expired: 1, refused: 2)); },
            s => s.Answer = i => i.Id == bad.Id ? "451 try later" : null);

        Assert.Equal(IcSyncJobTickResult.Executed, result);
        Assert.Equal(0, runner.Calls);
        Assert.Equal(new[] { ok.Id, bad.Id }, sender.Sent.Select(s => s.Item.Id).ToArray());
        Assert.Equal("192.168.1.60", sender.Sent[0].Relay.Host);

        var claim = Body(ic.All("/mail/claim").First());
        Assert.False(claim.GetProperty("canSignIn").GetBoolean());

        var settle = Body(ic.All("/mail/settle").Single()).GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(2, settle.Count);
        Assert.True(settle[0].GetProperty("success").GetBoolean());
        Assert.False(settle[1].GetProperty("success").GetBoolean());
        Assert.Equal("451 try later", settle[1].GetProperty("error").GetString());

        var complete = Body(ic.All("/complete").Single());
        Assert.True(complete.GetProperty("success").GetBoolean());
        Assert.Equal(2, complete.GetProperty("itemsProcessed").GetInt32());
        Assert.Equal(1, complete.GetProperty("itemsSucceeded").GetInt32());
        Assert.Equal(1, complete.GetProperty("itemsFailed").GetInt32());
        Assert.Contains("expired 1", complete.GetProperty("resultJson").GetString());
        Assert.Contains("recipient policy 2", complete.GetProperty("resultJson").GetString());
    }

    [Fact]
    public async Task A_relay_this_server_cannot_sign_into_fails_the_job_with_the_reason_and_sends_nothing()
    {
        var (_, ic, sender, _) = await RunAsync(ic =>
        {
            ic.ClaimJson = MailJobClaim;
            ic.MailClaims.Enqueue(Batch(Array.Empty<IcMailItem>(), unavailable: "The relay needs a sign-in and this server has no relay credentials of its own."));
        });

        Assert.Empty(sender.Sent);
        Assert.Empty(ic.All("/mail/settle"));
        var complete = Body(ic.All("/complete").Single());
        Assert.False(complete.GetProperty("success").GetBoolean());
        Assert.Contains("sign-in", complete.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task An_outcome_IdentityCenter_did_not_accept_is_never_reported_as_a_success()
    {
        var (_, ic, _, _) = await RunAsync(ic =>
        {
            ic.ClaimJson = MailJobClaim;
            ic.SettleStatus = 500;
            ic.MailClaims.Enqueue(Batch(new[] { Item("kai.mendoza@granite-lab.local") }));
        });

        var complete = Body(ic.All("/complete").Single());
        Assert.False(complete.GetProperty("success").GetBoolean());
        Assert.Contains("NOT accepted", complete.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task A_full_batch_takes_the_next_one_and_a_short_batch_ends_the_job()
    {
        var full = Enumerable.Range(0, IcMailJobRunner.BatchSize).Select(i => Item($"p{i}@granite-lab.local")).ToArray();
        var (_, ic, sender, _) = await RunAsync(ic =>
        {
            ic.ClaimJson = MailJobClaim;
            ic.MailClaims.Enqueue(Batch(full));
            ic.MailClaims.Enqueue(Batch(new[] { Item("last@granite-lab.local") }));
        });

        Assert.Equal(IcMailJobRunner.BatchSize + 1, sender.Sent.Count);
        Assert.Equal(2, ic.All("/mail/claim").Count());
        Assert.Equal(IcMailJobRunner.BatchSize + 1, Body(ic.All("/complete").Single()).GetProperty("itemsSucceeded").GetInt32());
    }

    [Fact]
    public async Task Without_a_mail_sender_a_mail_job_is_refused_by_name_and_nothing_is_claimed_from_the_queue()
    {
        var (result, ic, sender, _) = await RunAsync(ic => ic.ClaimJson = MailJobClaim, withMail: false);

        Assert.Equal(IcSyncJobTickResult.Refused, result);
        Assert.Empty(ic.All("/mail/claim"));
        Assert.Empty(sender.Sent);
        Assert.Contains("EmailQueueProcessing", Body(ic.All("/complete").Single()).GetProperty("errorMessage").GetString());
    }
}
