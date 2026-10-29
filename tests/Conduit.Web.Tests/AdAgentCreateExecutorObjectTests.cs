using System.DirectoryServices.Protocols;
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
/// CREATE-CONSOLE-02: the "CreateAdAccount" command also creates a group, an organizational unit, a computer or a
/// contact. The gates that run before any LDAP call are exercised on a real executor bound to a never-openable DB
/// (as <see cref="AdAgentCreateExecutorTests"/> does); the post-validation phase runs against a fake sink; the
/// sink's add-request builder is pure and pinned per class.
/// </summary>
public class AdAgentCreateExecutorObjectTests
{
    private const string Ou = "OU=Contractors,OU=Users,OU=Organization,DC=Domain,DC=Local";

    private static AdAgentCreateExecutor Build(params string[] permitted)
    {
        var dbConfig = new DatabaseConfig { ConnectionString = "Server=(local);Database=__none__;Trusted_Connection=False;Connect Timeout=1;" };
        return new AdAgentCreateExecutor(
            Array.Empty<IConnectorAdapter>(),
            new SinkConnectionCredentialMapRepository(dbConfig),
            new TenantRepository(dbConfig),
            new AdAgentCreateExecutorTests.FakePolicy(permitted),
            NullLogger<AdAgentCreateExecutor>.Instance);
    }

    private static string Payload(string operation, string? objectClass = null, string? sam = null, string? displayName = "Field Ops",
        string? targetOu = Ou, string? attributesJson = null)
        => "{\"schemaVersion\":1,\"operation\":\"" + operation + "\",\"sourceConnectionName\":\"corp-local\""
           + (objectClass is null ? "" : ",\"objectClass\":\"" + objectClass + "\"")
           + (sam is null ? "" : ",\"samAccountName\":\"" + sam + "\"")
           + (displayName is null ? "" : ",\"displayName\":\"" + displayName + "\"")
           + (targetOu is null ? "" : ",\"targetOu\":\"" + targetOu + "\"")
           + (attributesJson is null ? "" : ",\"attributes\":" + attributesJson)
           + "}";

    private static AdAgentCreateExecutor.CreateAdAccountPayload Parse(string json)
        => JsonSerializer.Deserialize<AdAgentCreateExecutor.CreateAdAccountPayload>(json)!;

    private static Task<(bool Success, string Message, string? ResultJson)> Run(string payload)
        => Build(Ou).ExecuteAsync(Guid.NewGuid(), payload, CancellationToken.None);

    // ── the operation table ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Five_operations_name_five_classes()
    {
        Assert.Equal(new[] { "CreateUser", "CreateGroup", "CreateOrganizationalUnit", "CreateComputer", "CreateContact" },
            AdAgentCreateExecutor.OperationClasses.Keys.ToArray());
        Assert.Equal("organizationalunit", AdAgentCreateExecutor.OperationClasses["CreateOrganizationalUnit"]);
    }

    [Fact]
    public async Task An_unknown_operation_is_still_refused()
    {
        var (ok, msg, _) = await Run(Payload("CreatePrinter", sam: "p1"));
        Assert.False(ok);
        Assert.Contains("not allowed", msg);
    }

    [Fact]
    public async Task A_payload_class_that_contradicts_the_operation_is_refused()
    {
        var (ok, msg, _) = await Run(Payload("CreateGroup", objectClass: "user", sam: "g1"));
        Assert.False(ok);
        Assert.Contains("creates a group, not 'user'", msg);
    }

    [Theory]
    [InlineData("CreateGroup")]
    [InlineData("CreateComputer")]
    public async Task A_group_or_computer_needs_a_samAccountName(string op)
    {
        var (ok, msg, _) = await Run(Payload(op));
        Assert.False(ok);
        Assert.Contains("samAccountName is required", msg);
    }

    [Theory]
    [InlineData("CreateOrganizationalUnit", "organizationalunit")]
    [InlineData("CreateContact", "contact")]
    public async Task An_OU_or_contact_needs_a_name_not_a_samAccountName(string op, string cls)
    {
        var (ok, msg, _) = await Run(Payload(op, displayName: null));
        Assert.False(ok);
        Assert.Contains($"displayName (the {cls}'s name) is required", msg);
    }

    [Fact]
    public async Task Containment_still_runs_for_every_class_before_any_credential_or_LDAP_work()
    {
        var (ok, msg, _) = await Build("OU=Staff,DC=corp,DC=local").ExecuteAsync(Guid.NewGuid(),
            Payload("CreateOrganizationalUnit", targetOu: Ou), CancellationToken.None);
        Assert.False(ok);
        Assert.Contains("not within any permitted creation base DN", msg);
    }

    [Fact]
    public async Task A_forbidden_or_structural_key_in_the_bag_is_refused_for_a_group_too()
    {
        var (ok, msg, _) = await Run(Payload("CreateGroup", sam: "g1", attributesJson: "{\"targetOU\":\"OU=Elsewhere,DC=x\"}"));
        Assert.False(ok);
        Assert.Contains("not permitted in the bag", msg);
    }

    // ── the object the sink receives ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_group_object_carries_sam_name_OU_and_the_bag_with_the_controlled_keys_last()
    {
        var p = Parse(Payload("CreateGroup", sam: "Granite-Contractors", displayName: "Granite-Contractors",
            attributesJson: "{\"description\":\"Field contractors\",\"groupType\":\"-2147483646\",\"cn\":\"Forged\"}"));
        var o = AdAgentCreateExecutor.BuildObjectCreateObject(p, "group", "Granite-Contractors", Ou);

        Assert.Equal("group", o.ObjectClass);
        Assert.Equal("Granite-Contractors", o.Attributes["sAMAccountName"]);
        Assert.Equal("Granite-Contractors", o.Attributes["cn"]);
        Assert.Equal(Ou, o.Attributes["targetOU"]);
        Assert.Equal("Field contractors", o.Attributes["description"]);
        Assert.Equal("-2147483646", o.Attributes["groupType"]);
        Assert.False(o.Attributes.ContainsKey("password"));
    }

    [Fact]
    public void The_OU_object_has_no_sam_and_no_displayName_only_its_name()
    {
        var o = AdAgentCreateExecutor.BuildObjectCreateObject(Parse(Payload("CreateOrganizationalUnit")), "organizationalunit", null, Ou);
        Assert.Equal("Field Ops", o.Attributes["cn"]);
        Assert.False(o.Attributes.ContainsKey("sAMAccountName"));
        Assert.False(o.Attributes.ContainsKey("displayName"));
        Assert.Equal("Field Ops", o.SourceId);
    }

    [Fact]
    public void The_computer_object_is_named_without_the_dollar()
    {
        var o = AdAgentCreateExecutor.BuildObjectCreateObject(Parse(Payload("CreateComputer", sam: "GC-LAPTOP-07$", displayName: null)), "computer", "GC-LAPTOP-07$", Ou);
        Assert.Equal("GC-LAPTOP-07$", o.Attributes["sAMAccountName"]);
        Assert.Equal("GC-LAPTOP-07", o.Attributes["cn"]);
    }

    // ── the post-validation phase against a fake sink ─────────────────────────────────────────────

    private sealed class FakeSink : IAdAccountCreateSink
    {
        public Func<ConnectorObject, ProvisionResult> OnCreate = o => ProvisionResult.Success("OU=Field Ops," + Ou);
        public Guid? ReadBack = Guid.Parse("8f1c0000-0000-0000-0000-000000000031");
        public ConnectorObject? CreatedWith;
        public int UserCreateCalls;

        public Task<AdCreatePrecheck> PrecheckCreateAsync(string sam, string? upn, CancellationToken ct) => throw new InvalidOperationException("no precheck for a non-user");
        public Task<ProvisionResult> CreateAsync(ConnectorObject newObject, CancellationToken ct) { UserCreateCalls++; throw new InvalidOperationException("the user create must not run"); }
        public Task<ProvisionResult> CreateDirectoryObjectAsync(ConnectorObject newObject, CancellationToken ct) { CreatedWith = newObject; return Task.FromResult(OnCreate(newObject)); }
        public Task<Guid?> ReadObjectGuidByDnAsync(string distinguishedName, CancellationToken ct) => Task.FromResult(ReadBack);
        public Task<ProvisionResult> ResetPasswordAsync(string externalId, string newPassword, bool requireChangeAtNextLogin, CancellationToken ct) => throw new InvalidOperationException();
        public Task<SinkWriteResult> SetEnabledByGuidAsync(string objectGuid, bool enabled, CancellationToken ct) => throw new InvalidOperationException();
    }

    private static Task<(bool Success, string Message, string? ResultJson)> Provision(FakeSink sink, string payload, string cls, string? sam)
        => Build(Ou).ProvisionObjectAsync(Guid.NewGuid(), sink, Parse(payload), cls, sam, Ou, CancellationToken.None);

    [Fact]
    public async Task A_created_OU_reports_the_DN_and_the_objectGUID_with_no_credential_claims()
    {
        var sink = new FakeSink();
        var (ok, msg, json) = await Provision(sink, Payload("CreateOrganizationalUnit"), "organizationalunit", null);

        Assert.True(ok);
        Assert.Equal("Created OU=Field Ops," + Ou + ".", msg);
        var root = JsonDocument.Parse(json!).RootElement;
        Assert.Equal("OU=Field Ops," + Ou, root.GetProperty("distinguishedName").GetString());
        Assert.Equal(sink.ReadBack.ToString(), root.GetProperty("objectGuid").GetString());
        Assert.False(root.GetProperty("accountEnabled").GetBoolean());
        Assert.False(root.GetProperty("passwordSet").GetBoolean());
        Assert.False(root.GetProperty("linked").GetBoolean());
        Assert.Equal(0, sink.UserCreateCalls);
        Assert.Equal("organizationalunit", sink.CreatedWith!.ObjectClass);
    }

    [Fact]
    public async Task An_existing_entry_at_the_DN_is_linked_not_created()
    {
        var sink = new FakeSink { OnCreate = _ => ProvisionResult.LinkedExisting("CN=Granite-Contractors," + Ou) };
        var (ok, msg, json) = await Provision(sink, Payload("CreateGroup", sam: "Granite-Contractors"), "group", "Granite-Contractors");

        Assert.True(ok);
        Assert.StartsWith("Linked existing group", msg);
        Assert.True(JsonDocument.Parse(json!).RootElement.GetProperty("linked").GetBoolean());
    }

    [Fact]
    public async Task A_failed_read_back_degrades_but_the_create_still_succeeds()
    {
        var sink = new FakeSink { ReadBack = null };
        var (ok, msg, json) = await Provision(sink, Payload("CreateContact", displayName: "Vendor Desk"), "contact", null);

        Assert.True(ok);
        Assert.Contains("degraded", msg);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(json!).RootElement.GetProperty("objectGuid").ValueKind);
    }

    [Fact]
    public async Task A_sink_failure_or_throw_is_a_clean_failure()
    {
        var failed = new FakeSink { OnCreate = _ => ProvisionResult.Failed("constraint violation") };
        var (ok, msg, _) = await Provision(failed, Payload("CreateGroup", sam: "g1"), "group", "g1");
        Assert.False(ok);
        Assert.Contains("constraint violation", msg);

        var thrown = new FakeSink { OnCreate = _ => throw new InvalidOperationException("bind refused") };
        var (ok2, msg2, _) = await Provision(thrown, Payload("CreateGroup", sam: "g1"), "group", "g1");
        Assert.False(ok2);
        Assert.Contains("bind refused", msg2);
    }

    // ── the sink's pure add-request builder ───────────────────────────────────────────────────────

    private static ConnectorObject Obj(string cls, params (string Key, object Value)[] attrs)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in attrs) d[k] = v;
        return new ConnectorObject { SourceId = "x", ObjectClass = cls, Attributes = d };
    }

    private static string[] ObjectClasses(AddRequest add)
        => add.Attributes.Cast<DirectoryAttribute>().First(a => a.Name == "objectClass").GetValues(typeof(string)).Cast<string>().ToArray();

    private static string? Attr(AddRequest add, string name)
        => add.Attributes.Cast<DirectoryAttribute>().FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))?.GetValues(typeof(string)).Cast<string>().FirstOrDefault();

    [Fact]
    public void The_group_add_carries_the_group_chain_sam_groupType_and_description()
    {
        var add = ActiveDirectorySink.BuildAddRequest("group", "CN=G1," + Ou, Obj("group", ("description", "d"), ("groupType", "-2147483646")), "G1");
        Assert.Equal(new[] { "top", "group" }, ObjectClasses(add));
        Assert.Equal("G1", Attr(add, "sAMAccountName"));
        Assert.Equal("-2147483646", Attr(add, "groupType"));
        Assert.Equal("d", Attr(add, "description"));
    }

    [Fact]
    public void The_OU_add_carries_the_OU_chain_and_an_OU_rdn()
    {
        var add = ActiveDirectorySink.BuildAddRequest("organizationalunit", "OU=Field Ops," + Ou, Obj("organizationalunit", ("description", "d")), null);
        Assert.Equal(new[] { "top", "organizationalUnit" }, ObjectClasses(add));
        Assert.Null(Attr(add, "sAMAccountName"));
        Assert.Equal("OU=Field Ops", ActiveDirectorySink.BuildRdn("organizationalunit", "Field Ops"));
        Assert.Equal("CN=Vendor\\, Desk", ActiveDirectorySink.BuildRdn("contact", "Vendor, Desk"));
    }

    [Fact]
    public void The_computer_add_is_a_pre_staged_workstation_trust_account()
    {
        var add = ActiveDirectorySink.BuildAddRequest("computer", "CN=GC-LAPTOP-07," + Ou, Obj("computer"), "GC-LAPTOP-07$");
        Assert.Equal(new[] { "top", "person", "organizationalPerson", "user", "computer" }, ObjectClasses(add));
        Assert.Equal("GC-LAPTOP-07$", Attr(add, "sAMAccountName"));
        Assert.Equal((0x1000 | 0x0020).ToString(), Attr(add, "userAccountControl"));
    }

    [Fact]
    public void The_contact_add_carries_the_contact_chain_and_the_person_attributes()
    {
        var add = ActiveDirectorySink.BuildAddRequest("contact", "CN=Vendor Desk," + Ou,
            Obj("contact", ("displayName", "Vendor Desk"), ("givenName", "Vendor"), ("sn", "Desk"), ("mail", "desk@vendor.example")), null);
        Assert.Equal(new[] { "top", "person", "organizationalPerson", "contact" }, ObjectClasses(add));
        Assert.Equal("desk@vendor.example", Attr(add, "mail"));
        Assert.Equal("Desk", Attr(add, "sn"));
        Assert.Null(Attr(add, "userAccountControl"));
    }

    [Fact]
    public void A_user_is_not_this_builders_business()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ActiveDirectorySink.BuildAddRequest("user", "CN=x," + Ou, Obj("user"), "x"));
}
