using System.Text.Json;
using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// Tests for <see cref="AdAgentCreateExecutor"/>. Like the AWS executor gate tests, the concrete repos
/// hit SQL and cannot be mocked, so we construct a REAL executor bound to a bogus (never-openable)
/// connection string and exercise the gates that run BEFORE any repository/LDAP call: parse, operation,
/// required fields, the defensive forbidden-key rejection, and — critically — the deny-all base-DN
/// containment. The create + read-back path requires a live DB + AD and is proven only at the
/// mock/pure level (BuildCreateObject, the result-JSON shapes) and the BaseDnContainment truth table.
///
/// Every case asserts the executor NEVER throws (returns a clean (false, msg, resultJson)).
/// </summary>
public class AdAgentCreateExecutorTests
{
    private const string Conn = "corp-local";

    // The permitted base-DN SOURCE is now the injectable policy; a fake supplies a fixed list so the
    // executor's containment behaviour is tested independently of the DB (the policy's own DB→config→
    // deny resolution is tested in CreationBaseDnPolicyTests). Credential resolution still hits a bogus,
    // never-openable DB — reached only after containment passes.
    private sealed class FakePolicy : ICreationBaseDnPolicy
    {
        private readonly IReadOnlyList<string> _dns;
        public FakePolicy(IReadOnlyList<string> dns) => _dns = dns;
        public Task<IReadOnlyList<string>> GetPermittedBaseDnsAsync(string sourceConnectionName) => Task.FromResult(_dns);
        public Task<(IReadOnlyList<string> Enforced, BaseDnSource Source)> GetEffectiveAsync(string sourceConnectionName)
            => Task.FromResult((_dns, _dns.Count > 0 ? BaseDnSource.Database : BaseDnSource.None));
        public IReadOnlyList<string> GetConfigBaseDns(string sourceConnectionName) => System.Array.Empty<string>();
        public IReadOnlyList<string> GetConfiguredConnectionNames() => System.Array.Empty<string>();
    }

    private static AdAgentCreateExecutor Build(params string[] permittedBaseDns)
    {
        var dbConfig = new DatabaseConfig
        {
            ConnectionString = "Server=(local);Database=__none__;Trusted_Connection=False;Connect Timeout=1;"
        };
        return new AdAgentCreateExecutor(
            Array.Empty<IConnectorAdapter>(),
            new SinkConnectionCredentialMapRepository(dbConfig),
            new TenantRepository(dbConfig),
            new FakePolicy(permittedBaseDns),
            NullLogger<AdAgentCreateExecutor>.Instance);
    }

    private static string Payload(string? targetOu = "OU=Staff,DC=corp,DC=local", string? attributesJson = null,
        string operation = "CreateUser", string sam = "nora.quint", string sourceConn = Conn)
    {
        var attrs = attributesJson is null ? "" : $",\"attributes\":{attributesJson}";
        return "{" +
            "\"schemaVersion\":1," +
            $"\"operation\":\"{operation}\"," +
            $"\"sourceConnectionName\":\"{sourceConn}\"," +
            $"\"samAccountName\":\"{sam}\"," +
            "\"userPrincipalName\":\"nora.quint@corp.local\"," +
            (targetOu is null ? "" : $"\"targetOu\":\"{targetOu}\",") +
            "\"displayName\":\"Nora Quint\"" +
            attrs +
            "}";
    }

    private static Task<(bool Success, string Message, string? ResultJson)> Run(AdAgentCreateExecutor ex, string? payload)
        => ex.ExecuteAsync(Guid.NewGuid(), payload, CancellationToken.None);

    // ── Parse / schema / operation / required fields ──────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_payload_is_rejected(string? payload)
    {
        var (ok, msg, _) = await Run(Build(), payload);
        Assert.False(ok);
        Assert.Contains("empty payload", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Malformed_json_is_rejected()
    {
        var (ok, msg, _) = await Run(Build(), "this is not json");
        Assert.False(ok);
        Assert.Contains("malformed", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task SchemaVersion_not_1_is_rejected(int version)
    {
        var payload = Payload().Replace("\"schemaVersion\":1", $"\"schemaVersion\":{version}");
        var (ok, msg, _) = await Run(Build("OU=Staff,DC=corp,DC=local"), payload);
        Assert.False(ok);
        Assert.Contains("schemaVersion", msg);
    }

    [Theory]
    [InlineData("CreateAdAccount")]  // that's the verb, not the payload operation
    [InlineData("SetAttributes")]
    [InlineData("createuser")]       // wrong case (Ordinal)
    [InlineData("")]
    public async Task Wrong_operation_is_rejected(string op)
    {
        var (ok, msg, _) = await Run(Build("OU=Staff,DC=corp,DC=local"), Payload(operation: op));
        Assert.False(ok);
        Assert.Contains("not allowed", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_sam_is_rejected()
    {
        var (ok, msg, _) = await Run(Build("OU=Staff,DC=corp,DC=local"), Payload(sam: ""));
        Assert.False(ok);
        Assert.Contains("samAccountName", msg);
    }

    [Fact]
    public async Task Missing_targetOu_is_rejected()
    {
        var (ok, msg, _) = await Run(Build("OU=Staff,DC=corp,DC=local"), Payload(targetOu: null));
        Assert.False(ok);
        Assert.Contains("targetOu", msg);
    }

    // ── Defensive: password / enable / UAC keys hard-rejected ─────────────────

    [Theory]
    [InlineData("password")]
    [InlineData("userPassword")]
    [InlineData("unicodePwd")]
    [InlineData("active")]
    [InlineData("enabled")]
    [InlineData("userAccountControl")]
    [InlineData("accountExpires")]
    public async Task Smuggled_password_or_enable_key_is_rejected(string key)
    {
        var attrs = $"{{\"givenName\":\"Nora\",\"{key}\":\"x\"}}";
        var (ok, msg, _) = await Run(Build("OU=Staff,DC=corp,DC=local"), Payload(attributesJson: attrs));
        Assert.False(ok);
        Assert.Contains("not permitted", msg, StringComparison.OrdinalIgnoreCase);
    }

    // ── ★ Containment (the real control) ──────────────────────────────────────

    [Fact]
    public async Task Empty_allow_list_denies_the_create()
    {
        // No permitted base DNs configured -> deny-all.
        var (ok, msg, resultJson) = await Run(Build(/* none */), Payload());
        Assert.False(ok);
        Assert.Contains("not within any permitted", msg, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(resultJson);
        Assert.Contains("ldapError", resultJson);
    }

    [Fact]
    public async Task TargetOu_outside_the_allow_list_is_denied()
    {
        var (ok, msg, _) = await Run(Build("OU=Contractors,DC=corp,DC=local"), Payload(targetOu: "OU=Staff,DC=corp,DC=local"));
        Assert.False(ok);
        Assert.Contains("not within any permitted", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Contained_targetOu_passes_the_gate_and_only_then_hits_the_unreachable_db()
    {
        // targetOu IS within the permitted base, so containment PASSES; the executor proceeds to
        // credential resolution which fails on the bogus DB — proving it got PAST the containment gate
        // (the failure is NOT a containment refusal). It still never throws.
        var (ok, msg, _) = await Run(Build("OU=Staff,DC=corp,DC=local"), Payload(targetOu: "OU=Staff,DC=corp,DC=local"));
        Assert.False(ok);
        Assert.DoesNotContain("not within any permitted", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Executor_never_throws_on_adversarial_inputs()
    {
        var ex = Build("OU=Staff,DC=corp,DC=local");
        var inputs = new[] { null, "", "{", "{}", "[]", "\"s\"", "123", "null",
            "{\"schemaVersion\":\"x\",\"operation\":\"CreateUser\"}" };
        foreach (var input in inputs)
            Assert.Null(await Record.ExceptionAsync(() => Run(ex, input)));
    }

    // ── Pure helpers: object build + result shapes + sanitizer ────────────────

    [Fact]
    public void BuildCreateObject_has_no_password_or_uac_and_keeps_passthrough_attrs()
    {
        var payload = JsonSerializer.Deserialize<AdAgentCreateExecutor.CreateAdAccountPayload>(
            Payload(attributesJson: "{\"givenName\":\"Nora\",\"sn\":\"Quint\",\"department\":\"Finance\"}"))!;

        var obj = AdAgentCreateExecutor.BuildCreateObject(payload, "nora.quint", "OU=Staff,DC=corp,DC=local");

        Assert.Equal("nora.quint", obj.Attributes["sAMAccountName"]);
        Assert.Equal("OU=Staff,DC=corp,DC=local", obj.Attributes["targetOU"]);
        Assert.Equal("Nora", obj.Attributes["givenName"]);
        Assert.Equal("Finance", obj.Attributes["department"]);
        // No password / enable / UAC keys anywhere.
        foreach (var key in new[] { "password", "userPassword", "unicodePwd", "active", "enabled", "userAccountControl", "accountExpires" })
            Assert.False(obj.Attributes.ContainsKey(key), $"forbidden key leaked: {key}");
    }

    [Fact]
    public void BuildCreateObject_passthrough_cannot_redirect_ou_or_forge_identity()
    {
        // A hostile bag tries to redirect the create OU (any case) and forge the sam/UPN AFTER the
        // validated targetOu passed containment. The controlled keys, written last, must win — and the
        // malicious values must not appear anywhere.
        var payload = JsonSerializer.Deserialize<AdAgentCreateExecutor.CreateAdAccountPayload>(
            Payload(attributesJson:
                "{\"givenName\":\"Nora\"," +
                "\"targetou\":\"CN=Users,DC=corp,DC=local\"," +
                "\"TargetOU\":\"OU=Domain Controllers,DC=corp,DC=local\"," +
                "\"ou\":\"OU=Admins,DC=corp,DC=local\"," +
                "\"sAMAccountName\":\"evil.admin\"," +
                "\"cn\":\"evil\"," +
                "\"userPrincipalName\":\"evil@corp.local\"}"))!;

        var obj = AdAgentCreateExecutor.BuildCreateObject(payload, "nora.quint", "OU=Staff,DC=corp,DC=local");

        Assert.Equal("OU=Staff,DC=corp,DC=local", obj.Attributes["targetOU"]); // validated wins
        Assert.Equal("nora.quint", obj.Attributes["sAMAccountName"]);          // validated wins
        Assert.False(obj.Attributes.ContainsKey("ou"));                        // alias location key not present
        Assert.False(obj.Attributes.ContainsKey("cn"));                        // structural key dropped
        Assert.Equal("Nora", obj.Attributes["givenName"]);                     // legitimate passthrough survives
        // None of the malicious values leaked into any attribute.
        foreach (var kvp in obj.Attributes)
        {
            var v = kvp.Value as string;
            Assert.NotEqual("CN=Users,DC=corp,DC=local", v);
            Assert.NotEqual("OU=Domain Controllers,DC=corp,DC=local", v);
            Assert.NotEqual("OU=Admins,DC=corp,DC=local", v);
            Assert.NotEqual("evil.admin", v);
            Assert.NotEqual("evil@corp.local", v);
        }
    }

    [Theory]
    [InlineData("targetOU")]
    [InlineData("targetou")]
    [InlineData("TARGETOU")]
    [InlineData("ou")]
    [InlineData("sAMAccountName")]
    [InlineData("samaccountname")]
    [InlineData("userName")]
    [InlineData("cn")]
    [InlineData("displayName")]
    [InlineData("userPrincipalName")]
    public async Task Smuggled_location_or_identity_key_in_the_bag_is_rejected(string key)
    {
        var attrs = $"{{\"givenName\":\"Nora\",\"{key}\":\"x\"}}";
        var (ok, msg, _) = await Run(Build("OU=Staff,DC=corp,DC=local"), Payload(attributesJson: attrs));
        Assert.False(ok);
        Assert.Contains("not permitted", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Success_result_json_carries_guid_and_dn()
    {
        var guid = Guid.Parse("8f1c0000-0000-0000-0000-000000000003");
        var json = AdAgentCreateExecutor.BuildSuccessResultJson(guid, "CN=Nora,OU=Staff,DC=corp,DC=local", "nora.quint", "nora.quint@corp.local");
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(guid.ToString(), root.GetProperty("objectGuid").GetString());
        Assert.Equal("CN=Nora,OU=Staff,DC=corp,DC=local", root.GetProperty("distinguishedName").GetString());
        Assert.False(root.GetProperty("accountEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ldapError").ValueKind);
    }

    [Fact]
    public void Degraded_result_json_has_null_guid_but_a_dn()
    {
        var json = AdAgentCreateExecutor.BuildSuccessResultJson(null, "CN=Nora,OU=Staff,DC=corp,DC=local", "nora.quint", null);
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("objectGuid").ValueKind);
        Assert.Equal("CN=Nora,OU=Staff,DC=corp,DC=local", root.GetProperty("distinguishedName").GetString());
    }

    [Fact]
    public void Failure_result_json_carries_the_ldap_error_only()
    {
        var json = AdAgentCreateExecutor.BuildFailureResultJson("CONSTRAINT_VIOLATION");
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("CONSTRAINT_VIOLATION", root.GetProperty("ldapError").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("objectGuid").ValueKind);
    }

    [Fact]
    public void Sanitizer_strips_crlf_control_and_angle_brackets()
    {
        var cleaned = AdAgentCreateExecutor.SanitizeLdapError("bad\r\n<OU>  error");
        Assert.DoesNotContain("\r", cleaned);
        Assert.DoesNotContain("\n", cleaned);
        Assert.DoesNotContain("<", cleaned);
        Assert.DoesNotContain(">", cleaned);
        Assert.Contains("bad", cleaned);
    }
}
