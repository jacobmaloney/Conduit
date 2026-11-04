using System.IO;
using System.Text.Json;
using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// DYNAMIC-GROUPS-01 (UC034-D12): the READ-ONLY EvaluateDynamicGroup verb. As for the browse verb, the concrete repos hit
/// SQL and cannot be mocked, so the gates that run before any repository or LDAP call are exercised (parse, operation,
/// required fields, the GUID), the result shape at the pure level, and that nothing throws. The connector's own
/// evaluator is proven on the lab (UC034-S01); here the poller contract.
/// </summary>
public class AdAgentDynamicGroupExecutorTests
{
    private const string Conn = "corp-local";
    private const string GroupGuid = "0786a4f0-5555-4555-8555-000000000005";

    private static AdAgentDynamicGroupExecutor Build()
    {
        var dbConfig = new DatabaseConfig
        {
            ConnectionString = "Server=(local);Database=__none__;Trusted_Connection=False;Connect Timeout=1;"
        };
        return new AdAgentDynamicGroupExecutor(
            System.Array.Empty<IConnectorAdapter>(),
            new SinkConnectionCredentialMapRepository(dbConfig),
            new TenantRepository(dbConfig),
            NullLogger<AdAgentDynamicGroupExecutor>.Instance);
    }

    private static string Payload(string? objectGuid = GroupGuid, string operation = "EvaluateDynamicGroup", string? sourceConn = Conn)
        => "{" +
           "\"schemaVersion\":1," +
           $"\"operation\":\"{operation}\"" +
           (sourceConn is null ? "" : $",\"sourceConnectionName\":\"{sourceConn}\"") +
           (objectGuid is null ? "" : $",\"objectGuid\":\"{objectGuid}\"") +
           "}";

    private static Task<(bool Success, string Message, string? ResultJson)> Run(string? payload)
        => Build().ExecuteAsync(System.Guid.NewGuid(), payload, CancellationToken.None);

    private static Task<(bool Success, string Message, string? ResultJson)> RunRebuild(string? payload)
        => Build().ExecuteAsync(System.Guid.NewGuid(), payload, CancellationToken.None, AdAgentDynamicGroupExecutor.OpRebuild);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Empty_payload_is_rejected(string? payload)
    {
        var (ok, msg, json) = await Run(payload);
        Assert.False(ok);
        Assert.Contains("empty payload", msg, System.StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(json);
    }

    [Fact]
    public async Task Malformed_json_is_rejected()
    {
        var (ok, msg, _) = await Run("{not json");
        Assert.False(ok);
        Assert.Contains("malformed", msg, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unsupported_schema_version_is_rejected()
    {
        var (ok, msg, _) = await Run(Payload().Replace("\"schemaVersion\":1", "\"schemaVersion\":2"));
        Assert.False(ok);
        Assert.Contains("schemaVersion", msg);
    }

    [Theory]
    [InlineData("ApplyObjectWrite")]
    [InlineData("RebuildDynamicGroup")]   // the write verb of a later slice: not this one
    [InlineData("evaluatedynamicgroup")]  // wrong case
    [InlineData("")]
    public async Task Wrong_operation_is_rejected(string op)
    {
        var (ok, msg, _) = await Run(Payload(operation: op));
        Assert.False(ok);
        Assert.Contains("not allowed", msg, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_connection_name_is_rejected()
    {
        var (ok, msg, _) = await Run(Payload(sourceConn: null));
        Assert.False(ok);
        Assert.Contains("sourceConnectionName", msg);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CN=Group,OU=X")]   // a DN is not the address: the group is re-read by GUID
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_missing_or_invalid_group_guid_is_rejected(string? guid)
    {
        var (ok, msg, _) = await Run(Payload(objectGuid: guid));
        Assert.False(ok);
        Assert.Contains("objectGuid", msg);
    }

    [Fact]
    public async Task A_well_formed_request_without_a_mapping_fails_closed_and_never_throws()
    {
        // No adapters and an unreachable database: the mapping lookup fails; the verb reports it rather than throwing.
        var (ok, msg, json) = await Run(Payload());
        Assert.False(ok);
        Assert.StartsWith("EvaluateDynamicGroup:", msg);
        Assert.NotNull(json);
    }

    // ── DYNAMIC-GROUPS-03: the rebuild on the same executor ─────────────────

    [Fact]
    public async Task A_rebuild_payload_on_an_evaluate_command_is_refused_and_the_reverse()
    {
        var (ok, msg, _) = await Run(Payload(operation: "RebuildDynamicGroup"));
        Assert.False(ok);
        Assert.StartsWith("EvaluateDynamicGroup:", msg);
        Assert.Contains("not allowed", msg);

        var (ok2, msg2, _) = await RunRebuild(Payload(operation: "EvaluateDynamicGroup"));
        Assert.False(ok2);
        Assert.StartsWith("RebuildDynamicGroup:", msg2);
        Assert.Contains("not allowed", msg2);
    }

    [Fact]
    public async Task A_well_formed_rebuild_without_a_mapping_fails_closed_and_never_throws()
    {
        var (ok, msg, json) = await RunRebuild(Payload(operation: "RebuildDynamicGroup"));
        Assert.False(ok);
        Assert.StartsWith("RebuildDynamicGroup:", msg);
        Assert.NotNull(json);
    }

    [Fact]
    public void The_rebuild_result_carries_every_change_with_its_outcome_and_guid()
    {
        var rebuild = new DynamicGroupRebuild
        {
            Evaluation = Evaluation(),
            Changes = new[]
            {
                new DynamicGroupChange { Operation = "Add", DistinguishedName = "CN=A,OU=X", ObjectGuid = GroupGuid, Outcome = "Applied" },
                new DynamicGroupChange { Operation = "Remove", DistinguishedName = "CN=Native,OU=X", ObjectGuid = null, Outcome = "Failed", Error = "The object does not exist." },
                new DynamicGroupChange { Operation = "Add", DistinguishedName = "CN=B,OU=X", ObjectGuid = GroupGuid, Outcome = "Skipped" },
            },
            AppliedAdds = 1, AppliedRemoves = 0, Skipped = 1, Failed = 1,
        };

        using var doc = JsonDocument.Parse(AdAgentDynamicGroupExecutor.BuildRebuildResultJson(rebuild));
        var root = doc.RootElement;
        Assert.Equal("RebuildDynamicGroup", root.GetProperty("operation").GetString());
        Assert.Equal("dynamo", root.GetProperty("groupName").GetString());
        var changes = root.GetProperty("changes");
        Assert.Equal(3, changes.GetArrayLength());
        Assert.Equal("Add", changes[0].GetProperty("operation").GetString());
        Assert.Equal(GroupGuid, changes[0].GetProperty("objectGuid").GetString());
        Assert.Equal("Applied", changes[0].GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, changes[1].GetProperty("objectGuid").ValueKind);
        Assert.Equal("The object does not exist.", changes[1].GetProperty("error").GetString());
        Assert.Equal(1, root.GetProperty("appliedAdds").GetInt32());
        Assert.Equal(1, root.GetProperty("failed").GetInt32());
        Assert.Equal(3, root.GetProperty("desiredCount").GetInt32());

        Assert.Equal("'dynamo' rebuilt: 1 added, 0 removed of 3 by the rules. 1 change(s) failed. 1 already so.", AdAgentDynamicGroupExecutor.DescribeRebuild(rebuild));
    }

    [Fact]
    public void The_rebuild_writes_through_the_sinks_one_member_modify_and_refuses_the_privileged_groups_by_the_write_rule()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", ".."));
        string Read(string relative) => File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

        var sink = Read("src/Conduit.Connectors.ActiveDirectory/ActiveDirectorySink.cs");
        Assert.Contains("public static SinkWriteResult ModifyMember(LdapConnection conn, string groupDn, string memberDn, DirectoryAttributeOperation op)", sink);
        Assert.Contains("return ModifyMember(conn, group.DistinguishedName, member.DistinguishedName, op);", sink);   // the GUID verbs on it
        Assert.Contains("conn.SendRequest(new ModifyRequest(groupDn, mod));", sink);                           // the one member modify
        Assert.DoesNotContain("conn.SendRequest(new ModifyRequest(group.DistinguishedName, mod));", sink);    // the GUID path's own copy is gone

        var evaluator = Read("src/Conduit.Connectors.ActiveDirectory/ActiveDirectoryDynamicGroupEvaluator.cs");
        Assert.Contains("ActiveDirectorySink.ModifyMember(connection, groupDn, dn, add ? DirectoryAttributeOperation.Add : DirectoryAttributeOperation.Delete)", evaluator);
        Assert.DoesNotContain("Name = \"member\"", evaluator);   // no second member write: the definition and the Notes are the only modifies it builds itself
        Assert.Contains("the rebuild was not run on a partial set", evaluator);

        var executor = Read("src/Conduit.Web/Services/AdAgentDynamicGroupExecutor.cs");
        var guard = executor.IndexOf("AdAgentWriteExecutor.IsPrivilegedGroup(target, out var why)", StringComparison.Ordinal);
        var rebuild = executor.IndexOf("await evaluator.RebuildAsync(groupGuid.ToString(), ct)", StringComparison.Ordinal);
        Assert.True(guard > 0 && rebuild > guard, "the privileged-group guard runs before the rebuild");
        Assert.Contains("internal static bool IsPrivilegedGroup(AdResolvedObject group, out string reason)", Read("src/Conduit.Web/Services/AdAgentWriteExecutor.cs"));

        var poller = Read("src/Conduit.Web/Services/IcAgentCommandPollerService.cs");
        Assert.Contains("CommandRebuildDynamicGroup = \"RebuildDynamicGroup\"", poller);
        Assert.Contains("DynamicGroupAsync(command.Id, command.PayloadJson, CommandRebuildDynamicGroup, ct)", poller);
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    // ── DYNAMIC-GROUPS-04: the rules write and the conversion back ───────────

    private static Task<(bool Success, string Message, string? ResultJson)> RunSetRules(string? payload)
        => Build().ExecuteAsync(System.Guid.NewGuid(), payload, CancellationToken.None, AdAgentDynamicGroupExecutor.OpSetRules);

    private static string RulesPayload(string rulesJson, string operation = "SetDynamicGroupRules")
        => "{\"schemaVersion\":1,\"operation\":\"" + operation + "\",\"sourceConnectionName\":\"" + Conn + "\",\"objectGuid\":\"" + GroupGuid + "\",\"rules\":" + rulesJson + ",\"clearMembers\":true,\"notes\":\"Managed by rules\"}";

    [Theory]
    [InlineData("[{\"type\":7,\"objectGuid\":\"0786a4f0-5555-4555-8555-000000000005\"}]", "six membership rule types")]
    [InlineData("[{\"type\":3,\"objectGuid\":\"not-a-guid\"}]", "names no objectGUID")]
    [InlineData("[{\"type\":1,\"objectGuid\":\"0786a4f0-5555-4555-8555-000000000005\"}]", "needs an LDAP filter")]
    public async Task A_rule_that_cannot_be_written_is_refused_before_any_directory_call(string rules, string reason)
    {
        var (ok, msg, _) = await RunSetRules(RulesPayload(rules));
        Assert.False(ok);
        Assert.StartsWith("SetDynamicGroupRules:", msg);
        Assert.Contains(reason, msg);
    }

    [Fact]
    public async Task A_rules_write_without_a_rule_list_or_with_too_many_is_refused()
    {
        var (ok, msg, _) = await RunSetRules(Payload(operation: "SetDynamicGroupRules"));
        Assert.False(ok);
        Assert.Contains("rules are missing", msg);

        var many = "[" + string.Join(",", Enumerable.Repeat("{\"type\":3,\"objectGuid\":\"" + GroupGuid + "\"}", AdAgentDynamicGroupExecutor.MaxRules + 1)) + "]";
        var (ok2, msg2, _) = await RunSetRules(RulesPayload(many));
        Assert.False(ok2);
        Assert.Contains("too many rules", msg2);
    }

    [Fact]
    public async Task A_sound_rules_write_without_a_mapping_fails_closed_and_the_convert_back_too()
    {
        var (ok, msg, json) = await RunSetRules(RulesPayload("[{\"type\":1,\"objectGuid\":\"" + GroupGuid + "\",\"filter\":\"(objectCategory=person)\"},{\"type\":3,\"objectGuid\":\"" + GroupGuid + "\"}]"));
        Assert.False(ok);
        Assert.StartsWith("SetDynamicGroupRules:", msg);   // the rules passed every gate; the unreachable mapping store is the refusal
        Assert.NotNull(json);

        var (ok2, msg2, _) = await Build().ExecuteAsync(System.Guid.NewGuid(), Payload(operation: "ConvertDynamicGroupToStatic"), CancellationToken.None, AdAgentDynamicGroupExecutor.OpConvertToStatic);
        Assert.False(ok2);
        Assert.StartsWith("ConvertDynamicGroupToStatic:", msg2);
    }

    [Fact]
    public void The_write_result_and_message_say_what_a_conversion_did()
    {
        var write = new DynamicGroupRebuild
        {
            Evaluation = new DynamicGroupEvaluation { GroupName = "Sales", IsDynamic = true },
            Changes = new[] { new DynamicGroupChange { Operation = "Remove", DistinguishedName = "CN=A,OU=X", ObjectGuid = GroupGuid, Outcome = "Applied" } },
            AppliedRemoves = 1, RulesWritten = 2, Converted = true,
        };
        using var doc = JsonDocument.Parse(AdAgentDynamicGroupExecutor.BuildRebuildResultJson(write, AdAgentDynamicGroupExecutor.OpSetRules));
        Assert.Equal("SetDynamicGroupRules", doc.RootElement.GetProperty("operation").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("rulesWritten").GetInt32());
        Assert.True(doc.RootElement.GetProperty("converted").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("changes").GetArrayLength());
        Assert.Equal("'Sales': 2 rule(s) written; converted to a dynamic group, 1 member(s) removed.", AdAgentDynamicGroupExecutor.DescribeWrite(AdAgentDynamicGroupExecutor.OpSetRules, write));
        Assert.Equal("'Sales': converted to a static group; its members stay as last computed.", AdAgentDynamicGroupExecutor.DescribeWrite(AdAgentDynamicGroupExecutor.OpConvertToStatic, write));

        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", ".."));
        var evaluator = File.ReadAllText(Path.Combine(root, "src", "Conduit.Connectors.ActiveDirectory", "ActiveDirectoryDynamicGroupEvaluator.cs"));
        var members = evaluator.IndexOf("ActiveDirectorySink.ModifyMember(connection, groupDn, dn, DirectoryAttributeOperation.Delete)", StringComparison.Ordinal);
        var definition = evaluator.IndexOf("definition.Add(DynamicGroupRules.Serialize(rules, DynamicGroupRules.IdentityCenterService));", StringComparison.Ordinal);
        Assert.True(members > 0 && definition > members, "on a conversion the members go first, then the definition and the Notes");
        Assert.Contains("Operation = DirectoryAttributeOperation.Delete", evaluator);   // the conversion back removes the attribute
        var poller = File.ReadAllText(Path.Combine(root, "src", "Conduit.Web", "Services", "IcAgentCommandPollerService.cs"));
        Assert.Contains("CommandSetDynamicGroupRules = \"SetDynamicGroupRules\"", poller);
        Assert.Contains("CommandConvertDynamicGroupToStatic = \"ConvertDynamicGroupToStatic\"", poller);
    }

    // ── The result shape, at the pure level ──────────────────────────────────

    private static DynamicGroupEvaluation Evaluation(int listed = 3) => new()
    {
        GroupDistinguishedName = "CN=dynamo,OU=DynamicGroups,OU=Organization,DC=Domain,DC=Local",
        GroupName = "dynamo",
        IsDynamic = true,
        OriginatingService = "06b43ba8-b20f-486b-9ce5-1d90101e6d34",
        Rules = new[]
        {
            new DynamicGroupRuleReport { TypeCode = 1, Type = "Include by Query", ObjectGuid = GroupGuid, ObjectDistinguishedName = "OU=Organization,DC=Domain,DC=Local", Filter = "(objectCategory=person)", MatchCount = listed },
            new DynamicGroupRuleReport { TypeCode = 3, Type = "Include Explicitly", ObjectGuid = GroupGuid, Error = "The rule's object was not found in the directory." },
        },
        CurrentMembers = Enumerable.Range(0, listed).Select(i => $"CN=Current{i},OU=X").ToList(),
        DesiredMembers = Enumerable.Range(0, listed).Select(i => $"CN=Desired{i},OU=X").ToList(),
        Adds = Enumerable.Range(0, listed).Select(i => $"CN=Desired{i},OU=X").ToList(),
        Removes = Enumerable.Range(0, listed).Select(i => $"CN=Current{i},OU=X").ToList(),
    };

    [Fact]
    public void The_result_carries_the_rules_as_resolved_and_the_four_lists_with_exact_counts()
    {
        using var doc = JsonDocument.Parse(AdAgentDynamicGroupExecutor.BuildResultJson(Evaluation()));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("dynamo", root.GetProperty("groupName").GetString());
        Assert.True(root.GetProperty("isDynamic").GetBoolean());
        Assert.Equal("06b43ba8-b20f-486b-9ce5-1d90101e6d34", root.GetProperty("originatingService").GetString());
        var rules = root.GetProperty("rules");
        Assert.Equal(2, rules.GetArrayLength());
        Assert.Equal("Include by Query", rules[0].GetProperty("type").GetString());
        Assert.Equal("(objectCategory=person)", rules[0].GetProperty("filter").GetString());
        Assert.Equal(3, rules[0].GetProperty("matchCount").GetInt32());
        Assert.Equal("The rule's object was not found in the directory.", rules[1].GetProperty("error").GetString());
        Assert.Equal(3, root.GetProperty("currentCount").GetInt32());
        Assert.Equal(3, root.GetProperty("addCount").GetInt32());
        Assert.Equal(3, root.GetProperty("removes").GetArrayLength());
        Assert.False(root.GetProperty("listsTruncated").GetBoolean());
        Assert.False(root.GetProperty("searchTruncated").GetBoolean());
    }

    [Fact]
    public void The_lists_are_capped_and_say_so_while_the_counts_stay_exact()
    {
        using var doc = JsonDocument.Parse(AdAgentDynamicGroupExecutor.BuildResultJson(Evaluation(AdAgentDynamicGroupExecutor.MaxListed + 5)));
        var root = doc.RootElement;
        Assert.Equal(AdAgentDynamicGroupExecutor.MaxListed + 5, root.GetProperty("currentCount").GetInt32());
        Assert.Equal(AdAgentDynamicGroupExecutor.MaxListed, root.GetProperty("current").GetArrayLength());
        Assert.True(root.GetProperty("listsTruncated").GetBoolean());
    }

    [Fact]
    public void The_message_counts_and_never_names()
    {
        var message = AdAgentDynamicGroupExecutor.Describe(Evaluation());
        Assert.Equal("'dynamo': 2 rule(s); 3 current, 3 desired; +3 / -3. 1 rule(s) could not be evaluated.", message);
        Assert.DoesNotContain("CN=", message);

        var stillStatic = AdAgentDynamicGroupExecutor.Describe(new DynamicGroupEvaluation { GroupName = "Sales", IsDynamic = false, CurrentMembers = new[] { "CN=A" } });
        Assert.Equal("'Sales' carries no membership rules (a static group with 1 member(s)).", stillStatic);
    }
}
