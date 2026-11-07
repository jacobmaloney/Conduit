using System.Text.Json;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Connectors.IdentityCenter.Tests;

/// <summary>
/// BLANK-CLEARS-01. Granite day 4 (2026-11-06): the rehired Nancy Dawson's HR row came with EndDate emptied; the sink
/// dropped empty values, so IdentityCenter kept the old end date and the nightly job kept her disabled. A field the
/// record carries empty is sent empty; a field it does not carry is not sent at all.
/// </summary>
public class IdentityCenterSinkBlankClearsTests
{
    private const string BaseUrl = "https://ic.example.invalid";

    [Fact]
    public async Task A_field_the_record_carries_empty_is_sent_empty_and_an_absent_one_is_not_sent()
    {
        IdentityCenterTableContext.Sink = "Identities";
        var handler = new CapturingHandler();
        var sink = new IdentityCenterSink(Guid.NewGuid(), new SingleClientHttpFactory(handler),
            new StubCredentialProtector(BaseUrl, "test-key"), NullLogger<IdentityCenterSink>.Instance);

        await sink.UpsertBatchAsync(new[]
        {
            new ConnectorObject
            {
                SourceId = "GC10041", ObjectClass = "user",
                Attributes = new() { ["EmployeeId"] = "GC10041", ["EndDate"] = "", ["Department"] = "Finance", ["JobTitle"] = null }
            }
        }, CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var attrs = JsonCI.Prop(JsonCI.Prop(doc.RootElement, "Items")[0], "Attributes");
        Assert.Equal("", JsonCI.Prop(attrs, "EndDate").GetString());       // cleared in IdentityCenter
        Assert.Equal("", JsonCI.Prop(attrs, "JobTitle").GetString());      // carried, no value: cleared too
        Assert.Equal("Finance", JsonCI.Prop(attrs, "Department").GetString());
        Assert.False(attrs.TryGetProperty("MobilePhone", out _));            // never carried: not sent, nothing cleared
    }
}
