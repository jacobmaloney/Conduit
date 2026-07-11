using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Conduit.Core.Models;
using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Security;
using Conduit.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// MEDIUM-2: the scrubs proven THROUGH the real service flows, not just as
/// isolated functions — EnrollmentService.RunAtStartupAsync (success / 403 /
/// transient / already-enrolled) and ProvisioningService.TryApplyAtStartupAsync.
/// DB and HTTP boundaries are substituted (virtual repo/protector members, fake
/// HttpMessageHandler); everything in between is the production code path.
/// Note: the enroll flow reads ConduitInstanceIdentity.InstanceId, which may
/// create the (benign, non-secret) instance-id.json in the real data dir.
/// </summary>
public class EnrollmentScrubWiringTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;
    private string SecretsPath => Path.Combine(_dir, "secrets.json");
    private string StatusPath => Path.Combine(_dir, "enroll-status.json");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ── Fakes over the DB/HTTP boundaries ───────────────────────────────

    private sealed class FakeTenantRepository : TenantRepository
    {
        public List<Tenant> Existing { get; } = new();
        public Tenant? Created { get; private set; }

        public FakeTenantRepository() : base(new DatabaseConfig()) { }

        public override Task<List<Tenant>> GetAllAsync(bool includeInactive = false) =>
            Task.FromResult(Existing.ToList());

        public override Task<Tenant> CreateAsync(Tenant tenant)
        {
            tenant.Id = Guid.NewGuid();
            Created = tenant;
            return Task.FromResult(tenant);
        }

        public override Task<bool> NameOrSlugInUseByOtherAsync(string newName, Guid excludeTenantId) =>
            Task.FromResult(false);

        public override Task<bool> StampIcEntitlementAsync(Guid id, string baseUrl) =>
            Task.FromResult(true);

        public override Task<bool> DeleteAsync(Guid id) => Task.FromResult(true);
    }

    private sealed class FakeCredentialProtector : CredentialProtector
    {
        public Func<Guid, string, string?>? OnRetrieve { get; set; }
        public Dictionary<(Guid, string), string> Stored { get; } = new();

        public FakeCredentialProtector() : base(
            new ConfigurationBuilder().Build(),
            new ConnectionCredentialRepository(new DatabaseConfig()),
            new CredentialKeyringRepository(new DatabaseConfig()))
        { }

        public override Task StoreAsync(Guid tenantId, string credentialName, string plaintext)
        {
            Stored[(tenantId, credentialName)] = plaintext;
            return Task.CompletedTask;
        }

        public override Task<string?> RetrieveAsync(Guid tenantId, string credentialName) =>
            Task.FromResult(OnRetrieve?.Invoke(tenantId, credentialName));
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _responder;
        public int Calls { get; private set; }

        public CountingHandler(Func<int, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_responder(Calls));
        }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private EnrollmentService BuildService(
        CountingHandler handler,
        FakeTenantRepository tenants,
        FakeCredentialProtector protector)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TenantRepository>(tenants);
        services.AddSingleton<CredentialProtector>(protector);
        var provider = services.BuildServiceProvider();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Enroll:Url"] = "https://ic.example.com",
            ["Enroll:Code"] = "CODE-1"
        }).Build();

        return new EnrollmentService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeHttpClientFactory(handler),
            config,
            NullLogger<EnrollmentService>.Instance)
        {
            SecretsPathOverride = SecretsPath,
            StatusFilePathOverride = StatusPath
        };
    }

    private void WriteSecretsWithEnrollCode() =>
        File.WriteAllText(SecretsPath, """{ "Enroll": { "Url": "https://ic.example.com", "Code": "CODE-1" }, "Jwt": { "SecretKey": "keep" } }""");

    private static HttpResponseMessage SuccessResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""{ "baseUrl": "https://ic.example.com", "tenantSlug": "acme", "agentId": "{{Guid.NewGuid()}}", "agentApiKey": "agent-key", "syncApiKey": "sync-key" }""",
            Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task SuccessfulEnrollment_ScrubsEnrollCode_ThroughTheRealFlow()
    {
        WriteSecretsWithEnrollCode();
        var tenants = new FakeTenantRepository();
        var protector = new FakeCredentialProtector();
        var service = BuildService(new CountingHandler(_ => SuccessResponse()), tenants, protector);

        await service.RunAtStartupAsync(databaseReady: true);

        Assert.NotNull(tenants.Created); // enrollment really completed
        Assert.NotEmpty(protector.Stored);
        var secrets = JsonNode.Parse(File.ReadAllText(SecretsPath))!.AsObject();
        Assert.Null(secrets["Enroll"]?["Code"]);
        Assert.Equal("https://ic.example.com", secrets["Enroll"]?["Url"]?.GetValue<string>());
        Assert.Equal("keep", secrets["Jwt"]?["SecretKey"]?.GetValue<string>());
        Assert.Contains("\"Success\"", File.ReadAllText(StatusPath));
    }

    [Fact]
    public async Task Definitive403_ScrubsEnrollCode_ThroughTheRealFlow()
    {
        WriteSecretsWithEnrollCode();
        var service = BuildService(
            new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)),
            new FakeTenantRepository(), new FakeCredentialProtector());

        await service.RunAtStartupAsync(databaseReady: true);

        var secrets = JsonNode.Parse(File.ReadAllText(SecretsPath))!.AsObject();
        Assert.Null(secrets["Enroll"]?["Code"]); // consumed server-side — never resend
        Assert.Contains("\"Failed\"", File.ReadAllText(StatusPath));
    }

    [Fact]
    public async Task TransientFailure_KeepsEnrollCode_ForTheNextRestart()
    {
        WriteSecretsWithEnrollCode();
        var service = BuildService(
            new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)),
            new FakeTenantRepository(), new FakeCredentialProtector());

        await service.RunAtStartupAsync(databaseReady: true);

        var secrets = JsonNode.Parse(File.ReadAllText(SecretsPath))!.AsObject();
        Assert.Equal("CODE-1", secrets["Enroll"]?["Code"]?.GetValue<string>()); // retryable — keep
    }

    [Fact]
    public async Task AlreadyEnrolled_CredentialOriginScan_PreventsResend_NoHttpCall()
    {
        // Restart-no-resend: a stored IdentityCenter credential pointing at the
        // enroll origin must short-circuit BEFORE any HTTP traffic.
        WriteSecretsWithEnrollCode();
        var tenants = new FakeTenantRepository();
        tenants.Existing.Add(new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "IdentityCenter-acme",
            Slug = "identitycenter-acme",
            SystemType = "IdentityCenter",
            IsActive = true
        });
        var protector = new FakeCredentialProtector
        {
            OnRetrieve = (_, name) => name == "identitycenter"
                ? """{ "BaseUrl": "https://ic.example.com", "ApiKey": "k", "AgentApiKey": "a" }"""
                : null
        };
        var handler = new CountingHandler(_ => SuccessResponse());
        var service = BuildService(handler, tenants, protector);

        await service.RunAtStartupAsync(databaseReady: true);

        Assert.Equal(0, handler.Calls); // the consumed single-use code was never re-sent
        Assert.Null(tenants.Created);
        Assert.Contains("Enrolled against", service.StateDescription);
        Assert.Contains("Skipped-already-enrolled", File.ReadAllText(StatusPath));
    }
}

/// <summary>Provision scrub proven through TryApplyAtStartupAsync, not just the scrub function.</summary>
public class ProvisioningScrubWiringTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;
    private string SecretsPath => Path.Combine(_dir, "secrets.json");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Conduit.Web.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeSetupService : SetupService
    {
        public bool ThrowAlreadyCompleted { get; set; }
        public bool Applied { get; private set; }

        public FakeSetupService(IConfiguration config) : base(
            config, NullLogger<SetupService>.Instance, new DatabaseConfig(),
            new SetupRepository(new DatabaseConfig()), new FakeHostEnvironment())
        { }

        public override Task<bool> ApplySetupAsync(SetupConfiguration config)
        {
            if (ThrowAlreadyCompleted)
                throw new SetupAlreadyCompletedException();
            Applied = true;
            return Task.FromResult(true);
        }
    }

    private static IConfiguration ProvisionConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Provision:ConnectionString"] = "Server=sql01;Database=Conduit;Integrated Security=True",
            ["Provision:AdminPassword"] = "Str0ng-Passw0rd!x", // supplied → no generated-password file
            ["Provision:JwtSecretKey"] = new string('k', 44)
        }).Build();

    private void WriteSecretsWithProvision() =>
        File.WriteAllText(SecretsPath, """
            {
              "Provision": { "ConnectionString": "Server=sql01;Database=Conduit", "JwtSecretKey": "p" },
              "Enroll": { "Url": "https://ic.example.com" }
            }
            """);

    [Fact]
    public async Task SuccessfulProvisionedSetup_RemovesProvisionSection_FromSecretsJson()
    {
        WriteSecretsWithProvision();
        var config = ProvisionConfig();
        var setup = new FakeSetupService(config);
        var service = new ProvisioningService(setup, config, NullLogger<ProvisioningService>.Instance)
        {
            SecretsPathOverride = SecretsPath
        };

        Assert.True(await service.TryApplyAtStartupAsync());

        Assert.True(setup.Applied);
        var secrets = JsonNode.Parse(File.ReadAllText(SecretsPath))!.AsObject();
        Assert.Null(secrets["Provision"]);
        Assert.Equal("https://ic.example.com", secrets["Enroll"]?["Url"]?.GetValue<string>()); // untouched
    }

    [Fact]
    public async Task SetupAlreadyComplete_StillRemovesTheDeadProvisionSection()
    {
        WriteSecretsWithProvision();
        var config = ProvisionConfig();
        var setup = new FakeSetupService(config) { ThrowAlreadyCompleted = true };
        var service = new ProvisioningService(setup, config, NullLogger<ProvisioningService>.Instance)
        {
            SecretsPathOverride = SecretsPath
        };

        Assert.False(await service.TryApplyAtStartupAsync());

        Assert.Null(JsonNode.Parse(File.ReadAllText(SecretsPath))!.AsObject()["Provision"]);
    }
}
