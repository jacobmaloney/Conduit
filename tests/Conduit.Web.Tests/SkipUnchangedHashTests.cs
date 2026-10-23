using System.Reflection;
using Conduit.Sync.Connectors;
using Conduit.Sync.Orchestration;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// SYNC-HASH-01 (Jacob 10-22, "every run updates ~1,356 objects"). Measured on the lab run log: the full
/// "user upsert" step and the BaseDN-scoped "user upsert Contractor" step (which stamps _tags) shared one
/// skip-unchanged slot per user, so each found the other's hash for the same 448 contractors and both
/// rewrote them every hour. And the hash wrote a multi-valued value as its type name, so a change to only
/// proxyAddresses was skipped as unchanged. ComputeContentHash is private; it is called through reflection.
/// </summary>
public class SkipUnchangedHashTests
{
    [Fact]
    public void A_partial_scope_step_keeps_its_own_hash_scope_and_a_full_step_keeps_the_class()
    {
        var step = Guid.Parse("3f2b8a51-0c1d-4e5f-9a7b-112233445566");
        Assert.Equal("user", SyncProjectOrchestrator.HashScopeKey("user", null));
        var key = SyncProjectOrchestrator.HashScopeKey("computer", step);
        Assert.Equal("step:3f2b8a510c1d4e5f9a7b112233445566", key);
        Assert.True(key.Length <= 50, "fits SinkRecordHashes.ObjectClass NVARCHAR(50)");
    }

    [Fact]
    public void A_multi_valued_change_moves_the_hash()
    {
        var before = Hash(new() { ["proxyAddresses"] = new List<string> { "SMTP:a@x.test" } });
        var after = Hash(new() { ["proxyAddresses"] = new List<string> { "SMTP:a@x.test", "smtp:b@x.test" } });
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void A_byte_value_change_moves_the_hash()
    {
        Assert.NotEqual(Hash(new() { ["thumbnailPhoto"] = new byte[] { 1, 2 } }),
                        Hash(new() { ["thumbnailPhoto"] = new byte[] { 1, 3 } }));
    }

    [Fact]
    public void A_reordered_multi_value_is_not_a_change()
    {
        Assert.Equal(Hash(new() { ["proxyAddresses"] = new List<string> { "a", "b" } }),
                     Hash(new() { ["proxyAddresses"] = new object[] { "b", "a" } }));
    }

    [Fact]
    public void Scalar_values_hash_exactly_as_before_so_unchanged_records_stay_skipped()
    {
        // The canonical form for strings and nulls is unchanged: class, then key 0x1F value 0x1E.
        var expected = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("user\x1e" + "cn\x1f" + "Amanda\x1e" + "mail\x1f" + "\x00\x1e")));
        Assert.Equal(expected, Hash(new() { ["cn"] = "Amanda", ["mail"] = null }));
    }

    private static string Hash(Dictionary<string, object?> attributes)
    {
        var compute = typeof(SyncProjectOrchestrator).GetMethod("ComputeContentHash", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)compute.Invoke(null, [new ConnectorObject { SourceId = "x", ObjectClass = "user", Attributes = attributes }, null])!;
    }
}
