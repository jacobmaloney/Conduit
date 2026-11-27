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
        public Func<string, Task<List<string>>> GetBlocked = _ => Task.FromResult(new List<string>());
        public Task<List<string>> GetBlockedBaseDnsAsync(string sourceConnectionName) => GetBlocked(sourceConnectionName);
        public Task<List<CreationBaseDnEntry>> GetAllAsync() => Task.FromResult(new List<CreationBaseDnEntry>());
        public Task<bool> AddAsync(string n, string d, string? by, bool isBlocked = false) => Task.FromResult(true);
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
        var result = (await Policy(repo, Config("OU=Other,DC=corp,DC=local")).GetScopeAsync(Conn)).Permitted!;
        Assert.Equal(new[] { "OU=Staff,DC=corp,DC=local" }, result);
    }

    [Fact]
    public async Task Db_read_exception_denies_all_fail_closed()
    {
        // A DB failure must NOT fall open to config or "no restriction".
        var repo = new FakeRepo { GetBaseDns = _ => throw new InvalidOperationException("db down") };
        var result = (await Policy(repo, Config("OU=Staff,DC=corp,DC=local")).GetScopeAsync(Conn)).Permitted!;
        Assert.Empty(result);
    }

    [Fact]
    public async Task Db_empty_falls_back_to_config()
    {
        var repo = new FakeRepo { GetBaseDns = _ => Task.FromResult(new List<string>()) };
        var result = (await Policy(repo, Config("OU=Staff,DC=corp,DC=local")).GetScopeAsync(Conn)).Permitted!;
        Assert.Equal(new[] { "OU=Staff,DC=corp,DC=local" }, result);
    }

    [Fact]
    public async Task Both_empty_denies_all()
    {
        var repo = new FakeRepo { GetBaseDns = _ => Task.FromResult(new List<string>()) };
        var result = (await Policy(repo, Config(/* no config */)).GetScopeAsync(Conn)).Permitted!;
        Assert.Empty(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_connection_name_denies_all(string? name)
    {
        var repo = new FakeRepo { GetBaseDns = _ => Task.FromResult(new List<string> { "OU=Staff,DC=corp,DC=local" }) };
        var result = (await Policy(repo, Config("OU=Staff,DC=corp,DC=local")).GetScopeAsync(name!)).Permitted!;
        Assert.Empty(result);
    }

    [Fact]
    public async Task Db_backed_list_feeds_containment_end_to_end()
    {
        var repo = new FakeRepo { GetBaseDns = _ => Task.FromResult(new List<string> { "OU=Users,DC=corp,DC=local" }) };
        var permitted = (await Policy(repo, Config()).GetScopeAsync(Conn)).Permitted!;
        Assert.True(BaseDnContainment.IsContained("OU=Staff,OU=Users,DC=corp,DC=local", permitted));   // contained
        Assert.False(BaseDnContainment.IsContained("OU=Admins,DC=corp,DC=local", permitted));          // outside
    }

    // ── Blocked containers (UC039-F104) ──

    private const string Org = "OU=Organization,DC=corp,DC=local";
    private const string Computers = "OU=Computers,OU=Organization,DC=corp,DC=local";

    private static FakeRepo Blocking(params string[] blocked) => new()
    {
        GetBaseDns = _ => Task.FromResult(new List<string> { Org }),
        GetBlocked = _ => Task.FromResult(blocked.ToList()),
    };

    [Fact]
    public async Task A_permitted_container_includes_everything_beneath_it()
    {
        var scope = await Policy(Blocking(), Config()).GetScopeAsync(Conn);
        Assert.True(BaseDnContainment.IsContained(Org, scope));
        Assert.True(BaseDnContainment.IsContained("OU=Employees,OU=West," + Org, scope));
    }

    [Fact]
    public async Task A_blocked_container_refuses_itself_and_everything_beneath_it_inside_a_permitted_one()
    {
        var scope = await Policy(Blocking(Computers), Config()).GetScopeAsync(Conn);
        Assert.False(BaseDnContainment.IsContained(Computers, scope));
        Assert.False(BaseDnContainment.IsContained("OU=Laptops," + Computers, scope));
        Assert.False(BaseDnContainment.IsStrictlyBelow("CN=PC1," + Computers, scope));
        // A sibling is untouched, and a name that merely ends the same is not the blocked container.
        Assert.True(BaseDnContainment.IsContained("OU=Employees," + Org, scope));
        Assert.True(BaseDnContainment.IsContained("OU=OldComputers," + Org, scope));
        Assert.True(BaseDnContainment.IsStrictlyBelow("CN=Ana Rios,OU=Employees," + Org, scope));
    }

    [Fact]
    public async Task A_block_wins_over_a_permit_placed_beneath_it()
    {
        var repo = Blocking(Computers);
        repo.GetBaseDns = _ => Task.FromResult(new List<string> { Org, "OU=Laptops," + Computers });
        var scope = await Policy(repo, Config()).GetScopeAsync(Conn);
        Assert.False(BaseDnContainment.IsContained("OU=Laptops," + Computers, scope));
    }

    [Fact]
    public async Task A_block_that_cannot_be_read_denies_everything()
    {
        var repo = Blocking();
        repo.GetBlocked = _ => throw new InvalidOperationException("db down");
        var scope = await Policy(repo, Config()).GetScopeAsync(Conn);
        Assert.False(BaseDnContainment.IsContained(Org, scope));

        // A stored block that does not parse is never skipped either.
        var malformed = await Policy(Blocking("not a dn"), Config()).GetScopeAsync(Conn);
        Assert.False(BaseDnContainment.IsContained(Org, malformed));
        Assert.False(BaseDnContainment.IsStrictlyBelow("CN=x," + Org, malformed));
    }

    [Fact]
    public async Task Blocks_apply_to_a_list_that_comes_from_host_config()
    {
        var repo = new FakeRepo { GetBlocked = _ => Task.FromResult(new List<string> { Computers }) };
        var scope = await Policy(repo, Config(Org)).GetScopeAsync(Conn);
        Assert.True(BaseDnContainment.IsContained("OU=Employees," + Org, scope));
        Assert.False(BaseDnContainment.IsContained(Computers, scope));
    }

    [Fact]
    public void The_default_scope_permits_nothing()
    {
        Assert.False(BaseDnContainment.IsContained(Org, default(CreationScope)));
        Assert.False(BaseDnContainment.IsContained(Org, CreationScope.DenyAll));
    }

    [Fact]
    public void Migration_39_adds_the_blocked_flag_idempotently_and_existing_rows_stay_permits()
    {
        var migrator = new Conduit.DataAccess.DatabaseMigrator("Server=offline;Database=offline;", NullLogger<Conduit.DataAccess.DatabaseMigrator>.Instance);
        var m39 = Assert.Single(migrator.GetRequiredMigrations(new Conduit.DataAccess.SchemaAnalysisResult { CurrentVersion = 38 }));
        Assert.Equal(39, m39.Version);
        Assert.Contains("IF COL_LENGTH('dbo.ProvisioningCreationBaseDns','IsBlocked') IS NULL", m39.SqlScript);
        Assert.Contains("[IsBlocked] BIT NOT NULL CONSTRAINT [DF_ProvBaseDns_IsBlocked] DEFAULT 0", m39.SqlScript);
    }

    [Fact]
    public void No_write_path_reads_the_permitted_list_without_its_blocks()
    {
        var services = Path.Combine(RepoRoot(), "src", "Conduit.Web", "Services");
        var files = Directory.GetFiles(services, "AdAgent*Executor.cs")
            .Where(f => File.ReadAllText(f).Contains("_baseDnPolicy", StringComparison.Ordinal)).ToList();
        Assert.True(files.Count >= 3, "the create, write and browse executors all enforce the scope");
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("GetEffectiveAsync(", text);
            Assert.DoesNotContain("GetConfigBaseDns(", text);
            Assert.Contains("_baseDnPolicy.GetScopeAsync(sourceConnectionName)", text);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conduit.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
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
