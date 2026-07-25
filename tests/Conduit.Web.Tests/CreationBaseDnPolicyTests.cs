using Conduit.DataAccess.Repositories;
using Conduit.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// Tests for the base-DN SOURCE resolution: DB-authoritative → config fallback → deny-all, and
/// FAIL-CLOSED on any DB read error. The matching logic (BaseDnContainment) is unchanged and tested
/// separately; this only proves what permitted list feeds it.
/// </summary>
public class CreationBaseDnPolicyTests
{
    private const string Conn = "corp-local";

    private sealed class FakeRepo : ICreationBaseDnAllowListRepository
    {
        public Func<string, Task<List<string>>> GetBaseDns = _ => Task.FromResult(new List<string>());
        public Task<List<string>> GetBaseDnsAsync(string sourceConnectionName) => GetBaseDns(sourceConnectionName);
        public Task<List<CreationBaseDnEntry>> GetAllAsync() => Task.FromResult(new List<CreationBaseDnEntry>());
        public Task<bool> AddAsync(string n, string d, string? by) => Task.FromResult(true);
        public Task<bool> DeleteAsync(Guid id) => Task.FromResult(true);
        public Task<CreationBaseDnEntry?> GetByIdAsync(Guid id) => Task.FromResult<CreationBaseDnEntry?>(null);
    }

    private static IConfiguration Config(params string[] configBaseDns)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < configBaseDns.Length; i++)
            dict[$"AdProvisioning:CreationBaseDns:{Conn}:{i}"] = configBaseDns[i];
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static CreationBaseDnPolicy Policy(FakeRepo repo, IConfiguration config) =>
        new(repo, config, NullLogger<CreationBaseDnPolicy>.Instance);

    [Fact]
    public async Task Db_entries_are_authoritative()
    {
        var repo = new FakeRepo { GetBaseDns = _ => Task.FromResult(new List<string> { "OU=Staff,DC=corp,DC=local" }) };
        // Config has a DIFFERENT value; the DB must win.
        var result = await Policy(repo, Config("OU=Other,DC=corp,DC=local")).GetPermittedBaseDnsAsync(Conn);
        Assert.Equal(new[] { "OU=Staff,DC=corp,DC=local" }, result);
    }

    [Fact]
    public async Task Db_read_exception_denies_all_fail_closed()
    {
        // A DB failure must NOT fall open to config or "no restriction".
        var repo = new FakeRepo { GetBaseDns = _ => throw new InvalidOperationException("db down") };
        var result = await Policy(repo, Config("OU=Staff,DC=corp,DC=local")).GetPermittedBaseDnsAsync(Conn);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Db_empty_falls_back_to_config()
    {
        var repo = new FakeRepo { GetBaseDns = _ => Task.FromResult(new List<string>()) };
        var result = await Policy(repo, Config("OU=Staff,DC=corp,DC=local")).GetPermittedBaseDnsAsync(Conn);
        Assert.Equal(new[] { "OU=Staff,DC=corp,DC=local" }, result);
    }

    [Fact]
    public async Task Both_empty_denies_all()
    {
        var repo = new FakeRepo { GetBaseDns = _ => Task.FromResult(new List<string>()) };
        var result = await Policy(repo, Config(/* no config */)).GetPermittedBaseDnsAsync(Conn);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_connection_name_denies_all(string? name)
    {
        var repo = new FakeRepo { GetBaseDns = _ => Task.FromResult(new List<string> { "OU=Staff,DC=corp,DC=local" }) };
        var result = await Policy(repo, Config("OU=Staff,DC=corp,DC=local")).GetPermittedBaseDnsAsync(name!);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Db_backed_list_feeds_containment_end_to_end()
    {
        var repo = new FakeRepo { GetBaseDns = _ => Task.FromResult(new List<string> { "OU=Users,DC=corp,DC=local" }) };
        var permitted = await Policy(repo, Config()).GetPermittedBaseDnsAsync(Conn);
        Assert.True(BaseDnContainment.IsContained("OU=Staff,OU=Users,DC=corp,DC=local", permitted));   // contained
        Assert.False(BaseDnContainment.IsContained("OU=Admins,DC=corp,DC=local", permitted));          // outside
    }

    // ── Display-vs-enforcement parity (the settings-page honesty fix) ──

    [Fact]
    public async Task Effective_source_is_database_when_db_has_entries()
    {
        var repo = new FakeRepo { GetBaseDns = _ => Task.FromResult(new List<string> { "OU=Staff,DC=corp,DC=local" }) };
        var (enforced, source) = await Policy(repo, Config("OU=Other,DC=corp,DC=local")).GetEffectiveAsync(Conn);
        Assert.Equal(BaseDnSource.Database, source);
        Assert.Equal(new[] { "OU=Staff,DC=corp,DC=local" }, enforced);
    }

    [Fact]
    public async Task Empty_db_with_config_surfaces_as_config_not_denied()
    {
        // The bug: this used to display "denied" while creates were permitted by config.
        var repo = new FakeRepo { GetBaseDns = _ => Task.FromResult(new List<string>()) };
        var (enforced, source) = await Policy(repo, Config("OU=Staff,DC=corp,DC=local")).GetEffectiveAsync(Conn);
        Assert.Equal(BaseDnSource.Config, source);   // surfaced, NOT None/denied
        Assert.Equal(new[] { "OU=Staff,DC=corp,DC=local" }, enforced);
    }

    [Fact]
    public async Task Both_empty_effective_source_is_none_denied()
    {
        var repo = new FakeRepo { GetBaseDns = _ => Task.FromResult(new List<string>()) };
        var (enforced, source) = await Policy(repo, Config()).GetEffectiveAsync(Conn);
        Assert.Equal(BaseDnSource.None, source);
        Assert.Empty(enforced);
    }

    [Fact]
    public async Task Db_read_exception_effective_source_is_none_fail_closed()
    {
        var repo = new FakeRepo { GetBaseDns = _ => throw new InvalidOperationException("db down") };
        var (enforced, source) = await Policy(repo, Config("OU=Staff,DC=corp,DC=local")).GetEffectiveAsync(Conn);
        Assert.Equal(BaseDnSource.None, source);   // fail-closed display too — does NOT show config as effective
        Assert.Empty(enforced);
    }

    [Fact]
    public void Config_base_dns_and_connection_names_are_surfaced_for_the_ui()
    {
        var repo = new FakeRepo();
        var policy = Policy(repo, Config("OU=Staff,DC=corp,DC=local"));
        Assert.Equal(new[] { "OU=Staff,DC=corp,DC=local" }, policy.GetConfigBaseDns(Conn));
        Assert.Contains(Conn, policy.GetConfiguredConnectionNames());
    }
}
