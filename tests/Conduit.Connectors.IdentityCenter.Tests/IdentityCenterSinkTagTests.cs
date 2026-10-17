using System.Text.Json;
using Conduit.Sync.Connectors;
using Conduit.Sync.Orchestration;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Connectors.IdentityCenter.Tests;

/// <summary>
/// UC009-F4 on the IC sink. The Mapping step's tags travel as the "_tags" pseudo-attribute
/// the orchestrator stamps; the sink must (a) lift them into the typed <c>Tags[]</c> on the
/// bulk item and keep "_tags" out of the attribute bag, and (b) read the tag outcome IC
/// now returns (<c>tagsApplied</c> / <c>unknownTags</c> / <c>tagError</c>) and say in the
/// RUN LOG what happened, while the per-item results still stand. Before this, a step whose
/// tag did not exist in IC reported "updated" and nothing else, anywhere.
/// Same no-live-IC harness as <see cref="IdentityCenterSinkDispatchTests"/>: a canned
/// handler; the run log is captured through the same ambient appender the orchestrator
/// binds for a run.
/// </summary>
public class IdentityCenterSinkTagTests
{
    private const string BaseUrl = "https://api.certification-center.com";

    private static IdentityCenterSink BuildSink(CapturingHandler handler)
    {
        IdentityCenterTableContext.Sink = "Objects";
        var factory = new SingleClientHttpFactory(handler);
        var protector = new StubCredentialProtector(BaseUrl, "test-key");
        return new IdentityCenterSink(Guid.NewGuid(), factory, protector, NullLogger<IdentityCenterSink>.Instance);
    }

    private static ConnectorObject TaggedUser(string? tagsCsv) => new()
    {
        SourceId = "src-1",
        ObjectClass = "user",
        Attributes = tagsCsv is null
            ? new() { ["displayName"] = "Geordi La Forge" }
            : new() { ["displayName"] = "Geordi La Forge", ["_tags"] = tagsCsv }
    };

    private static string Response(int applied, string[]? unknown = null, string? error = null) =>
        JsonSerializer.Serialize(new
        {
            batchId = Guid.NewGuid(),
            results = new[] { new { sourceUniqueId = "src-1", outcome = "Updated", errorMessage = (string?)null } },
            tagsApplied = applied,
            unknownTags = unknown ?? Array.Empty<string>(),
            tagError = error
        });

    /// <summary>Captures what the sink appends to the run log for the duration of one test.</summary>
    private static List<(string Level, string Message)> CaptureRunLog()
    {
        var lines = new List<(string, string)>();
        SourceRunLogContext.Append = (level, message) => { lines.Add((level, message)); return Task.CompletedTask; };
        return lines;
    }

    [Fact]
    public async Task The_step_tags_ride_the_item_as_Tags_and_never_as_an_attribute()
    {
        var handler = new CapturingHandler(Response(1));
        var sink = BuildSink(handler);
        CaptureRunLog();

        await sink.UpsertBatchAsync(new[] { TaggedUser("X") }, CancellationToken.None);

        Assert.NotNull(handler.LastBody);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var item = JsonCI.Prop(doc.RootElement, "Items")[0];
        var tags = JsonCI.Prop(item, "Tags").EnumerateArray().Select(t => t.GetString()).ToArray();
        Assert.Equal(new[] { "X" }, tags);

        var attrs = JsonCI.Prop(item, "Attributes");
        Assert.False(attrs.EnumerateObject().Any(p => p.Name.Equals("_tags", StringComparison.OrdinalIgnoreCase)),
            "\"_tags\" is plumbing and must not reach IC's ObjectAttributes.");
    }

    [Fact]
    public async Task An_applied_count_is_written_to_the_run_log_and_the_upserts_stand()
    {
        var handler = new CapturingHandler(Response(1));
        var sink = BuildSink(handler);
        var log = CaptureRunLog();

        var results = await sink.UpsertBatchAsync(new[] { TaggedUser("X") }, CancellationToken.None);

        Assert.Equal(SinkWriteOutcome.Updated, results[0].Outcome);
        Assert.Contains(log, l => l.Level == "Info" && l.Message == "    Tags: applied 1");
        Assert.DoesNotContain(log, l => l.Level == "Warning");
    }

    [Fact]
    public async Task Unknown_names_are_a_Warning_naming_them_verbatim_and_the_upserts_stand()
    {
        var handler = new CapturingHandler(Response(0, unknown: new[] { "Nope", "Also-Nope" }));
        var sink = BuildSink(handler);
        var log = CaptureRunLog();

        var results = await sink.UpsertBatchAsync(new[] { TaggedUser("Nope,Also-Nope") }, CancellationToken.None);

        // The batch result is unchanged: objects were upserted, only the enrichment is missing.
        Assert.Equal(SinkWriteOutcome.Updated, results[0].Outcome);
        Assert.Contains(log, l => l.Level == "Info" && l.Message == "    Tags: applied 0");
        Assert.Contains(log, l => l.Level == "Warning"
            && l.Message == "    Tags: unknown in IdentityCenter: Nope, Also-Nope (create them there first)");
    }

    [Fact]
    public async Task A_flush_error_is_a_Warning_carrying_ICs_message()
    {
        var handler = new CapturingHandler(Response(0, error: "Invalid object name 'ObjectTags'."));
        var sink = BuildSink(handler);
        var log = CaptureRunLog();

        var results = await sink.UpsertBatchAsync(new[] { TaggedUser("X") }, CancellationToken.None);

        Assert.Equal(SinkWriteOutcome.Updated, results[0].Outcome);
        Assert.Contains(log, l => l.Level == "Warning"
            && l.Message == "    Tags: IdentityCenter could not apply tags: Invalid object name 'ObjectTags'.");
    }

    [Fact]
    public async Task An_IC_without_the_outcome_fields_is_named_rather_than_trusted()
    {
        // The pre-UC009-F4 response shape: results only. Silence here was the original defect.
        var handler = new CapturingHandler("{\"results\":[{\"sourceUniqueId\":\"src-1\",\"outcome\":\"Updated\"}]}");
        var sink = BuildSink(handler);
        var log = CaptureRunLog();

        await sink.UpsertBatchAsync(new[] { TaggedUser("X") }, CancellationToken.None);

        Assert.Contains(log, l => l.Level == "Warning" && l.Message.Contains("did not report a tag outcome", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_batch_without_tags_says_nothing_about_tags()
    {
        var handler = new CapturingHandler(Response(0));
        var sink = BuildSink(handler);
        var log = CaptureRunLog();

        await sink.UpsertBatchAsync(new[] { TaggedUser(null) }, CancellationToken.None);

        Assert.DoesNotContain(log, l => l.Message.Contains("Tags:", StringComparison.Ordinal));
    }
}
