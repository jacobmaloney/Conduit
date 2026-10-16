using System.Text.Json;
using Conduit.Connectors.ActiveDirectory;
using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// UC008-D07 (enable-on-create) and UC008-D04(a)/F08 (search-before-create → linked) for
/// <see cref="AdAgentCreateExecutor"/>. The gates that run before credential resolution are covered
/// by <see cref="AdAgentCreateExecutorTests"/>; these drive the post-validation phase
/// (<see cref="AdAgentCreateExecutor.ProvisionAsync"/>) through a fake <see cref="IAdAccountCreateSink"/>
/// that records every call and the exact ConnectorObject it was handed, so the tests prove
/// (a) the secure-channel refusal happens BEFORE any create, with positive controls either side,
/// (b) password/active are placed only when the executor was asked and never from the bag,
/// (c) each result JSON shape tells the truth (created-disabled, created-enabled, linked, failure), and
/// (d) the generated password never leaves the executor (not in the message, not in the JSON).
/// </summary>
public class AdAgentCreateExecutorEnableAndLinkTests
{
    private const string Ou = "OU=Staff,DC=corp,DC=local";
    private const string Dn = "CN=Nora Quint,OU=Staff,DC=corp,DC=local";
    private const string ExistingDn = "CN=Nora Quint (old),OU=Contractors,DC=corp,DC=local";
    private static readonly Guid ReadBackGuid = Guid.Parse("8f1c0000-0000-0000-0000-000000000003");
    private static readonly Guid ExistingGuid = Guid.Parse("8f1c0000-0000-0000-0000-00000000000e");

    private sealed class FakeSink : IAdAccountCreateSink
    {
        public bool Secure = true;
        public AdExistingAccount? Existing;
        public Exception? PrecheckThrows;
        public Func<ConnectorObject, ProvisionResult> OnCreate = _ => ProvisionResult.Success(Dn);
        public Guid? ReadBack = ReadBackGuid;
        public ProvisionResult ResetResult = ProvisionResult.Success(Dn);
        public SinkWriteResult EnableResult = SinkWriteResult.Ok(SinkWriteOutcome.Updated);

        public int PrecheckCalls, CreateCalls, ResetCalls, EnableCalls;
        public ConnectorObject? CreatedWith;
        public string? ResetPasswordValue;
        public bool ResetRequireChange;

        public Task<AdCreatePrecheck> PrecheckCreateAsync(string sam, string? upn, CancellationToken ct)
        {
            PrecheckCalls++;
            if (PrecheckThrows is not null) throw PrecheckThrows;
            return Task.FromResult(new AdCreatePrecheck(Secure, Existing));
        }

        public Task<ProvisionResult> CreateAsync(ConnectorObject newObject, CancellationToken ct)
        {
            CreateCalls++;
            CreatedWith = newObject;
            return Task.FromResult(OnCreate(newObject));
        }

        public Task<Guid?> ReadObjectGuidByDnAsync(string distinguishedName, CancellationToken ct) => Task.FromResult(ReadBack);

        public Task<ProvisionResult> ResetPasswordAsync(string externalId, string newPassword, bool requireChangeAtNextLogin, CancellationToken ct)
        {
            ResetCalls++;
            ResetPasswordValue = newPassword;
            ResetRequireChange = requireChangeAtNextLogin;
            return Task.FromResult(ResetResult);
        }

        public Task<SinkWriteResult> SetEnabledByGuidAsync(string objectGuid, bool enabled, CancellationToken ct)
        {
            EnableCalls++;
            return Task.FromResult(EnableResult);
        }
    }

    private static AdAgentCreateExecutor Executor()
    {
        // Repositories are real but bound to a never-openable connection; ProvisionAsync never touches them.
        var dbConfig = new DatabaseConfig { ConnectionString = "Server=(local);Database=__none__;Trusted_Connection=False;Connect Timeout=1;" };
        return new AdAgentCreateExecutor(
            Array.Empty<IConnectorAdapter>(),
            new SinkConnectionCredentialMapRepository(dbConfig),
            new TenantRepository(dbConfig),
            new AdAgentCreateExecutorTests.FakePolicy(new[] { Ou }),
            NullLogger<AdAgentCreateExecutor>.Instance);
    }

    private static AdAgentCreateExecutor.CreateAdAccountPayload Payload(bool? enableOnCreate = null, bool? mustChange = null, string? attributesJson = null)
    {
        var json = "{" +
            "\"schemaVersion\":1,\"operation\":\"CreateUser\",\"sourceConnectionName\":\"corp-local\"," +
            "\"samAccountName\":\"nora.quint\",\"userPrincipalName\":\"nora.quint@corp.local\"," +
            $"\"targetOu\":\"{Ou}\",\"displayName\":\"Nora Quint\"" +
            (enableOnCreate is null ? "" : $",\"enableOnCreate\":{(enableOnCreate.Value ? "true" : "false")}") +
            (mustChange is null ? "" : $",\"mustChangePasswordAtLogon\":{(mustChange.Value ? "true" : "false")}") +
            (attributesJson is null ? "" : $",\"attributes\":{attributesJson}") +
            "}";
        return JsonSerializer.Deserialize<AdAgentCreateExecutor.CreateAdAccountPayload>(json)!;
    }

    private static Task<(bool Success, string Message, string? ResultJson)> Provision(FakeSink sink, AdAgentCreateExecutor.CreateAdAccountPayload p)
        => Executor().ProvisionAsync(Guid.NewGuid(), sink, p, "nora.quint", Ou, CancellationToken.None);

    private static JsonElement Root(string? json) => JsonDocument.Parse(json!).RootElement;

    private static string? PasswordOn(ConnectorObject? o) => o is not null && o.Attributes.TryGetValue("password", out var v) ? v as string : null;

    // ── Payload parsing ───────────────────────────────────────────────────────

    [Fact]
    public void EnableOnCreate_parses_and_defaults_to_false()
    {
        Assert.False(Payload().EnableOnCreate);
        Assert.False(Payload(enableOnCreate: false).EnableOnCreate);
        Assert.True(Payload(enableOnCreate: true).EnableOnCreate);
    }

    [Fact]
    public void MustChangePasswordAtLogon_parses_and_defaults_to_false()
    {
        Assert.False(Payload().MustChangePasswordAtLogon);
        Assert.True(Payload(mustChange: true).MustChangePasswordAtLogon);
    }

    // ── BuildCreateObject: credential keys only when asked, never from the bag ────

    [Fact]
    public void BuildCreateObject_without_password_places_no_credential_key_even_when_the_bag_tries()
    {
        var p = Payload(enableOnCreate: true, mustChange: true,
            attributesJson: "{\"givenName\":\"Nora\",\"password\":\"smuggled\",\"active\":\"true\",\"mustChangePasswordAtLogon\":\"true\",\"pwdLastSet\":\"0\"}");

        // enableOnCreate on the payload is NOT enough: only the executor's generated password places the keys.
        var obj = AdAgentCreateExecutor.BuildCreateObject(p, "nora.quint", Ou);

        foreach (var key in new[] { "password", "userPassword", "unicodePwd", "active", "enabled", "userAccountControl", "accountExpires", "pwdLastSet", "mustChangePasswordAtLogon" })
            Assert.False(obj.Attributes.ContainsKey(key), $"credential key leaked: {key}");
        Assert.Equal("Nora", obj.Attributes["givenName"]);
    }

    [Fact]
    public void BuildCreateObject_with_password_places_password_active_and_mustChange_from_validated_fields_only()
    {
        var p = Payload(enableOnCreate: true, mustChange: true,
            attributesJson: "{\"givenName\":\"Nora\",\"password\":\"smuggled\",\"active\":\"false\",\"mustChangePasswordAtLogon\":\"false\"}");

        var obj = AdAgentCreateExecutor.BuildCreateObject(p, "nora.quint", Ou, "G3nerated-Locally!x");

        Assert.Equal("G3nerated-Locally!x", obj.Attributes["password"]);  // the executor's value, not the bag's
        Assert.Equal(true, obj.Attributes["active"]);                       // always true when a password is placed
        Assert.Equal(true, obj.Attributes["mustChangePasswordAtLogon"]);    // the validated top-level field, not the bag
        Assert.Equal("Nora", obj.Attributes["givenName"]);
        foreach (var v in obj.Attributes.Values) Assert.NotEqual("smuggled", v as string);
    }

    [Fact]
    public void BuildCreateObject_with_password_and_mustChange_false_carries_false()
    {
        var obj = AdAgentCreateExecutor.BuildCreateObject(Payload(enableOnCreate: true, mustChange: false), "nora.quint", Ou, "G3nerated-Locally!x");
        Assert.Equal(false, obj.Attributes["mustChangePasswordAtLogon"]);
    }

    // ── The secure-channel refusal, BEFORE any create, with positive controls ──

    [Fact]
    public async Task Enable_on_create_over_a_plaintext_connection_is_refused_before_any_create()
    {
        var sink = new FakeSink { Secure = false };

        var (ok, msg, json) = await Provision(sink, Payload(enableOnCreate: true));

        Assert.False(ok);
        Assert.Contains("secure (LDAPS/StartTLS) connection", msg);
        Assert.Contains("create refused", msg);
        Assert.Equal(1, sink.PrecheckCalls);
        Assert.Equal(0, sink.CreateCalls);     // nothing was created
        Assert.Equal(0, sink.ResetCalls);
        Assert.Equal(0, sink.EnableCalls);
        var root = Root(json);
        Assert.Contains("secure", root.GetProperty("ldapError").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("objectGuid").ValueKind);
        Assert.False(root.TryGetProperty("linked", out _));
    }

    [Fact]
    public async Task Positive_control_enable_on_create_over_a_secure_connection_creates_with_a_generated_password()
    {
        var sink = new FakeSink { Secure = true, OnCreate = _ => new ProvisionResult { Outcome = ProvisionOutcome.Success, ExternalId = Dn, PasswordSet = true, AccountEnabled = true } };

        var (ok, _, _) = await Provision(sink, Payload(enableOnCreate: true, mustChange: true));

        Assert.True(ok);
        Assert.Equal(1, sink.CreateCalls);
        var pw = PasswordOn(sink.CreatedWith);
        Assert.NotNull(pw);
        Assert.Equal(24, pw!.Length);
        Assert.Equal(true, sink.CreatedWith!.Attributes["active"]);
        Assert.Equal(true, sink.CreatedWith.Attributes["mustChangePasswordAtLogon"]);
    }

    [Fact]
    public async Task Positive_control_no_enable_over_a_plaintext_connection_still_creates_disabled_as_today()
    {
        var sink = new FakeSink { Secure = false };

        var (ok, msg, json) = await Provision(sink, Payload(enableOnCreate: false));

        Assert.True(ok);
        Assert.Equal(1, sink.CreateCalls);
        Assert.Null(PasswordOn(sink.CreatedWith));
        Assert.False(sink.CreatedWith!.Attributes.ContainsKey("active"));
        Assert.Equal($"Created {Dn}.", msg);     // today's exact message
        Assert.False(Root(json).GetProperty("accountEnabled").GetBoolean());
    }

    // ── Result JSON shapes ────────────────────────────────────────────────────

    [Fact]
    public async Task Created_disabled_result_shape()
    {
        var (ok, msg, json) = await Provision(new FakeSink(), Payload());

        Assert.True(ok);
        Assert.Equal($"Created {Dn}.", msg);
        var root = Root(json);
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(ReadBackGuid.ToString(), root.GetProperty("objectGuid").GetString());
        Assert.Equal(Dn, root.GetProperty("distinguishedName").GetString());
        Assert.Equal("nora.quint", root.GetProperty("samAccountName").GetString());
        Assert.Equal("nora.quint@corp.local", root.GetProperty("userPrincipalName").GetString());
        Assert.False(root.GetProperty("accountEnabled").GetBoolean());
        Assert.False(root.GetProperty("passwordSet").GetBoolean());
        Assert.False(root.GetProperty("linked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ldapError").ValueKind);
    }

    [Fact]
    public async Task Created_enabled_result_shape()
    {
        var sink = new FakeSink { OnCreate = _ => new ProvisionResult { Outcome = ProvisionOutcome.Success, ExternalId = Dn, PasswordSet = true, AccountEnabled = true } };

        var (ok, msg, json) = await Provision(sink, Payload(enableOnCreate: true, mustChange: true));

        Assert.True(ok);
        Assert.StartsWith($"Created {Dn}.", msg);
        Assert.Contains("Enabled with an initial password (must change at next logon)", msg);
        var root = Root(json);
        Assert.Equal(ReadBackGuid.ToString(), root.GetProperty("objectGuid").GetString());
        Assert.True(root.GetProperty("accountEnabled").GetBoolean());
        Assert.True(root.GetProperty("passwordSet").GetBoolean());
        Assert.False(root.GetProperty("linked").GetBoolean());
        Assert.Equal(0, sink.ResetCalls);   // no retry when the first password landed
    }

    [Fact]
    public async Task Enable_requested_but_the_sink_only_set_the_password_reports_enabled_false()
    {
        // Password landed, enable write failed inside the sink: accountEnabled must NOT be claimed.
        var sink = new FakeSink { OnCreate = _ => new ProvisionResult { Outcome = ProvisionOutcome.Success, ExternalId = Dn, PasswordSet = true, AccountEnabled = false, Warning = "password set but enable failed: busy" } };

        var (ok, msg, json) = await Provision(sink, Payload(enableOnCreate: true));

        Assert.True(ok);
        Assert.Contains("NOT applied", msg);
        Assert.Contains("enable failed: busy", msg);
        var root = Root(json);
        Assert.False(root.GetProperty("accountEnabled").GetBoolean());
        Assert.True(root.GetProperty("passwordSet").GetBoolean());
        Assert.Equal(0, sink.ResetCalls);   // the password is set; no second password is generated
    }

    [Fact]
    public async Task Password_policy_refusal_retries_once_at_32_chars_then_enables()
    {
        var sink = new FakeSink
        {
            OnCreate = _ => new ProvisionResult { Outcome = ProvisionOutcome.Success, ExternalId = Dn, PasswordSet = false, Warning = "initial password refused: policy" },
            ResetResult = ProvisionResult.Success(Dn),
            EnableResult = SinkWriteResult.Ok(SinkWriteOutcome.Updated)
        };

        var (ok, msg, json) = await Provision(sink, Payload(enableOnCreate: true, mustChange: true));

        Assert.True(ok);
        Assert.Equal(1, sink.ResetCalls);
        Assert.Equal(32, sink.ResetPasswordValue!.Length);
        Assert.True(sink.ResetRequireChange);
        Assert.Equal(1, sink.EnableCalls);
        Assert.NotEqual(PasswordOn(sink.CreatedWith), sink.ResetPasswordValue);
        var root = Root(json);
        Assert.True(root.GetProperty("passwordSet").GetBoolean());
        Assert.True(root.GetProperty("accountEnabled").GetBoolean());
        Assert.Contains("Enabled with an initial password", msg);
        Assert.DoesNotContain("refused", msg);
    }

    [Fact]
    public async Task Password_policy_refusal_on_both_attempts_reports_created_but_disabled()
    {
        var sink = new FakeSink
        {
            OnCreate = _ => new ProvisionResult { Outcome = ProvisionOutcome.Success, ExternalId = Dn, PasswordSet = false, Warning = "initial password refused: policy" },
            ResetResult = ProvisionResult.Failed("CONSTRAINT_VIOLATION: password policy")
        };

        var (ok, msg, json) = await Provision(sink, Payload(enableOnCreate: true));

        Assert.True(ok);                                  // the account exists; the result says what it is
        Assert.Equal(1, sink.ResetCalls);
        Assert.Equal(0, sink.EnableCalls);
        Assert.Contains("NOT applied", msg);
        Assert.Contains("password policy refused", msg);
        var root = Root(json);
        Assert.False(root.GetProperty("passwordSet").GetBoolean());
        Assert.False(root.GetProperty("accountEnabled").GetBoolean());
        Assert.Equal(Dn, root.GetProperty("distinguishedName").GetString());
    }

    [Fact]
    public async Task Existing_account_is_linked_not_created_and_nothing_is_written()
    {
        var sink = new FakeSink { Secure = false, Existing = new AdExistingAccount(ExistingDn, ExistingGuid, Enabled: true, "sAMAccountName") };

        // Even with enable-on-create on a plaintext channel: the link wins and no refusal is raised,
        // because nothing needs to be written.
        var (ok, msg, json) = await Provision(sink, Payload(enableOnCreate: true));

        Assert.True(ok);
        Assert.Equal($"Linked existing account {ExistingDn}.", msg);
        Assert.Equal(0, sink.CreateCalls);
        Assert.Equal(0, sink.ResetCalls);
        Assert.Equal(0, sink.EnableCalls);
        var root = Root(json);
        Assert.True(root.GetProperty("linked").GetBoolean());
        Assert.Equal(ExistingGuid.ToString(), root.GetProperty("objectGuid").GetString());
        Assert.Equal(ExistingDn, root.GetProperty("distinguishedName").GetString());
        Assert.True(root.GetProperty("accountEnabled").GetBoolean());   // the directory's UAC, as read
        Assert.False(root.GetProperty("passwordSet").GetBoolean());
    }

    [Fact]
    public async Task Existing_disabled_account_reports_enabled_false()
    {
        var sink = new FakeSink { Existing = new AdExistingAccount(ExistingDn, ExistingGuid, Enabled: false, "userPrincipalName") };
        var (ok, _, json) = await Provision(sink, Payload());
        Assert.True(ok);
        Assert.True(Root(json).GetProperty("linked").GetBoolean());
        Assert.False(Root(json).GetProperty("accountEnabled").GetBoolean());
    }

    [Fact]
    public async Task Existing_account_with_unread_uac_reports_enabled_false_and_says_so()
    {
        var sink = new FakeSink { Existing = new AdExistingAccount(ExistingDn, null, Enabled: null, "sAMAccountName") };
        var (ok, msg, json) = await Provision(sink, Payload());
        Assert.True(ok);
        Assert.StartsWith($"Linked existing account {ExistingDn}.", msg);
        Assert.Contains("not read", msg);
        var root = Root(json);
        Assert.True(root.GetProperty("linked").GetBoolean());
        Assert.False(root.GetProperty("accountEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("objectGuid").ValueKind);
    }

    [Fact]
    public async Task Sink_internal_link_is_reported_as_linked_with_nothing_claimed()
    {
        // The pre-search missed (race) and the sink's own create-else-link attached to the existing DN.
        var sink = new FakeSink { OnCreate = _ => ProvisionResult.LinkedExisting(ExistingDn) };

        var (ok, msg, json) = await Provision(sink, Payload(enableOnCreate: true));

        Assert.True(ok);
        Assert.StartsWith($"Linked existing account {ExistingDn}.", msg);
        Assert.Equal(0, sink.ResetCalls);
        var root = Root(json);
        Assert.True(root.GetProperty("linked").GetBoolean());
        Assert.False(root.GetProperty("passwordSet").GetBoolean());
        Assert.False(root.GetProperty("accountEnabled").GetBoolean());
        Assert.Equal(ExistingDn, root.GetProperty("distinguishedName").GetString());
    }

    [Fact]
    public async Task Failure_result_shape()
    {
        var sink = new FakeSink { OnCreate = _ => ProvisionResult.Failed("CONSTRAINT_VIOLATION <ou>\r\nbad") };

        var (ok, msg, json) = await Provision(sink, Payload(enableOnCreate: true));

        Assert.False(ok);
        Assert.StartsWith("CreateAdAccount: CONSTRAINT_VIOLATION", msg);
        var root = Root(json);
        Assert.Contains("CONSTRAINT_VIOLATION", root.GetProperty("ldapError").GetString());
        Assert.DoesNotContain("<", root.GetProperty("ldapError").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("objectGuid").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("distinguishedName").ValueKind);
        Assert.False(root.TryGetProperty("linked", out _));
    }

    [Fact]
    public async Task Degraded_readback_still_succeeds_with_null_guid()
    {
        var sink = new FakeSink { ReadBack = null };
        var (ok, msg, json) = await Provision(sink, Payload());
        Assert.True(ok);
        Assert.Equal($"Created (degraded — objectGUID read-back failed) {Dn}.", msg);
        Assert.Equal(JsonValueKind.Null, Root(json).GetProperty("objectGuid").ValueKind);
    }

    [Fact]
    public async Task Precheck_failure_fails_closed_without_a_create()
    {
        var sink = new FakeSink { PrecheckThrows = new InvalidOperationException("RootDSE unreadable <x>") };

        var (ok, msg, json) = await Provision(sink, Payload());

        Assert.False(ok);
        Assert.Equal(0, sink.CreateCalls);
        Assert.Contains("RootDSE unreadable", msg);
        Assert.DoesNotContain("<", msg);
        Assert.NotNull(json);
    }

    [Fact]
    public async Task Provision_never_throws_when_the_sink_throws()
    {
        var sink = new FakeSink { OnCreate = _ => throw new InvalidOperationException("boom") };
        var ex = await Record.ExceptionAsync(() => Provision(sink, Payload(enableOnCreate: true)));
        Assert.Null(ex);
    }

    // ── The password never leaves the executor ────────────────────────────────

    [Fact]
    public async Task Generated_password_never_appears_in_message_or_result_on_the_create_path()
    {
        var sink = new FakeSink { OnCreate = _ => new ProvisionResult { Outcome = ProvisionOutcome.Success, ExternalId = Dn, PasswordSet = true, AccountEnabled = true } };

        var (_, msg, json) = await Provision(sink, Payload(enableOnCreate: true, mustChange: true));

        var pw = PasswordOn(sink.CreatedWith)!;
        Assert.Equal(24, pw.Length);
        Assert.DoesNotContain(pw, msg);
        Assert.DoesNotContain(pw, json!);
    }

    [Fact]
    public async Task Generated_passwords_never_appear_in_message_or_result_on_the_retry_path()
    {
        var sink = new FakeSink
        {
            OnCreate = _ => new ProvisionResult { Outcome = ProvisionOutcome.Success, ExternalId = Dn, PasswordSet = false, Warning = "initial password refused: policy" },
            ResetResult = ProvisionResult.Failed("still refused")
        };

        var (_, msg, json) = await Provision(sink, Payload(enableOnCreate: true));

        var first = PasswordOn(sink.CreatedWith)!;
        var second = sink.ResetPasswordValue!;
        Assert.Equal(24, first.Length);
        Assert.Equal(32, second.Length);
        foreach (var pw in new[] { first, second })
        {
            Assert.DoesNotContain(pw, msg);
            Assert.DoesNotContain(pw, json!);
        }
    }

    [Fact]
    public async Task Generated_password_never_appears_when_the_sink_fails_the_create()
    {
        var sink = new FakeSink { OnCreate = o => ProvisionResult.Failed("refused " + (o.Attributes["password"] as string)!.Length) };

        var (ok, msg, json) = await Provision(sink, Payload(enableOnCreate: true));

        Assert.False(ok);
        var pw = PasswordOn(sink.CreatedWith)!;
        Assert.DoesNotContain(pw, msg);
        Assert.DoesNotContain(pw, json!);
    }

    // ── Success-JSON helper defaults keep the old 4-argument shape honest ─────

    [Fact]
    public void Success_result_json_defaults_report_passwordSet_and_linked_false()
    {
        var root = Root(AdAgentCreateExecutor.BuildSuccessResultJson(ReadBackGuid, Dn, "nora.quint", null));
        Assert.False(root.GetProperty("accountEnabled").GetBoolean());
        Assert.False(root.GetProperty("passwordSet").GetBoolean());
        Assert.False(root.GetProperty("linked").GetBoolean());
    }
}
