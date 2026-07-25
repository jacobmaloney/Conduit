using System.Text.Json;
using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// Tests for <see cref="AdAgentBrowseExecutor"/> — the READ-ONLY BrowseContainers verb. Like the create
/// executor, the concrete repos hit SQL and cannot be mocked, so we exercise the gates that run BEFORE
/// any repository/LDAP call: parse, operation, required fields, well-formedness, and — critically — the
/// deny-all allow-list SCOPE (you can only browse where you could create). The live browse is proven at
/// the pure level (BuildResultJson shape + count cap). Every case asserts it NEVER throws.
/// </summary>
public class AdAgentBrowseExecutorTests
{
    private const string Conn = "corp-local";

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

    private static AdAgentBrowseExecutor Build(params string[] permittedBaseDns)
    {
        var dbConfig = new DatabaseConfig
        {
            ConnectionString = "Server=(local);Database=__none__;Trusted_Connection=False;Connect Timeout=1;"
        };
        return new AdAgentBrowseExecutor(
            System.Array.Empty<IConnectorAdapter>(),
            new SinkConnectionCredentialMapRepository(dbConfig),
            new TenantRepository(dbConfig),
            new FakePolicy(permittedBaseDns),
            NullLogger<AdAgentBrowseExecutor>.Instance);
    }

    private static string Payload(string? baseDn = "OU=Staff,DC=corp,DC=local", string operation = "BrowseContainers", string sourceConn = Conn)
        => "{" +
           "\"schemaVersion\":1," +
           $"\"operation\":\"{operation}\"," +
           $"\"sourceConnectionName\":\"{sourceConn}\"" +
           (baseDn is null ? "" : $",\"baseDn\":\"{baseDn}\"") +
           "}";

    private static Task<(bool Success, string Message, string? ResultJson)> Run(AdAgentBrowseExecutor ex, string? payload)
        => ex.ExecuteAsync(System.Guid.NewGuid(), payload, CancellationToken.None);

    // ── Gates ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Empty_payload_is_rejected(string? payload)
    {
        var (ok, msg, _) = await Run(Build("OU=Staff,DC=corp,DC=local"), payload);
        Assert.False(ok);
        Assert.Contains("empty payload", msg, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CreateAdAccount")]
    [InlineData("browsecontainers")]  // wrong case
    [InlineData("")]
    public async Task Wrong_operation_is_rejected(string op)
    {
        var (ok, msg, _) = await Run(Build("OU=Staff,DC=corp,DC=local"), Payload(operation: op));
        Assert.False(ok);
        Assert.Contains("not allowed", msg, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_baseDn_is_rejected()
    {
        var (ok, msg, _) = await Run(Build("OU=Staff,DC=corp,DC=local"), Payload(baseDn: null));
        Assert.False(ok);
        Assert.Contains("baseDn", msg);
    }

    [Fact]
    public async Task Malformed_baseDn_is_rejected()
    {
        var (ok, msg, _) = await Run(Build("OU=Staff,DC=corp,DC=local"), Payload(baseDn: "not a dn"));
        Assert.False(ok);
        Assert.Contains("well-formed", msg, System.StringComparison.OrdinalIgnoreCase);
    }

    // ── ★ Scope to the permitted base DNs ──────────────────────────────────────

    [Fact]
    public async Task Base_outside_the_allow_list_is_refused()
    {
        var (ok, msg, _) = await Run(Build("OU=Staff,DC=corp,DC=local"), Payload(baseDn: "OU=Admins,DC=corp,DC=local"));
        Assert.False(ok);
        Assert.Contains("not within any permitted", msg, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Empty_allow_list_refuses_every_browse()
    {
        var (ok, msg, _) = await Run(Build(/* none */), Payload());
        Assert.False(ok);
        Assert.Contains("not within any permitted", msg, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Base_within_the_allow_list_passes_the_scope_gate()
    {
        // Contained (equal to a permitted base, or a child) passes the scope gate; the executor then
        // proceeds to credential resolution which fails on the bogus DB — proving it got PAST the scope
        // check (the failure is NOT an allow-list refusal). It still never throws.
        var (ok, msg, _) = await Run(Build("OU=Staff,DC=corp,DC=local"), Payload(baseDn: "OU=Staff,DC=corp,DC=local"));
        Assert.False(ok);
        Assert.DoesNotContain("not within any permitted", msg, System.StringComparison.OrdinalIgnoreCase);

        // A child of a permitted base is also in scope.
        var (ok2, msg2, _) = await Run(Build("OU=Staff,DC=corp,DC=local"), Payload(baseDn: "OU=Team,OU=Staff,DC=corp,DC=local"));
        Assert.False(ok2);
        Assert.DoesNotContain("not within any permitted", msg2, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Executor_never_throws_on_adversarial_inputs()
    {
        var ex = Build("OU=Staff,DC=corp,DC=local");
        var inputs = new[] { null, "", "{", "{}", "[]", "\"s\"", "123", "null", "{\"schemaVersion\":\"x\",\"operation\":\"BrowseContainers\"}" };
        foreach (var input in inputs)
            Assert.Null(await Record.ExceptionAsync(() => Run(ex, input)));
    }

    // ── Pure result-JSON: shape + count cap ────────────────────────────────────

    [Fact]
    public void Result_json_shape_carries_dn_and_name_per_container()
    {
        var nodes = new List<DirectoryContainerNode>
        {
            new() { Name = "Staff", DistinguishedName = "OU=Staff,DC=corp,DC=local", HasChildren = true },
            new() { Name = "Users", DistinguishedName = "CN=Users,DC=corp,DC=local", HasChildren = false }
        };
        var json = AdAgentBrowseExecutor.BuildResultJson("DC=corp,DC=local", nodes);
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("DC=corp,DC=local", root.GetProperty("baseDn").GetString());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        var containers = root.GetProperty("containers");
        Assert.Equal(2, containers.GetArrayLength());
        Assert.Equal("OU=Staff,DC=corp,DC=local", containers[0].GetProperty("distinguishedName").GetString());
        Assert.Equal("Staff", containers[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Result_json_caps_an_oversized_tree()
    {
        var nodes = new List<DirectoryContainerNode>();
        for (var i = 0; i < AdAgentBrowseExecutor.MaxContainers + 50; i++)
            nodes.Add(new DirectoryContainerNode { Name = "n" + i, DistinguishedName = $"OU=n{i},DC=corp,DC=local", HasChildren = false });

        var json = AdAgentBrowseExecutor.BuildResultJson("DC=corp,DC=local", nodes);
        var root = JsonDocument.Parse(json).RootElement;
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(AdAgentBrowseExecutor.MaxContainers, root.GetProperty("containers").GetArrayLength());
    }
}
