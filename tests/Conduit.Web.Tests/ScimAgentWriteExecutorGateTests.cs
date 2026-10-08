using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Security;
using Conduit.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// Tests for the DB-INDEPENDENT trust-boundary gates of <see cref="ScimAgentWriteExecutor"/>,
/// on the same honest scope as <see cref="AwsAgentWriteExecutorGateTests"/>: the executor's
/// repositories hit SQL through non-virtual methods, so REAL instances bound to a never-opened
/// connection string exercise only the gates that run BEFORE the first repository call —
/// parse, size cap, schemaVersion, operation allow-list, users-only, the closed attribute map,
/// and the per-operation required fields. Credential resolution and the SCIM HTTP are past that
/// point and are not unit-proven here. Every case also asserts the executor never throws.
/// </summary>
public class ScimAgentWriteExecutorGateTests
{
    private static ScimAgentWriteExecutor BuildExecutor()
    {
        var dbConfig = new DatabaseConfig { ConnectionString = "Server=(local);Database=__none__;Trusted_Connection=False;" };
        var key = Convert.ToBase64String(new byte[32]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Sync:CredentialKey"] = key })
            .Build();

        var credRepo = new ConnectionCredentialRepository(dbConfig);
        var keyring = new CredentialKeyringRepository(dbConfig);
        var protector = new CredentialProtector(config, credRepo, keyring);
        var credMap = new SinkConnectionCredentialMapRepository(dbConfig);
        var tenants = new TenantRepository(dbConfig);
        var httpFactory = new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        return new ScimAgentWriteExecutor(protector, credMap, tenants, httpFactory, NullLoggerFactory.Instance);
    }

    private static async Task<(bool Success, string Message)> Run(string? payload)
        => await BuildExecutor().ExecuteAsync(Guid.NewGuid(), payload, CancellationToken.None);

    private const string Head = "{\"schemaVersion\":1,\"objectClass\":\"user\",\"userExternalId\":\"u-1\",\"sourceConnectionName\":\"scim-prod\"";

    // ── (1) size-bounded / empty / strict parse ────────────────────────────────

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
        var big = new string('a', 70 * 1024);
        var (ok, msg) = await Run(Head + ",\"operation\":\"Disable\",\"managerExternalId\":\"" + big + "\"}");
        Assert.False(ok);
        Assert.Contains("KB", msg);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"operation\":\"Disable\",}")]
    [InlineData("{\"schemaVersion\":1, /* nope */ \"operation\":\"Disable\"}")]
    [InlineData("this is not json")]
    public async Task Malformed_json_is_rejected(string payload)
    {
        var (ok, msg) = await Run(payload);
        Assert.False(ok);
        Assert.Contains("malformed", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Json_literal_null_is_rejected()
    {
        var (ok, msg) = await Run("null");
        Assert.False(ok);
        Assert.Contains("null", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Wrong_schemaVersion_is_rejected(int version)
    {
        var (ok, msg) = await Run("{\"schemaVersion\":" + version + ",\"objectClass\":\"user\",\"operation\":\"Disable\",\"userExternalId\":\"u-1\"}");
        Assert.False(ok);
        Assert.Contains("schemaVersion", msg);
    }

    // ── (2) operation allow-list ───────────────────────────────────────────────

    [Theory]
    [InlineData("Delete")]
    [InlineData("CreateUser")]
    [InlineData("AddGroupMember")]
    [InlineData("")]
    public async Task Operation_outside_the_closed_set_is_rejected(string operation)
    {
        var (ok, msg) = await Run(Head + ",\"operation\":\"" + operation + "\"}");
        Assert.False(ok);
        Assert.Contains("not allowed", msg);
    }

    // ── (4) users only ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("group")]
    [InlineData("Group")]
    [InlineData("")]
    public async Task A_non_user_object_class_is_refused(string objectClass)
    {
        var (ok, msg) = await Run("{\"schemaVersion\":1,\"objectClass\":\"" + objectClass + "\",\"operation\":\"Disable\",\"userExternalId\":\"u-1\"}");
        Assert.False(ok);
        Assert.Contains("only 'user'", msg);
    }

    [Fact]
    public async Task A_missing_user_id_is_refused()
    {
        var (ok, msg) = await Run("{\"schemaVersion\":1,\"objectClass\":\"user\",\"operation\":\"Disable\"}");
        Assert.False(ok);
        Assert.Contains("userExternalId", msg);
    }

    // ── (3) the closed attribute map ───────────────────────────────────────────

    [Fact]
    public async Task An_unmapped_attribute_refuses_the_whole_command_by_name()
    {
        var (ok, msg) = await Run(Head + ",\"operation\":\"SetAttributes\",\"attributes\":{\"department\":\"Finance\",\"extensionAttribute7\":\"x\"}}");
        Assert.False(ok);
        Assert.Contains("extensionAttribute7", msg);
        Assert.Contains("nothing was written", msg);
    }

    [Fact]
    public async Task SetAttributes_without_attributes_is_refused()
    {
        var (ok, msg) = await Run(Head + ",\"operation\":\"SetAttributes\"}");
        Assert.False(ok);
        Assert.Contains("no attributes", msg);
    }

    [Fact]
    public async Task SetManager_without_a_manager_is_refused()
    {
        var (ok, msg) = await Run(Head + ",\"operation\":\"SetManager\"}");
        Assert.False(ok);
        Assert.Contains("managerExternalId", msg);
    }

    [Theory]
    [InlineData("givenName", "name.givenName")]
    [InlineData("sn", "name.familyName")]
    [InlineData("mail", "emails[type eq \"work\"].value")]
    [InlineData("department", "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:department")]
    [InlineData("employeeID", "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:employeeNumber")]
    [InlineData("DisplayName", "displayName")]   // case-insensitive on the directory side
    public void Known_attributes_map_to_scim_paths(string directory, string expected)
    {
        Assert.True(ScimAttributePaths.TryMap(directory, out var path));
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("userName")]           // renaming the login is not a lifecycle attribute write
    [InlineData("password")]
    [InlineData("extensionAttribute1")]
    [InlineData(null)]
    public void Unlisted_attributes_do_not_map(string? directory)
    {
        Assert.False(ScimAttributePaths.TryMap(directory, out _));
    }

    // ── A valid command reaches the credential step and fails closed there (no DB) ──

    [Fact]
    public async Task A_valid_command_whose_credential_lookup_faults_fails_closed_and_never_throws()
    {
        // The bogus connection string makes the first repository call throw. The executor must turn
        // that into a failed command: an escape skips the completion callback and leaves the command
        // claimed with no outcome.
        var (ok, msg) = await Run(Head + ",\"operation\":\"Disable\"}");
        Assert.False(ok);
        Assert.Contains("could not resolve the credential mapping", msg);
    }
}
