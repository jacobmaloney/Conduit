using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Security;
using Conduit.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// Tests for the DB-INDEPENDENT trust-boundary gates of <see cref="AwsAgentWriteExecutor"/>.
///
/// HONEST SCOPE NOTE: the executor's concrete deps (SinkConnectionCredentialMapRepository,
/// TenantRepository, CredentialProtector) extend BaseRepository with NON-virtual methods that
/// hit SQL, so they cannot be mocked. We therefore construct REAL instances bound to a bogus
/// (never-opened) connection string and exercise ONLY the gates that run BEFORE any repository
/// call: empty-payload, 64 KB cap, strict-JSON, null-deserialize, schemaVersion, and the
/// operation allow-list. The credential-resolution step and everything after it (incl. the
/// privileged-attach "marker required" refusal inside DoManagedPolicyAsync) is past the first
/// repo call and requires a live DB + AWS — it CANNOT be unit-proven here and is called out in
/// the report. The privileged-attach refusal logic IS proven on the IC side and via the
/// AwsIamWriter classifier tests.
///
/// Every case also asserts the executor NEVER throws (returns a (false, msg) tuple).
/// </summary>
public class AwsAgentWriteExecutorGateTests
{
    private static AwsAgentWriteExecutor BuildExecutor()
    {
        // Bogus config: connection is only opened at query time, which the pre-credential
        // gates never reach. A valid 32-byte base64 key lets CredentialProtector construct.
        var dbConfig = new DatabaseConfig { ConnectionString = "Server=(local);Database=__none__;Trusted_Connection=False;" };
        var key = Convert.ToBase64String(new byte[32]); // 32 zero bytes -> valid length
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Sync:CredentialKey"] = key })
            .Build();

        var credRepo = new ConnectionCredentialRepository(dbConfig);
        var keyring = new CredentialKeyringRepository(dbConfig);
        var protector = new CredentialProtector(config, credRepo, keyring);
        var credMap = new SinkConnectionCredentialMapRepository(dbConfig);
        var tenants = new TenantRepository(dbConfig);

        return new AwsAgentWriteExecutor(
            protector, credMap, tenants, NullLogger<AwsAgentWriteExecutor>.Instance);
    }

    private static async Task<(bool Success, string Message)> Run(string? payload)
        => await BuildExecutor().ExecuteAsync(Guid.NewGuid(), payload, CancellationToken.None);

    // ── (1) size-bounded / empty parse ────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_payload_is_rejected(string? payload)
    {
        var (ok, msg) = await Run(payload);
        Assert.False(ok);
        Assert.Contains("empty payload", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Payload_over_64KB_is_rejected()
    {
        // A syntactically-valid JSON object padded past 64 KB in a string field.
        var big = new string('a', 70 * 1024);
        var payload = "{\"schemaVersion\":1,\"operation\":\"TagUser\",\"tagValue\":\"" + big + "\"}";
        var (ok, msg) = await Run(payload);
        Assert.False(ok);
        Assert.Contains("KB", msg);
    }

    // ── (2) strict JSON ────────────────────────────────────────────────────────

    [Fact]
    public async Task Trailing_comma_is_rejected_strict_json()
    {
        var (ok, msg) = await Run("{\"schemaVersion\":1,\"operation\":\"TagUser\",}");
        Assert.False(ok);
        Assert.Contains("malformed", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Json_with_comments_is_rejected_strict_json()
    {
        var (ok, msg) = await Run("{\"schemaVersion\":1, /* nope */ \"operation\":\"TagUser\"}");
        Assert.False(ok);
        Assert.Contains("malformed", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Garbage_json_is_rejected()
    {
        var (ok, msg) = await Run("this is not json");
        Assert.False(ok);
        Assert.Contains("malformed", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Json_literal_null_deserializes_to_null_and_is_rejected()
    {
        var (ok, msg) = await Run("null");
        Assert.False(ok);
        Assert.Contains("null", msg, StringComparison.OrdinalIgnoreCase);
    }

    // ── (3) schemaVersion ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public async Task SchemaVersion_not_1_is_rejected(int version)
    {
        var (ok, msg) = await Run($"{{\"schemaVersion\":{version},\"operation\":\"TagUser\"}}");
        Assert.False(ok);
        Assert.Contains("schemaVersion", msg);
    }

    [Fact]
    public async Task Missing_schemaVersion_defaults_to_zero_and_is_rejected()
    {
        var (ok, msg) = await Run("{\"operation\":\"TagUser\"}");
        Assert.False(ok);
        Assert.Contains("schemaVersion", msg);
    }

    // ── (4) operation allow-list (closed set) ──────────────────────────────────

    [Theory]
    [InlineData("CreateUser")]      // deliberately deferred / never exposed
    [InlineData("DeleteUser")]
    [InlineData("CreateAccessKey")]
    [InlineData("PutUserPolicy")]
    [InlineData("taguser")]         // wrong case (allow-list is Ordinal)
    [InlineData("")]
    [InlineData("nonsense")]
    public async Task Unknown_or_disallowed_operation_is_rejected(string op)
    {
        var (ok, msg) = await Run($"{{\"schemaVersion\":1,\"operation\":\"{op}\"}}");
        Assert.False(ok);
        Assert.Contains("not allowed", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_operation_is_rejected()
    {
        var (ok, msg) = await Run("{\"schemaVersion\":1}");
        Assert.False(ok);
        Assert.Contains("not allowed", msg, StringComparison.OrdinalIgnoreCase);
    }

    // ── (5) never throws ───────────────────────────────────────────────────────

    [Fact]
    public async Task Executor_never_throws_on_adversarial_inputs()
    {
        // None of these should throw; each must come back as a clean (false, msg).
        var inputs = new[]
        {
            null, "", "{", "{}", "[]", "\"string\"", "12345",
            "{\"schemaVersion\":\"one\",\"operation\":\"TagUser\"}", // schemaVersion wrong type -> JsonException
        };
        foreach (var input in inputs)
        {
            var ex = await Record.ExceptionAsync(() => Run(input));
            Assert.Null(ex);
        }
    }
}
