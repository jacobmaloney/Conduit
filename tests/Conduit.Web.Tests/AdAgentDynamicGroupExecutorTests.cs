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
        // The changes that did not apply come first (MEMBERSHIP-RULES-01), the applied ones after, each with its outcome and guid.
        Assert.Equal("Failed", changes[0].GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, changes[0].GetProperty("objectGuid").ValueKind);
        Assert.Equal("The object does not exist.", changes[0].GetProperty("error").GetString());
        Assert.Equal("Skipped", changes[1].GetProperty("outcome").GetString());
        Assert.Equal("Add", changes[2].GetProperty("operation").GetString());
        Assert.Equal(GroupGuid, changes[2].GetProperty("objectGuid").GetString());
        Assert.Equal("Applied", changes[2].GetProperty("outcome").GetString());
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
        // DYNAMIC-GROUPS-05: the guard is one method (RefuseWriteAsync) that the command path and the clock both await before their rebuild.
        var guardMethod = executor.IndexOf("private static async Task<string?> RefuseWriteAsync(", StringComparison.Ordinal);
        Assert.True(guardMethod > 0 && executor.IndexOf("AdAgentWriteExecutor.IsPrivilegedGroup(target, out var why)", StringComparison.Ordinal) > guardMethod, "the privileged-group rule lives in the one guard");
        var commandGuard = executor.IndexOf("var refusal = await RefuseWriteAsync(adapter!, resolvedTenantId.Value, groupGuid, ct);", StringComparison.Ordinal);
        var commandRebuild = executor.IndexOf("OpRebuild => await evaluator.RebuildAsync(groupGuid.ToString(), ct)", StringComparison.Ordinal);
        Assert.True(commandGuard > 0 && commandRebuild > commandGuard, "the command's guard runs before its rebuild");
        var clockGuard = executor.IndexOf("var refusal = await RefuseWriteAsync(adapter!, tenantId, groupGuid, ct);", StringComparison.Ordinal);
        var clockRebuild = executor.IndexOf("return (await evaluator.RebuildAsync(groupGuid.ToString(), ct), null);", StringComparison.Ordinal);
        Assert.True(clockGuard > 0 && clockRebuild > clockGuard, "the clock's guard runs before its rebuild");
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
    [InlineData("[{\"type\":9,\"objectGuid\":\"0786a4f0-5555-4555-8555-000000000005\"}]", "seven membership rule types")]
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
        Assert.Equal(0, root.GetProperty("ineligibleCount").GetInt32());

        // MEMBERSHIP-RULES-01 (UC034-F12): what the group cannot hold is counted and said.
        var left = new DynamicGroupEvaluation { GroupName = "dynamo", IsDynamic = true, Ineligible = 63, Rules = Evaluation().Rules, CurrentMembers = Evaluation().CurrentMembers, DesiredMembers = Evaluation().DesiredMembers };
        Assert.EndsWith(" 63 object(s) the group cannot hold were left out.", AdAgentDynamicGroupExecutor.Describe(left));
        using var leftDoc = JsonDocument.Parse(AdAgentDynamicGroupExecutor.BuildResultJson(left));
        Assert.Equal(63, leftDoc.RootElement.GetProperty("ineligibleCount").GetInt32());
    }

    [Fact]
    public void The_lists_are_capped_and_say_so_while_the_counts_stay_exact()
    {
        using var doc = JsonDocument.Parse(AdAgentDynamicGroupExecutor.BuildResultJson(Evaluation(AdAgentDynamicGroupExecutor.MaxListed + 5)));
        var root = doc.RootElement;
        Assert.Equal(AdAgentDynamicGroupExecutor.MaxListed + 5, root.GetProperty("currentCount").GetInt32());
        Assert.InRange(root.GetProperty("current").GetArrayLength(), 1, AdAgentDynamicGroupExecutor.MaxListed);   // at most MaxListed, and fewer when the bytes say so (DYNAMIC-GROUPS-08)
        Assert.True(root.GetProperty("listsTruncated").GetBoolean());
    }

    // DYNAMIC-GROUPS-08: IdentityCenter refuses a structured result over 64 KB, and a refused completion left the
    // command Acked for good (the lab's 217-member group, 2026-11-04). The result is sized by bytes, not entries.
    [Fact]
    public void The_result_fits_under_the_wire_cap_by_bytes_and_the_counts_stay_exact()
    {
        var e = Evaluation(AdAgentDynamicGroupExecutor.MaxListed);   // four lists of 2,000 names: well over the cap at MaxListed
        var json = AdAgentDynamicGroupExecutor.BuildResultJson(e);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= AdAgentDynamicGroupExecutor.MaxResultBytes, "the result exceeds the wire cap");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(AdAgentDynamicGroupExecutor.MaxListed, root.GetProperty("currentCount").GetInt32());
        Assert.Equal(AdAgentDynamicGroupExecutor.MaxListed, root.GetProperty("desiredCount").GetInt32());
        Assert.InRange(root.GetProperty("current").GetArrayLength(), 1, AdAgentDynamicGroupExecutor.MaxListed - 1);
        Assert.True(root.GetProperty("listsTruncated").GetBoolean());
    }

    [Fact]
    public void A_large_rebuilds_change_list_fits_under_the_wire_cap_and_says_so()
    {
        var changes = Enumerable.Range(0, 3000).Select(i => new DynamicGroupChange
        {
            Operation = "Add",
            DistinguishedName = $"CN=Member {i} with a long common name,OU=Somewhere,OU=Organization,DC=Domain,DC=Local",
            ObjectGuid = System.Guid.NewGuid().ToString(),
            Outcome = "Applied",
        }).ToList();
        var big = new DynamicGroupRebuild { Evaluation = Evaluation(), Changes = changes, AppliedAdds = changes.Count };
        var json = AdAgentDynamicGroupExecutor.BuildRebuildResultJson(big);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= AdAgentDynamicGroupExecutor.MaxResultBytes, "the rebuild result exceeds the wire cap");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(3000, root.GetProperty("changeCount").GetInt32());
        Assert.Equal(3000, root.GetProperty("appliedAdds").GetInt32());
        Assert.InRange(root.GetProperty("changes").GetArrayLength(), 1, 2999);
        Assert.True(root.GetProperty("changesTruncated").GetBoolean());

        // The failures come first, so a shrunk list still carries every reason (the lab's rebuild lost 63 of them to the cap).
        var mixed = changes.Take(2990).Concat(Enumerable.Range(0, 10).Select(i => new DynamicGroupChange { Operation = "Add", DistinguishedName = $"CN=Universal {i},OU=Groups,DC=Domain,DC=Local", Outcome = "Failed", Error = "A global group cannot have a universal group as a member." })).ToList();
        using var mixedDoc = JsonDocument.Parse(AdAgentDynamicGroupExecutor.BuildRebuildResultJson(new DynamicGroupRebuild { Evaluation = Evaluation(), Changes = mixed, AppliedAdds = 2990, Failed = 10 }));
        var listed = mixedDoc.RootElement.GetProperty("changes").EnumerateArray().ToList();
        Assert.True(listed.Count < 3000);
        Assert.Equal(10, listed.Count(c => c.GetProperty("outcome").GetString() == "Failed"));
        Assert.All(listed.Take(10), c => Assert.Equal("Failed", c.GetProperty("outcome").GetString()));

        var small = new DynamicGroupRebuild { Evaluation = Evaluation(), Changes = changes.Take(3).ToList(), AppliedAdds = 3 };
        using var smallDoc = JsonDocument.Parse(AdAgentDynamicGroupExecutor.BuildRebuildResultJson(small));
        Assert.Equal(3, smallDoc.RootElement.GetProperty("changes").GetArrayLength());
        Assert.False(smallDoc.RootElement.GetProperty("changesTruncated").GetBoolean());
    }

    [Fact]
    public void A_refused_completion_is_said_at_warning_and_reported_again_without_the_structured_result()
    {
        var poller = File.ReadAllText(Path.Combine(TestsDirectory(), "..", "..", "src", "Conduit.Web", "Services", "IcAgentCommandPollerService.cs"));
        Assert.Contains("complete callback refused (HTTP {Status}: {Reason})", poller);
        Assert.Contains("if (status == 400 && resultJson is not null)", poller);
        Assert.Contains("JsonSerializer.Serialize(new { success, message }), ct);", poller);
        Assert.DoesNotContain("complete callback failed (run outcome", poller);
    }

    private static string TestsDirectory([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;

    // ── MEMBERSHIP-RULES-01 (UC034-D13): the preview verb ─────────────────────

    private static Task<(bool Success, string Message, string? ResultJson)> RunPreview(string? payload)
        => Build().ExecuteAsync(System.Guid.NewGuid(), payload, CancellationToken.None, AdAgentDynamicGroupExecutor.OpPreview);

    private static string PreviewPayload(string ruleJson)
        => "{\"schemaVersion\":1,\"operation\":\"PreviewDynamicGroupRule\",\"sourceConnectionName\":\"" + Conn + "\",\"objectGuid\":\"" + GroupGuid + "\",\"rule\":" + ruleJson + "}";

    [Fact]
    public async Task A_preview_without_its_rule_is_refused()
    {
        var (ok, msg, _) = await RunPreview(Payload(operation: "PreviewDynamicGroupRule"));
        Assert.False(ok);
        Assert.StartsWith("PreviewDynamicGroupRule:", msg);
        Assert.Contains("rule is missing", msg);
    }

    [Theory]
    [InlineData("{\"type\":0,\"objectGuid\":\"0786a4f0-5555-4555-8555-000000000005\"}", "seven membership rule types")]
    [InlineData("{\"type\":5,\"objectGuid\":\"nope\"}", "names no objectGUID")]
    [InlineData("{\"type\":2,\"objectGuid\":\"0786a4f0-5555-4555-8555-000000000005\"}", "needs an LDAP filter")]
    public async Task A_preview_rule_is_checked_like_a_written_one(string rule, string reason)
    {
        var (ok, msg, _) = await RunPreview(PreviewPayload(rule));
        Assert.False(ok);
        Assert.Contains(reason, msg);
    }

    [Fact]
    public async Task A_preview_under_another_operations_name_is_refused()
    {
        var (ok, msg, _) = await RunPreview(PreviewPayload("{\"type\":7,\"objectGuid\":\"0786a4f0-5555-4555-8555-000000000005\"}").Replace("PreviewDynamicGroupRule", "EvaluateDynamicGroup"));
        Assert.False(ok);
        Assert.Contains("not allowed", msg);
    }

    [Fact]
    public async Task A_sound_preview_reaches_the_tenant_lookup()
    {
        // Past every payload gate; the harness has no database, so the tenant lookup is where it stops.
        var (ok, msg, _) = await RunPreview(PreviewPayload("{\"type\":7,\"objectGuid\":\"0786a4f0-5555-4555-8555-000000000005\"}"));
        Assert.False(ok);
        Assert.StartsWith("PreviewDynamicGroupRule:", msg);
        Assert.DoesNotContain("rule is missing", msg);
        Assert.DoesNotContain("a rule's type", msg);
        Assert.DoesNotContain("names no objectGUID", msg);
        Assert.DoesNotContain("needs an LDAP filter", msg);
    }

    [Fact]
    public void The_preview_result_carries_the_exact_count_and_a_sample_under_the_wire_cap()
    {
        var names = Enumerable.Range(0, 5000).Select(i => $"CN=Member {i} with a long common name,OU=Somewhere,OU=Organization,DC=Domain,DC=Local").ToList();
        var preview = new DynamicGroupRulePreview { TypeCode = 1, Type = "Include by Query", ObjectDistinguishedName = "OU=Organization,DC=Domain,DC=Local", MatchCount = 7000, Truncated = false, Sample = names };
        var json = AdAgentDynamicGroupExecutor.BuildPreviewResultJson(preview);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= AdAgentDynamicGroupExecutor.MaxResultBytes);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(7000, root.GetProperty("matchCount").GetInt32());
        Assert.InRange(root.GetProperty("sample").GetArrayLength(), 1, 4999);
        Assert.True(root.GetProperty("sampleTruncated").GetBoolean());
        Assert.Equal("'Include by Query' matches 7,000 objects.", AdAgentDynamicGroupExecutor.DescribePreview(preview));
        Assert.Equal("Retain Deprovisioned keeps deprovisioned members in place; it matches nothing of its own.",
            AdAgentDynamicGroupExecutor.DescribePreview(new DynamicGroupRulePreview { TypeCode = 7, Type = "Retain Deprovisioned" }));

        var small = new DynamicGroupRulePreview { TypeCode = 3, Type = "Include Explicitly", MatchCount = 1, Sample = new[] { "CN=One,OU=X" } };
        using var smallDoc = JsonDocument.Parse(AdAgentDynamicGroupExecutor.BuildPreviewResultJson(small));
        Assert.False(smallDoc.RootElement.GetProperty("sampleTruncated").GetBoolean());
        Assert.Equal("'Include Explicitly' matches 1 object.", AdAgentDynamicGroupExecutor.DescribePreview(small));
    }

    [Fact]
    public void The_poller_dispatches_the_preview_verb()
    {
        var poller = File.ReadAllText(Path.Combine(TestsDirectory(), "..", "..", "src", "Conduit.Web", "Services", "IcAgentCommandPollerService.cs"));
        Assert.Contains("CommandPreviewDynamicGroupRule = \"PreviewDynamicGroupRule\"", poller);
        Assert.Contains("await DynamicGroupAsync(command.Id, command.PayloadJson, CommandPreviewDynamicGroupRule, ct)", poller);
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

    // ── DYNAMIC-GROUPS-05: the clock's view of the same executor ─────────────

    [Fact]
    public async Task The_clocks_reads_and_rebuild_fail_closed_without_an_adapter_and_never_throw()
    {
        IDynamicGroupClockWork work = Build();
        var tenant = System.Guid.NewGuid();

        var (groups, error) = await work.ListDynamicGroupsAsync(tenant, CancellationToken.None);
        Assert.Null(groups);
        Assert.Equal("Active Directory connector is not available on this agent.", error);

        Assert.Null(await work.AnyDirectoryChangeSinceAsync(tenant, System.DateTime.UtcNow.AddMinutes(-5), CancellationToken.None));

        var (rebuild, refusal) = await work.RebuildOnClockAsync(tenant, System.Guid.Parse(GroupGuid), CancellationToken.None);
        Assert.Null(rebuild);
        Assert.Equal("Active Directory connector is not available on this agent.", refusal);
    }
}
