using System.Text.Json;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Connectors.IdentityCenter.Tests;

/// <summary>
/// Slice 2 license-path contract on the IC sink:
///   (a) every /api/objects/licenses/bulk payload carries SyncStartedAt, constant
///       across the batches of one run (one sink instance = one run);
///   (b) a 200 whose usersResolved is 0 while rows were sent is a FAILED batch —
///       nothing attached, and Ok there was the false success this fixes;
///   (c) partial resolution (usersResolved &gt; 0, usersUnresolved &gt; 0) stays Ok.
/// Same no-live-IC harness as IdentityCenterSinkDispatchTests: a canned handler,
/// assertions on the request the sink built and on the result mapping.
/// </summary>
public class IdentityCenterSinkLicenseTests
{
    private const string BaseUrl = "https://api.certification-center.com";

    private static IdentityCenterSink BuildSink(CapturingHandler handler)
    {
        IdentityCenterTableContext.Sink = "Objects";
        var factory = new SingleClientHttpFactory(handler);
        var protector = new StubCredentialProtector(BaseUrl, "test-key");
        return new IdentityCenterSink(Guid.NewGuid(), factory, protector, NullLogger<IdentityCenterSink>.Instance);
    }

    private static ConnectorObject LicenseObj(string upn) => new()
    {
        SourceId = $"user-{upn}:sku-1",
        ObjectClass = "license",
        Attributes = new()
        {
            ["objectClass"] = "license",
            ["SkuId"] = "sku-1",
            ["SkuName"] = "E5",
            ["TotalUnits"] = "10",
            ["ConsumedUnits"] = "5",
            ["UserPrincipalName"] = upn,
            ["UserSourceUniqueId"] = "user-" + upn,
            ["AssignmentSource"] = "Direct",
            ["_sourceConnection"] = "domain.local2"
        }
    };

    private static string ResolvedResponse(int resolved, int unresolved) =>
        $"{{\"batchId\":\"{Guid.NewGuid()}\",\"poolsUpserted\":1,\"usersResolved\":{resolved},\"usersUnresolved\":{unresolved},\"assignmentsPersisted\":{resolved},\"staleDeactivated\":0}}";

    [Fact]
    public async Task Every_license_payload_carries_a_SyncStartedAt_constant_across_batches()
    {
        var handler = new CapturingHandler(ResolvedResponse(1, 0));
        var sink = BuildSink(handler);

        var before = DateTime.UtcNow;
        await sink.UpsertBatchAsync(new[] { LicenseObj("a@x.y") }, CancellationToken.None);
        var first = ReadSyncStartedAt(handler.LastBody!);

        await Task.Delay(50);
        await sink.UpsertBatchAsync(new[] { LicenseObj("b@x.y") }, CancellationToken.None);
        var second = ReadSyncStartedAt(handler.LastBody!);

        Assert.NotNull(first);
        Assert.Equal(first, second); // constant across the run's batches
        Assert.InRange(DateTime.Parse(first!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
            before.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task A_200_with_usersResolved_zero_is_a_failed_batch()
    {
        var handler = new CapturingHandler(ResolvedResponse(0, 2));
        var sink = BuildSink(handler);

        var results = await sink.UpsertBatchAsync(
            new[] { LicenseObj("a@x.y"), LicenseObj("b@x.y") }, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal(SinkWriteOutcome.Failed, r.Outcome);
            Assert.Contains("resolved 0", r.ErrorMessage);
        });
    }

    [Fact]
    public async Task Partial_resolution_stays_Ok()
    {
        var handler = new CapturingHandler(ResolvedResponse(1, 1));
        var sink = BuildSink(handler);

        var results = await sink.UpsertBatchAsync(
            new[] { LicenseObj("a@x.y"), LicenseObj("b@x.y") }, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(SinkWriteOutcome.Updated, r.Outcome));
    }

    private static string? ReadSyncStartedAt(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var prop = JsonCI.Prop(doc.RootElement, "SyncStartedAt");
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }
}
