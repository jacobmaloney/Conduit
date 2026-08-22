using System.Net;
using System.Text.Json;
using Conduit.Web.Services;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// DB-free tests of the enrollment core (<see cref="EnrollmentClient"/>): the fixed
/// wire contract (request/response field names), the single-use-code retry policy
/// (403 NEVER retried; one retry on network/5xx only), origin-match idempotency
/// normalization, the LOAD-BEARING credential-blob field names the poller and
/// IdentityCenterCredentialReader parse, and tenant-name sanitization.
/// </summary>
public class EnrollmentClientTests
{
    private const string SuccessJson = """
        {
          "baseUrl": "https://ic.example.com:8443",
          "tenantSlug": "acme-corp",
          "agentId": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
          "agentApiKey": "agent-key-123",
          "syncApiKey": "sync-key-456"
        }
        """;

    // ── Fake handler ─────────────────────────────────────────────────────────

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;
        public List<string> RequestBodies { get; } = new();
        public int Calls { get; private set; }

        public FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
            => _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            if (_responses.Count == 0)
                throw new InvalidOperationException("FakeHandler ran out of scripted responses.");
            return _responses.Dequeue()(request);
        }

        public static Func<HttpRequestMessage, HttpResponseMessage> Status(HttpStatusCode code, string body = "{}") =>
            _ => new HttpResponseMessage(code) { Content = new StringContent(body) };

        public static Func<HttpRequestMessage, HttpResponseMessage> Throw() =>
            _ => throw new HttpRequestException("connection refused");
    }

    private static EnrollmentClient Build(FakeHandler handler) =>
        new(new HttpClient(handler), retryDelay: TimeSpan.Zero);

    // ── Response parse ───────────────────────────────────────────────────────

    [Fact]
    public void ParseResponse_happy_path_maps_all_five_fields()
    {
        var r = EnrollmentClient.ParseResponse(SuccessJson);

        Assert.NotNull(r);
        Assert.Equal("https://ic.example.com:8443", r!.BaseUrl);
        Assert.Equal("acme-corp", r.TenantSlug);
        Assert.Equal(Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"), r.AgentId);
        Assert.Equal("agent-key-123", r.AgentApiKey);
        Assert.Equal("sync-key-456", r.SyncApiKey);
    }

    [Theory]
    [InlineData("""{"baseUrl":"https://x","tenantSlug":"s","agentId":"not-a-guid","agentApiKey":"a","syncApiKey":"b"}""")]
    [InlineData("""{"tenantSlug":"s","agentId":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","agentApiKey":"a","syncApiKey":"b"}""")]
    [InlineData("""{"baseUrl":"https://x","tenantSlug":"s","agentId":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","agentApiKey":"","syncApiKey":"b"}""")]
    [InlineData("""{"baseUrl":"not a url","tenantSlug":"s","agentId":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","agentApiKey":"a","syncApiKey":"b"}""")]
    [InlineData("""{"baseUrl":"ftp://x/file","tenantSlug":"s","agentId":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","agentApiKey":"a","syncApiKey":"b"}""")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    public void ParseResponse_rejects_missing_malformed_or_invalid_baseUrl(string json)
    {
        Assert.Null(EnrollmentClient.ParseResponse(json));
    }

    // ── Request build (fixed contract) ───────────────────────────────────────

    [Fact]
    public void BuildRequestJson_uses_exact_contract_field_names()
    {
        var id = Guid.NewGuid();
        var json = EnrollmentClient.BuildRequestJson("CODE-1", id, "box1", "1.2.3");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("CODE-1", doc.RootElement.GetProperty("enrollCode").GetString());
        Assert.Equal(id, doc.RootElement.GetProperty("instanceId").GetGuid());
        Assert.Equal("box1", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("1.2.3", doc.RootElement.GetProperty("version").GetString());
        Assert.Equal(4, doc.RootElement.EnumerateObject().Count());
    }

    // ── Retry policy ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Forbidden_403_is_never_retried_and_reports_actionable_error()
    {
        var handler = new FakeHandler(
            FakeHandler.Status(HttpStatusCode.Forbidden, """{"error":"invalid_or_expired_code"}"""));

        var outcome = await Build(handler).EnrollAsync("https://ic.example.com/", "CODE", Guid.NewGuid(), "n", "v");

        Assert.Null(outcome.Response);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("invalid or expired", outcome.Error);
        Assert.Equal("invalid_or_expired_code", outcome.ErrorCategory);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task BadRequest_and_RateLimit_are_not_retried(HttpStatusCode status)
    {
        var handler = new FakeHandler(FakeHandler.Status(status));

        var outcome = await Build(handler).EnrollAsync("https://ic.example.com", "CODE", Guid.NewGuid(), "n", "v");

        Assert.Null(outcome.Response);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Transient_5xx_is_retried_once_then_succeeds()
    {
        var handler = new FakeHandler(
            FakeHandler.Status(HttpStatusCode.InternalServerError),
            FakeHandler.Status(HttpStatusCode.OK, SuccessJson));

        var outcome = await Build(handler).EnrollAsync("https://ic.example.com", "CODE", Guid.NewGuid(), "n", "v");

        Assert.NotNull(outcome.Response);
        Assert.Equal(2, handler.Calls);
        Assert.Equal("acme-corp", outcome.Response!.TenantSlug);
    }

    [Fact]
    public async Task Network_failure_is_retried_once_then_gives_up()
    {
        var handler = new FakeHandler(FakeHandler.Throw(), FakeHandler.Throw());

        var outcome = await Build(handler).EnrollAsync("https://ic.example.com", "CODE", Guid.NewGuid(), "n", "v");

        Assert.Null(outcome.Response);
        Assert.Equal(2, handler.Calls);
        Assert.Contains("could not reach", outcome.Error);
        Assert.Equal("network_unreachable", outcome.ErrorCategory);
    }

    [Fact]
    public async Task Enroll_url_is_trimmed_and_endpoint_appended()
    {
        HttpRequestMessage? seen = null;
        var capture = new FakeHandler(req => { seen = req; return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") }; });

        await Build(capture).EnrollAsync("https://ic.example.com///", "CODE", Guid.NewGuid(), "n", "v");

        Assert.Equal("https://ic.example.com/api/agent/enroll", seen!.RequestUri!.ToString());
    }

    // ── Origin-match idempotency ─────────────────────────────────────────────

    [Theory]
    [InlineData("https://Host:443", "https://host")]
    [InlineData("https://host/", "https://HOST")]
    [InlineData("https://host:443/some/path", "https://host")]
    [InlineData("http://Host:80/", "http://host")]
    [InlineData("https://host:8443/", "https://HOST:8443/trailing/")]
    public void NormalizeOrigin_equates_default_port_case_and_trailing_slash_variants(string a, string b)
    {
        Assert.Equal(EnrollmentClient.NormalizeOrigin(a), EnrollmentClient.NormalizeOrigin(b));
        Assert.NotNull(EnrollmentClient.NormalizeOrigin(a));
    }

    [Theory]
    [InlineData("https://host", "https://host:8443")]
    [InlineData("https://host", "http://host")]
    [InlineData("https://host-a", "https://host-b")]
    public void NormalizeOrigin_distinguishes_different_origins(string a, string b)
    {
        Assert.NotEqual(EnrollmentClient.NormalizeOrigin(a), EnrollmentClient.NormalizeOrigin(b));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://host/file")]
    public void NormalizeOrigin_rejects_non_http_or_invalid(string? url)
    {
        Assert.Null(EnrollmentClient.NormalizeOrigin(url));
    }

    [Fact]
    public void CredentialMatchesOrigin_matches_stored_blob_BaseUrl_variants()
    {
        var origin = EnrollmentClient.NormalizeOrigin("https://ic.example.com")!;

        Assert.True(EnrollmentClient.CredentialMatchesOrigin(
            """{"BaseUrl":"https://IC.example.com:443/","ApiKey":"k"}""", origin));
        Assert.False(EnrollmentClient.CredentialMatchesOrigin(
            """{"BaseUrl":"https://other.example.com","ApiKey":"k"}""", origin));
        Assert.False(EnrollmentClient.CredentialMatchesOrigin("not json", origin));
        Assert.False(EnrollmentClient.CredentialMatchesOrigin("{}", origin));
    }

    [Fact]
    public void CredentialMatchesOrigin_also_matches_the_recorded_EnrollUrl()
    {
        // IC's baseUrl may legitimately live on a different host than the enroll
        // endpoint; the recorded EnrollUrl must keep restart idempotency intact so
        // the consumed single-use code is never re-sent.
        var enrollOrigin = EnrollmentClient.NormalizeOrigin("https://portal.example.com")!;
        var blob = """{"BaseUrl":"https://api.example.com","ApiKey":"k","AgentApiKey":"a","EnrollUrl":"https://PORTAL.example.com:443/"}""";

        Assert.True(EnrollmentClient.CredentialMatchesOrigin(blob, enrollOrigin));
        Assert.True(EnrollmentClient.CredentialMatchesOrigin(blob, EnrollmentClient.NormalizeOrigin("https://api.example.com")!));
        Assert.False(EnrollmentClient.CredentialMatchesOrigin(blob, EnrollmentClient.NormalizeOrigin("https://elsewhere.example.com")!));
    }

    [Fact]
    public void CredentialIsAgentEnrolledFor_requires_an_agent_key_not_just_the_right_host()
    {
        // The live shape on 2026-08-21: an IdentityCenter connection configured by hand in the UI,
        // holding a sync ApiKey and no AgentApiKey. Enrollment skipped as "already enrolled" on
        // every boot, so the agent channel had no agent_id claim to present and answered 403
        // forever -- while the status card read "Enrolled against http://...:8080".
        var origin = EnrollmentClient.NormalizeOrigin("http://192.168.1.60:8080")!;
        var handConfigured = """{"BaseUrl":"http://192.168.1.60:8080","ApiKey":"sync-key-typed-by-a-human"}""";

        // Same origin -- so the origin predicate alone cannot tell these apart...
        Assert.True(EnrollmentClient.CredentialMatchesOrigin(handConfigured, origin));
        // ...but it is not an agent enrollment, and treating it as one is what made the code a no-op.
        Assert.False(EnrollmentClient.CredentialIsAgentEnrolledFor(handConfigured, origin));
    }

    [Fact]
    public void CredentialIsAgentEnrolledFor_accepts_a_real_enrollment_credential()
    {
        var origin = EnrollmentClient.NormalizeOrigin("http://192.168.1.60:8080")!;
        var enrolled = """{"BaseUrl":"http://192.168.1.60:8080","ApiKey":"sync","AgentApiKey":"agent-scoped"}""";

        Assert.True(EnrollmentClient.CredentialIsAgentEnrolledFor(enrolled, origin));
        // Still origin-scoped: an agent credential for a DIFFERENT IC must not suppress enrollment
        // against this one, or pointing an agent at a second server would silently do nothing.
        Assert.False(EnrollmentClient.CredentialIsAgentEnrolledFor(
            enrolled, EnrollmentClient.NormalizeOrigin("http://192.168.1.99:8080")!));
    }

    [Theory]
    [InlineData("""{"BaseUrl":"http://192.168.1.60:8080","AgentApiKey":""}""")]
    [InlineData("""{"BaseUrl":"http://192.168.1.60:8080","AgentApiKey":"   "}""")]
    [InlineData("""{"BaseUrl":"http://192.168.1.60:8080","AgentApiKey":null}""")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void CredentialIsAgentEnrolledFor_treats_an_absent_or_blank_agent_key_as_not_enrolled(string blob)
    {
        // A present-but-empty key is the dangerous one: it looks like the field exists, and a
        // truthiness check on presence alone would call this enrolled.
        Assert.False(EnrollmentClient.CredentialIsAgentEnrolledFor(
            blob, EnrollmentClient.NormalizeOrigin("http://192.168.1.60:8080")!));
    }

    // ── Credential blob (LOAD-BEARING field names) ───────────────────────────

    [Fact]
    public void ComposeCredentialBlob_uses_exactly_the_load_bearing_field_names()
    {
        var response = EnrollmentClient.ParseResponse(SuccessJson)!;
        var blob = EnrollmentClient.ComposeCredentialBlob(response, "https://portal.example.com/");

        using var doc = JsonDocument.Parse(blob);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "BaseUrl", "ApiKey", "AgentApiKey", "EnrollUrl" }, names);

        // ApiKey is the SHARED sync key (drives /api/objects/bulk); AgentApiKey is
        // the per-agent key (drives claim + heartbeat). Swapping them breaks both channels.
        // EnrollUrl is additive and ignored by both existing readers.
        Assert.Equal("https://ic.example.com:8443", doc.RootElement.GetProperty("BaseUrl").GetString());
        Assert.Equal("sync-key-456", doc.RootElement.GetProperty("ApiKey").GetString());
        Assert.Equal("agent-key-123", doc.RootElement.GetProperty("AgentApiKey").GetString());
        Assert.Equal("https://portal.example.com/", doc.RootElement.GetProperty("EnrollUrl").GetString());
    }

    // ── Tenant name sanitization ─────────────────────────────────────────────

    [Theory]
    [InlineData("acme-corp", "IdentityCenter-acme-corp")]
    [InlineData("Acme Corp (prod)", "IdentityCenter-Acme-Corp-prod")]
    [InlineData("", "IdentityCenter")]
    [InlineData("///", "IdentityCenter")]
    public void BuildTenantName_stays_Source_regex_safe(string slug, string expected)
    {
        var name = EnrollmentClient.BuildTenantName(slug);
        Assert.Equal(expected, name);
        Assert.Matches(@"^[A-Za-z0-9_.\-]{1,100}$", name);
    }
}
