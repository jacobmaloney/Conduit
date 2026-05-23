using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Conduit.Emulator.GoogleWorkspace.Auth;
using Conduit.Emulator.GoogleWorkspace.Infrastructure;
using Conduit.Emulator.GoogleWorkspace.Models;
using Conduit.Emulator.GoogleWorkspace.Repositories;

namespace Conduit.Emulator.GoogleWorkspace.Seed;

public sealed class TenantSeeder
{
    private readonly GoogleWorkspaceOptions _options;
    private readonly GwCustomerRepository _customers;
    private readonly GwDomainRepository _domains;
    private readonly GwOrgUnitRepository _orgUnits;
    private readonly GwUserRepository _users;
    private readonly GwGroupRepository _groups;
    private readonly GwMemberRepository _members;
    private readonly GwAliasRepository _aliases;
    private readonly ServiceAccountStore _serviceAccounts;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<TenantSeeder> _logger;

    public TenantSeeder(
        IOptions<GoogleWorkspaceOptions> options,
        GwCustomerRepository customers,
        GwDomainRepository domains,
        GwOrgUnitRepository orgUnits,
        GwUserRepository users,
        GwGroupRepository groups,
        GwMemberRepository members,
        GwAliasRepository aliases,
        ServiceAccountStore serviceAccounts,
        IWebHostEnvironment env,
        ILogger<TenantSeeder> logger)
    {
        _options = options.Value;
        _customers = customers;
        _domains = domains;
        _orgUnits = orgUnits;
        _users = users;
        _groups = groups;
        _members = members;
        _aliases = aliases;
        _serviceAccounts = serviceAccounts;
        _env = env;
        _logger = logger;
    }

    public async Task SeedIfEmptyAsync()
    {
        if (await _customers.CountAsync() > 0)
        {
            _logger.LogInformation("Google Workspace emulator already seeded — skipping.");
            return;
        }
        _logger.LogInformation("Seeding Google Workspace emulator tenant '{Domain}'...", _options.PrimaryDomain);
        await SeedAsync();
    }

    public async Task SeedAsync()
    {
        // 1. Customer + domains
        await _customers.UpsertAsync(_options.CustomerId, _options.PrimaryDomain);
        await _domains.UpsertAsync(_options.CustomerId, _options.PrimaryDomain, isPrimary: true);
        foreach (var d in _options.SecondaryDomains)
            await _domains.UpsertAsync(_options.CustomerId, d, isPrimary: false);

        // 2. Org units
        var orgUnits = LoadFixture<List<OrgUnitFixture>>("orgunits.json")
                       ?? new List<OrgUnitFixture>();
        foreach (var ou in orgUnits)
            await _orgUnits.UpsertAsync(_options.CustomerId, ou.Path, ou.Parent, ou.Name, ou.Description);

        var ouPaths = orgUnits.Select(o => o.Path).Append("/").Distinct().ToList();

        // 3. Users
        var firstNames = LoadLines("FirstNames.txt");
        var lastNames = LoadLines("LastNames.txt");
        var titles = LoadLines("Titles.txt");
        var rand = new Random(20260421);

        var userRecords = new List<(GwUser User, string? ManagerEmail, string? Title, string? Department)>();
        var seenEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < _options.SeedUserCount; i++)
        {
            var first = firstNames[rand.Next(firstNames.Count)];
            var last = lastNames[rand.Next(lastNames.Count)];
            var ou = ouPaths[rand.Next(ouPaths.Count)];
            var title = titles[rand.Next(titles.Count)];
            var department = DepartmentFromPath(ou);

            var email = UniqueEmail(first, last, _options.PrimaryDomain, seenEmails, rand);
            var now = DateTime.UtcNow.AddDays(-rand.Next(1, 1500));

            var user = new GwUser
            {
                Id = IdGenerator.NewUserId(),
                CustomerId = _options.CustomerId,
                PrimaryEmail = email,
                Name = new GwName { GivenName = first, FamilyName = last, FullName = $"{first} {last}" },
                OrgUnitPath = ou,
                CreationTime = now,
                LastLoginTime = rand.NextDouble() < 0.85 ? now.AddDays(rand.Next(1, 90)) : null,
                Suspended = rand.NextDouble() < _options.SuspendedRatio,
                Archived = false,
                IsAdmin = false,
                Emails = new List<GwEmail> { new() { Address = email, Type = "work", Primary = true } },
                Organizations = new List<GwOrganization>
                {
                    new() { Name = "Acme Inc", Title = title, Department = department, Type = "work", Primary = true }
                },
                RecoveryEmail = rand.NextDouble() < 0.4 ? $"{first.ToLower()}.{last.ToLower()}@gmail.example" : null
            };
            if (user.Suspended) user.SuspensionReason = "Manually suspended";

            await _users.InsertAsync(user);
            userRecords.Add((user, null, title, department));
        }

        // Promote a handful to admin
        var admins = userRecords.OrderBy(_ => rand.Next()).Take(_options.AdminCount).ToList();
        foreach (var (u, _, _, _) in admins)
            await _users.MakeAdminAsync(u.Id, true);

        // Assign managers (skip some to keep the graph realistic)
        for (int i = 0; i < userRecords.Count; i++)
        {
            if (rand.NextDouble() < 0.25) continue;
            var mgr = userRecords[rand.Next(userRecords.Count)];
            if (mgr.User.Id == userRecords[i].User.Id) continue;

            var u = userRecords[i].User;
            u.Relations = new List<GwRelation> { new() { Value = mgr.User.PrimaryEmail, Type = "manager" } };
            await _users.UpdateAsync(u.Id, u, ifMatch: null);
        }

        // Seed aliases for a subset
        foreach (var (u, _, _, _) in userRecords.Where(_ => rand.NextDouble() < 0.12))
        {
            var alias = $"{u.Name.GivenName.ToLower()}@{_options.PrimaryDomain}";
            await _aliases.UpsertAsync(alias, u.Id, "user", u.PrimaryEmail);
        }

        // 4. Groups
        var groupFixtures = LoadFixture<List<GroupFixture>>("groups.json") ?? new();
        foreach (var gf in groupFixtures.Take(_options.SeedGroupCount))
        {
            var group = new GwGroup
            {
                Id = IdGenerator.NewGroupId(),
                Email = $"{gf.Email}@{_options.PrimaryDomain}",
                Name = gf.Name,
                Description = gf.Description
            };
            await _groups.InsertWithCustomerAsync(group, _options.CustomerId);

            // Membership: pool by org unit, plus admins as OWNER of exec/managers groups.
            var pool = userRecords.Where(r => MatchesOrgUnit(r.User.OrgUnitPath, gf.OrgUnit)).ToList();
            if (pool.Count == 0) pool = userRecords;

            // Group size varies by fixture: all-hands=most, proj-*=small, etc.
            var size = gf.Email switch
            {
                "all-hands" => Math.Min(pool.Count, userRecords.Count),
                "managers" => admins.Count + Math.Min(pool.Count, 8),
                var e when e.StartsWith("proj-") => Math.Min(pool.Count, rand.Next(5, 15)),
                _ => Math.Min(pool.Count, rand.Next(6, 30))
            };
            size = Math.Max(1, size);

            var picks = pool.OrderBy(_ => rand.Next()).Take(size).ToList();
            for (int i = 0; i < picks.Count; i++)
            {
                var role = i == 0 ? "OWNER" : (i < 3 ? "MANAGER" : "MEMBER");
                await _members.InsertAsync(group.Id, new GwMember
                {
                    Id = picks[i].User.Id,
                    Email = picks[i].User.PrimaryEmail,
                    Role = role,
                    Type = "USER",
                    Status = "ACTIVE"
                });
            }
            await _groups.RefreshMemberCountAsync(group.Id);
        }

        // 5. Service accounts (with freshly generated RSA keypairs)
        var saFixtures = LoadFixture<List<ServiceAccountFixture>>("service_accounts.json") ?? new();
        foreach (var sa in saFixtures)
        {
            if (await _serviceAccounts.GetByClientEmailAsync(sa.ClientEmail) is not null) continue;
            var kp = KeyPairFactory.NewRsa2048();
            await _serviceAccounts.UpsertAsync(new ServiceAccountRecord(
                ClientEmail: sa.ClientEmail,
                ClientId: IdGenerator.NewServiceAccountClientId(),
                PrivateKeyId: IdGenerator.NewPrivateKeyId(),
                PublicKeyPem: kp.PublicPem,
                PrivateKeyPem: kp.PrivatePem,
                ProjectId: sa.ProjectId,
                AllowedScopes: sa.AllowedScopes,
                Disabled: false,
                CreatedAt: DateTime.UtcNow));
        }

        _logger.LogInformation(
            "Seed complete: {Users} users, {Groups} groups, {Admins} admins.",
            userRecords.Count, Math.Min(groupFixtures.Count, _options.SeedGroupCount), admins.Count);
    }

    private static bool MatchesOrgUnit(string userPath, string fixturePath)
    {
        if (fixturePath == "/") return true;
        return userPath == fixturePath || userPath.StartsWith(fixturePath + "/");
    }

    private static string DepartmentFromPath(string orgUnitPath)
    {
        if (orgUnitPath == "/") return "Company";
        return orgUnitPath.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static string UniqueEmail(string first, string last, string domain, HashSet<string> seen, Random rand)
    {
        var baseEmail = $"{first}.{last}".ToLowerInvariant();
        var email = $"{baseEmail}@{domain}";
        int suffix = 2;
        while (!seen.Add(email))
            email = $"{baseEmail}{suffix++}@{domain}";
        return email;
    }

    private T? LoadFixture<T>(string fileName)
    {
        var path = Path.Combine(_env.ContentRootPath, "Seed", "Fixtures", fileName);
        if (!File.Exists(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, "Seed", "Fixtures", fileName);
            if (!File.Exists(path))
            {
                _logger.LogWarning("Seed fixture not found: {File}", fileName);
                return default;
            }
        }
        return JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
    }

    private List<string> LoadLines(string fileName)
    {
        var path = Path.Combine(_env.ContentRootPath, "Seed", "Data", fileName);
        if (!File.Exists(path))
            path = Path.Combine(AppContext.BaseDirectory, "Seed", "Data", fileName);
        if (!File.Exists(path)) return new();
        return File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
    }

    private sealed class OrgUnitFixture
    {
        public string Path { get; set; } = "/";
        public string? Parent { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    private sealed class GroupFixture
    {
        public string Email { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string OrgUnit { get; set; } = "/";
    }

    private sealed class ServiceAccountFixture
    {
        public string ClientEmail { get; set; } = string.Empty;
        public string ProjectId { get; set; } = string.Empty;
        public string AllowedScopes { get; set; } = string.Empty;
    }
}

public sealed class TenantSeederHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly GoogleWorkspaceOptions _options;
    private readonly ILogger<TenantSeederHostedService> _logger;

    public TenantSeederHostedService(
        IServiceProvider services,
        IOptions<GoogleWorkspaceOptions> options,
        ILogger<TenantSeederHostedService> logger)
    {
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.SeedOnStartup) return;
        using var scope = _services.CreateScope();
        var initializer = scope.ServiceProvider.GetRequiredService<GwDatabaseInitializer>();
        await initializer.InitializeAsync();
        var seeder = scope.ServiceProvider.GetRequiredService<TenantSeeder>();
        try
        {
            await seeder.SeedIfEmptyAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Seed failed — emulator will continue but may be empty.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
