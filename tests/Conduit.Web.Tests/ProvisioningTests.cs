using System.Text.Json;
using Conduit.Web.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// DB-free tests of the provisioned-boot config mapping
/// (<see cref="ProvisioningService.TryBuildConfiguration"/>) and the
/// enroll-status JSON shape (<see cref="EnrollmentStatusReporter.BuildStatusJson"/>).
/// </summary>
public class ProvisioningTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value))
            .Build();

    // ── Provision section mapping ────────────────────────────────────────────

    [Fact]
    public void No_Provision_section_returns_null()
    {
        var setup = ProvisioningService.TryBuildConfiguration(Config(), out var generated);
        Assert.Null(setup);
        Assert.False(generated);
    }

    [Fact]
    public void Provision_without_ConnectionString_returns_null()
    {
        var setup = ProvisioningService.TryBuildConfiguration(
            Config(("Provision:AdminUsername", "ops")), out _);
        Assert.Null(setup);
    }

    [Fact]
    public void Full_section_maps_every_supported_key()
    {
        var setup = ProvisioningService.TryBuildConfiguration(Config(
            ("Provision:ConnectionString", "Server=.\\SQLEXPRESS;Database=Conduit;Trusted_Connection=True;TrustServerCertificate=True"),
            ("Provision:AdminUsername", "opsadmin"),
            ("Provision:AdminPassword", "SuppliedPassw0rd!"),
            ("Provision:JwtSecretKey", new string('k', 40)),
            ("Provision:ServerPort", "6600")), out var generated);

        Assert.NotNull(setup);
        Assert.False(generated);
        Assert.Equal("Server=.\\SQLEXPRESS;Database=Conduit;Trusted_Connection=True;TrustServerCertificate=True", setup!.ConnectionString);
        Assert.Equal("opsadmin", setup.AdminUsername);
        Assert.Equal("SuppliedPassw0rd!", setup.AdminPassword);
        Assert.Equal(new string('k', 40), setup.JwtSecretKey);
        Assert.Equal(6600, setup.ServerPort);
        Assert.True(setup.AutoCreateDatabase);
    }

    [Fact]
    public void Defaults_apply_when_only_ConnectionString_is_supplied()
    {
        var setup = ProvisioningService.TryBuildConfiguration(
            Config(("Provision:ConnectionString", "Server=x;Database=y")), out _);

        Assert.NotNull(setup);
        Assert.Equal("admin", setup!.AdminUsername);
        Assert.Equal(5500, setup.ServerPort);
    }

    [Fact]
    public void Blank_AdminPassword_generates_a_strong_unique_one()
    {
        var cfg = Config(("Provision:ConnectionString", "Server=x;Database=y"));
        var a = ProvisioningService.TryBuildConfiguration(cfg, out var generatedA)!;
        var b = ProvisioningService.TryBuildConfiguration(cfg, out var generatedB)!;

        Assert.True(generatedA);
        Assert.True(generatedB);
        Assert.True(a.AdminPassword.Length >= 20);
        Assert.NotEqual(a.AdminPassword, b.AdminPassword);
    }

    [Fact]
    public void Blank_JwtSecretKey_generates_one_that_passes_the_32_char_floor()
    {
        var setup = ProvisioningService.TryBuildConfiguration(
            Config(("Provision:ConnectionString", "Server=x;Database=y")), out _)!;

        Assert.True(setup.JwtSecretKey.Length >= 32);
    }

    [Fact]
    public void Invalid_ServerPort_falls_back_to_default()
    {
        var setup = ProvisioningService.TryBuildConfiguration(Config(
            ("Provision:ConnectionString", "Server=x;Database=y"),
            ("Provision:ServerPort", "not-a-port")), out _)!;

        Assert.Equal(5500, setup.ServerPort);
    }

    // ── Enroll status file shape ─────────────────────────────────────────────

    [Fact]
    public void BuildStatusJson_carries_outcome_timestamp_category_and_detail()
    {
        var when = new DateTime(2026, 7, 9, 12, 30, 0, DateTimeKind.Utc);
        var json = EnrollmentStatusReporter.BuildStatusJson(
            EnrollmentStatusReporter.OutcomeFailed, when, "invalid_or_expired_code",
            "Enrollment failed: enroll code invalid or expired — generate a new one in the tenant portal.");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Failed", doc.RootElement.GetProperty("Outcome").GetString());
        Assert.Equal("2026-07-09T12:30:00.0000000Z", doc.RootElement.GetProperty("TimestampUtc").GetString());
        Assert.Equal("invalid_or_expired_code", doc.RootElement.GetProperty("ErrorCategory").GetString());
        Assert.Contains("invalid or expired", doc.RootElement.GetProperty("Detail").GetString());
    }

    [Fact]
    public void BuildStatusJson_success_has_null_category()
    {
        var json = EnrollmentStatusReporter.BuildStatusJson(
            EnrollmentStatusReporter.OutcomeSuccess, DateTime.UtcNow, null,
            "Enrolled against https://ic.example.com as agent 6f9619ff-8b86-d011-b42d-00cf4fc964ff (connection 'IdentityCenter-acme').");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Success", doc.RootElement.GetProperty("Outcome").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("ErrorCategory").ValueKind);
    }

    [Fact]
    public void StatusFilePath_lands_in_the_conduit_data_dir()
    {
        var path = EnrollmentStatusReporter.StatusFilePath;
        Assert.EndsWith("enroll-status.json", path);
        Assert.Contains("onduit", Path.GetDirectoryName(path)!); // Conduit (Windows) / conduit (POSIX)
    }
}
