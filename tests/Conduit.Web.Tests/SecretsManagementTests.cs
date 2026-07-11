using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Conduit.Sync.Security;
using Conduit.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// HIGH-2 secrets relocation acceptance tests (Worf's assertion bar):
/// exact ACLs on secret files, boot precedence, merge-not-replace writes,
/// relocator migrate/no-op/failure semantics, scrubs, and CLI override precedence.
/// </summary>
public class RestrictedFileWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Write_ExactAcl_OwnerSystemAdminsOnly_NoUsers_NoInheritance()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(_dir, "secret.json");
        RestrictedFileWriter.Write(path, "{ \"k\": \"v\" }");

        Assert.Equal("{ \"k\": \"v\" }", File.ReadAllText(path));

        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected, "inheritance must be disabled");

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.NotEmpty(rules);

        var allowed = new[]
        {
            WindowsIdentity.GetCurrent().User,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        };
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        foreach (var rule in rules)
        {
            Assert.False(rule.IsInherited, "no inherited ACEs may remain");
            var sid = (SecurityIdentifier)rule.IdentityReference;
            Assert.NotEqual(users, sid); // BUILTIN\Users must never appear
            Assert.Contains(sid, allowed!);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        }
    }

    [Fact]
    public void Write_ReplacesExistingContentCompletely()
    {
        var path = Path.Combine(_dir, "secret.json");
        RestrictedFileWriter.Write(path, "first-version-with-longer-content");
        RestrictedFileWriter.Write(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir)); // no *.tmp leftovers
    }

    [Fact]
    public void Write_FailedRewrite_PreservesTheOnlyCopy_AndCleansTemp()
    {
        // HIGH-2 regression: a failed rewrite (here: destination locked, the
        // same observable class as disk-full/crash) must never truncate or
        // delete the pre-existing secret — only the temp file may die.
        var path = Path.Combine(_dir, "secret.json");
        RestrictedFileWriter.Write(path, "precious-only-copy");

        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<Exception>(() => RestrictedFileWriter.Write(path, "replacement"));
        }

        Assert.Equal("precious-only-copy", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir)); // temp cleaned, original intact
    }

    [Fact]
    public void Write_PreExistingLaxFile_GetsLockedAclOnRewrite()
    {
        // MEDIUM-1 regression: a secrets file created earlier with inherited
        // (lax) permissions must come out of the next Write fully locked.
        if (!OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(_dir, "secret.json");
        File.WriteAllText(path, "created-lax");
        var laxSecurity = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        Assert.False(laxSecurity.AreAccessRulesProtected); // sanity: it really was inheriting

        RestrictedFileWriter.Write(path, "now-locked");

        Assert.Equal("now-locked", File.ReadAllText(path));
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            Assert.False(rule.IsInherited);
            Assert.NotEqual(users, (SecurityIdentifier)rule.IdentityReference);
        }
    }
}

public class SecretsFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;
    private string SecretsPath => Path.Combine(_dir, "secrets.json");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Update_MergesIntoExistingContent_NeverReplacesWholeFile()
    {
        File.WriteAllText(SecretsPath, """{ "Existing": { "Keep": "me" } }""");

        SecretsFile.Update(root => root["New"] = new JsonObject { ["Key"] = "value" }, SecretsPath);

        var root = JsonNode.Parse(File.ReadAllText(SecretsPath))!.AsObject();
        Assert.Equal("me", root["Existing"]?["Keep"]?.GetValue<string>());
        Assert.Equal("value", root["New"]?["Key"]?.GetValue<string>());
    }

    [Fact]
    public void Update_CorruptFile_ThrowsInsteadOfClobbering()
    {
        File.WriteAllText(SecretsPath, "{ not json ");

        Assert.ThrowsAny<Exception>(() => SecretsFile.Update(root => root["X"] = "y", SecretsPath));
        Assert.Equal("{ not json ", File.ReadAllText(SecretsPath)); // untouched
    }
}

public class SecretsRelocatorTests : IDisposable
{
    private readonly string _appDir = Directory.CreateTempSubdirectory().FullName;
    private readonly string _dataDir = Directory.CreateTempSubdirectory().FullName;

    private string BasePath => Path.Combine(_appDir, "appsettings.json");
    private string ProdPath => Path.Combine(_appDir, "appsettings.Production.json");
    private string SecretsPath => Path.Combine(_dataDir, "secrets.json");

    public void Dispose()
    {
        try { Directory.Delete(_appDir, true); } catch { }
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private static JsonObject Parse(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    [Fact]
    public void FirstBoot_MigratesAllSecretShapes_SecondBoot_NoOps()
    {
        File.WriteAllText(BasePath, """
            {
              "Logging": { "LogLevel": { "Default": "Information" } },
              "ConnectionStrings": { "DefaultConnection": "Server=sql01;Database=Conduit;Integrated Security=True" },
              "Jwt": { "SecretKey": "base-jwt-secret", "Issuer": "Conduit" },
              "Provision": { "ConnectionString": "Server=sql01;Database=Conduit", "JwtSecretKey": "prov-jwt", "ServerPort": 5500 },
              "Enroll": { "Url": "https://platform.example.com", "Code": "ABC123" }
            }
            """);

        var migrated = SecretsRelocator.Run(_appDir, SecretsPath, "Production");
        Assert.True(migrated);

        // Secrets landed in secrets.json.
        var secrets = Parse(SecretsPath);
        Assert.Equal("Server=sql01;Database=Conduit;Integrated Security=True", secrets["ConnectionStrings"]?["DefaultConnection"]?.GetValue<string>());
        Assert.Equal("base-jwt-secret", secrets["Jwt"]?["SecretKey"]?.GetValue<string>());
        Assert.Equal("prov-jwt", secrets["Provision"]?["JwtSecretKey"]?.GetValue<string>());
        Assert.Equal(5500, secrets["Provision"]?["ServerPort"]?.GetValue<int>());
        Assert.Equal("ABC123", secrets["Enroll"]?["Code"]?.GetValue<string>());

        // Sources were cleaned of secrets but keep everything non-secret.
        var cleaned = Parse(BasePath);
        Assert.Null(cleaned["ConnectionStrings"]?["DefaultConnection"]);
        Assert.Null(cleaned["Jwt"]?["SecretKey"]);
        Assert.Null(cleaned["Provision"]);
        Assert.Null(cleaned["Enroll"]?["Code"]);
        Assert.Equal("Conduit", cleaned["Jwt"]?["Issuer"]?.GetValue<string>());
        Assert.Equal("https://platform.example.com", cleaned["Enroll"]?["Url"]?.GetValue<string>());
        Assert.Equal("Information", cleaned["Logging"]?["LogLevel"]?["Default"]?.GetValue<string>());

        // Second boot: nothing left to migrate.
        var snapshotBase = File.ReadAllText(BasePath);
        var snapshotSecrets = File.ReadAllText(SecretsPath);
        Assert.False(SecretsRelocator.Run(_appDir, SecretsPath, "Production"));
        Assert.Equal(snapshotBase, File.ReadAllText(BasePath));
        Assert.Equal(snapshotSecrets, File.ReadAllText(SecretsPath));
    }

    [Fact]
    public void DevelopmentEnvironment_IsSkippedEntirely()
    {
        File.WriteAllText(BasePath, """{ "Jwt": { "SecretKey": "dev-secret" } }""");

        Assert.False(SecretsRelocator.Run(_appDir, SecretsPath, "Development"));
        Assert.False(File.Exists(SecretsPath));
        Assert.Equal("dev-secret", Parse(BasePath)["Jwt"]?["SecretKey"]?.GetValue<string>());
    }

    [Fact]
    public void PlaceholderAndBlankValues_AreNotMigrated()
    {
        File.WriteAllText(BasePath, """
            {
              "ConnectionStrings": { "DefaultConnection": "Server=YOUR_SERVER;Database=YOUR_DATABASE" },
              "Jwt": { "SecretKey": "" },
              "Enroll": { "Url": "https://x.example.com" }
            }
            """);

        Assert.False(SecretsRelocator.Run(_appDir, SecretsPath, "Production"));
        Assert.False(File.Exists(SecretsPath));
    }

    [Fact]
    public void WriteFailure_LeavesOriginalsUntouched()
    {
        var original = """{ "Jwt": { "SecretKey": "keep-me-safe" } }""";
        File.WriteAllText(BasePath, original);

        // secrets path nested under a FILE — directory creation must fail.
        var blocker = Path.Combine(_dataDir, "blocker");
        File.WriteAllText(blocker, "x");
        var impossibleSecretsPath = Path.Combine(blocker, "sub", "secrets.json");

        var warnings = new List<string>();
        Assert.False(SecretsRelocator.Run(_appDir, impossibleSecretsPath, "Production", warnings.Add));

        Assert.Equal(original, File.ReadAllText(BasePath)); // untouched
        Assert.Contains(warnings, w => w.Contains("relocation failed"));
    }

    [Fact]
    public void ExistingSecretsJsonValue_WinsOverStaleAppsettingsCopy()
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(SecretsPath, """{ "Jwt": { "SecretKey": "authoritative" } }""");
        File.WriteAllText(BasePath, """{ "Jwt": { "SecretKey": "stale-copy" } }""");

        Assert.True(SecretsRelocator.Run(_appDir, SecretsPath, "Production"));

        Assert.Equal("authoritative", Parse(SecretsPath)["Jwt"]?["SecretKey"]?.GetValue<string>());
        Assert.Null(Parse(BasePath)["Jwt"]?["SecretKey"]); // stale copy still removed
    }

    [Fact]
    public void ProductionValue_WinsOverBase_MatchingRuntimePrecedence()
    {
        File.WriteAllText(BasePath, """{ "Jwt": { "SecretKey": "from-base" } }""");
        File.WriteAllText(ProdPath, """{ "Jwt": { "SecretKey": "from-production" } }""");

        Assert.True(SecretsRelocator.Run(_appDir, SecretsPath, "Production"));

        Assert.Equal("from-production", Parse(SecretsPath)["Jwt"]?["SecretKey"]?.GetValue<string>());
        Assert.Null(Parse(BasePath)["Jwt"]?["SecretKey"]);
        Assert.Null(Parse(ProdPath)["Jwt"]?["SecretKey"]);
    }
}

public class BootPrecedenceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private IConfiguration BuildLikeProgramCs(string? secretsContent, string[]? args = null)
    {
        // Mirrors the Program.cs layering: appsettings*.json (+ CLI via CreateBuilder),
        // then secrets.json appended LAST.
        var appsettings = Path.Combine(_dir, "appsettings.json");
        File.WriteAllText(appsettings, """
            {
              "ConnectionStrings": { "DefaultConnection": "Server=from-appsettings" },
              "Enroll": { "Url": "https://appsettings.example.com", "Code": "stale-appsettings-code" }
            }
            """);
        var secretsPath = Path.Combine(_dir, "secrets.json");
        if (secretsContent is not null)
            File.WriteAllText(secretsPath, secretsContent);

        var builder = new ConfigurationBuilder()
            .AddJsonFile(appsettings, optional: false)
            .AddCommandLine(args ?? Array.Empty<string>());
        builder.AddJsonFile(secretsPath, optional: true, reloadOnChange: false);
        return builder.Build();
    }

    [Fact]
    public void SecretsJson_OverridesAppsettings()
    {
        var config = BuildLikeProgramCs("""{ "ConnectionStrings": { "DefaultConnection": "Server=from-secrets" } }""");

        Assert.Equal("Server=from-secrets", config.GetConnectionString("DefaultConnection"));
    }

    [Fact]
    public void MissingSecretsJson_BootsOnAppsettingsValues()
    {
        var config = BuildLikeProgramCs(secretsContent: null);

        Assert.Equal("Server=from-appsettings", config.GetConnectionString("DefaultConnection"));
    }

    [Fact]
    public void EnrollCodeCli_BeatsStaleSecretsJsonCode()
    {
        var config = BuildLikeProgramCs(
            """{ "Enroll": { "Url": "https://secrets.example.com", "Code": "stale-secrets-code" } }""",
            new[] { "--enroll-url=https://cli.example.com", "--enroll-code=fresh-cli-code" });

        var (url, code) = EnrollmentService.ResolveEnrollmentConfig(config);

        Assert.Equal("https://cli.example.com", url);
        Assert.Equal("fresh-cli-code", code);
    }

    [Fact]
    public void WithoutCli_EnrollCodeComesFromSecretsJson()
    {
        var config = BuildLikeProgramCs("""{ "Enroll": { "Url": "https://secrets.example.com", "Code": "secrets-code" } }""");

        var (url, code) = EnrollmentService.ResolveEnrollmentConfig(config);

        Assert.Equal("https://secrets.example.com", url);
        Assert.Equal("secrets-code", code);
    }
}

public class SetupConfigWriteTests
{
    [Fact]
    public void ApplySecrets_MergesWithoutDroppingOtherContent()
    {
        var root = JsonNode.Parse("""{ "Provision": { "ServerPort": 5500 }, "Jwt": { "ExpirationMinutes": 60 } }""")!.AsObject();

        SetupService.ApplySecrets(root, new SetupConfiguration
        {
            ConnectionString = "Server=sql01;Database=Conduit",
            JwtSecretKey = "new-secret"
        });

        Assert.Equal("Server=sql01;Database=Conduit", root["ConnectionStrings"]?["DefaultConnection"]?.GetValue<string>());
        Assert.Equal("new-secret", root["Jwt"]?["SecretKey"]?.GetValue<string>());
        Assert.Equal("Conduit", root["Jwt"]?["Issuer"]?.GetValue<string>());
        Assert.Equal(60, root["Jwt"]?["ExpirationMinutes"]?.GetValue<int>()); // preserved
        Assert.Equal(5500, root["Provision"]?["ServerPort"]?.GetValue<int>()); // preserved
    }

    [Fact]
    public void ApplyKestrelPort_MergesWithoutDroppingOtherContent()
    {
        var root = JsonNode.Parse("""{ "Logging": { "LogLevel": { "Default": "Warning" } } }""")!.AsObject();

        SetupService.ApplyKestrelPort(root, 5500);

        Assert.Equal("http://localhost:5500", root["Kestrel"]?["Endpoints"]?["Http"]?["Url"]?.GetValue<string>());
        Assert.Equal("Warning", root["Logging"]?["LogLevel"]?["Default"]?.GetValue<string>()); // preserved
    }

    [Theory]
    [InlineData("Production", "appsettings.Production.json")]
    [InlineData(null, "appsettings.Production.json")]
    [InlineData("Development", "appsettings.Development.json")]
    public void BuildEnvironmentConfigPath_AnchorsToContentRoot(string? environment, string expectedFileName)
    {
        var path = SetupService.BuildEnvironmentConfigPath(@"C:\install\root", environment);

        Assert.Equal(Path.Combine(@"C:\install\root", expectedFileName), path);
    }
}

public class SecretsScrubTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;
    private string SecretsPath => Path.Combine(_dir, "secrets.json");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void ProvisionScrub_RemovesOnlyProvision()
    {
        File.WriteAllText(SecretsPath, """
            {
              "ConnectionStrings": { "DefaultConnection": "Server=sql01" },
              "Jwt": { "SecretKey": "s" },
              "Provision": { "ConnectionString": "Server=sql01", "JwtSecretKey": "p" }
            }
            """);

        ProvisioningService.ScrubProvisionSection(NullLogger.Instance, SecretsPath);

        var root = JsonNode.Parse(File.ReadAllText(SecretsPath))!.AsObject();
        Assert.Null(root["Provision"]);
        Assert.Equal("Server=sql01", root["ConnectionStrings"]?["DefaultConnection"]?.GetValue<string>());
        Assert.Equal("s", root["Jwt"]?["SecretKey"]?.GetValue<string>());
    }

    [Fact]
    public void EnrollScrub_RemovesCode_KeepsUrl()
    {
        File.WriteAllText(SecretsPath, """{ "Enroll": { "Url": "https://x.example.com", "Code": "ABC" } }""");

        EnrollmentService.ScrubEnrollCode(NullLogger.Instance, "test", SecretsPath);

        var root = JsonNode.Parse(File.ReadAllText(SecretsPath))!.AsObject();
        Assert.Null(root["Enroll"]?["Code"]);
        Assert.Equal("https://x.example.com", root["Enroll"]?["Url"]?.GetValue<string>());
    }

    [Fact]
    public void EnrollScrub_RemovesEmptyEnrollSection()
    {
        File.WriteAllText(SecretsPath, """{ "Enroll": { "Code": "ABC" } }""");

        EnrollmentService.ScrubEnrollCode(NullLogger.Instance, "test", SecretsPath);

        Assert.Null(JsonNode.Parse(File.ReadAllText(SecretsPath))!.AsObject()["Enroll"]);
    }

    [Fact]
    public void Scrubs_NoSecretsFile_DoNotCreateOne()
    {
        ProvisioningService.ScrubProvisionSection(NullLogger.Instance, SecretsPath);
        EnrollmentService.ScrubEnrollCode(NullLogger.Instance, "test", SecretsPath);

        Assert.False(File.Exists(SecretsPath));
    }
}
