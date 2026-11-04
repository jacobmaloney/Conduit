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
