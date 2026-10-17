using System.Reflection;
using System.Runtime.CompilerServices;
using Conduit.Sync.Orchestration;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// UC009-F4 on the orchestrator. A Mapping step can carry <c>{"tags":["Contractor"]}</c>
/// in its Configuration; before this the run log never said which tags were parsed, so a
/// step whose tags never reached IdentityCenter looked like any other step.
///
/// The orchestrator cannot be constructed in a unit test (nine concrete repositories and a
/// connector registry, no in-memory doubles), so this is pinned in two halves: the parser
/// is called through reflection on the real private method, and the log statement that
/// follows the parse is pinned by source, format included, in the same way
/// <see cref="ScheduledConnectionStateTests"/> pins that file.
/// </summary>
public class SyncStepTagLogTests
{
    [Theory]
    [InlineData("{\"tags\":[\"X\"]}", "X")]
    [InlineData("{\"tags\":[\"Contractor\",\" SOX \",\"contractor\"]}", "Contractor,SOX")]
    [InlineData("{\"tags\":\"Contractor, SOX\"}", "Contractor,SOX")]
    [InlineData("{\"other\":1}", "")]
    [InlineData("not json", "")]
    [InlineData(null, "")]
    public void The_step_configuration_yields_the_tag_names_the_log_line_will_name(string? configuration, string expectedCsv)
    {
        var parse = typeof(SyncProjectOrchestrator).GetMethod("ParseStepTagNames",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(parse);

        var names = (List<string>)parse!.Invoke(null, new object?[] { configuration })!;

        Assert.Equal(expectedCsv, string.Join(",", names));
    }

    [Fact]
    public void The_run_log_names_the_parsed_tags_right_after_the_parse_in_the_two_exact_forms()
    {
        var source = Read("src/Conduit.Sync/Orchestration/SyncProjectOrchestrator.cs");
        var pump = Between(source, "var stepTagNames = ParseStepTagNames(step?.Configuration);",
            "// Recover the prior incremental cursor");

        // One Info line into the RUN log (Log(run, ...) is SyncRunRepository.AppendLogAsync),
        // only when a step exists (the legacy no-step path has no name to log).
        Assert.Contains("if (step is not null)", pump);
        Assert.Contains("await Log(run, \"Info\", stepTagNames.Count > 0", pump);
        Assert.Contains("$\"    Step '{step.Name}': tags = {string.Join(\", \", stepTagNames)}\"", pump);
        Assert.Contains("$\"    Step '{step.Name}': no tags\"", pump);
    }

    [Fact]
    public void The_log_line_is_followed_by_the_stamp_the_sink_lifts()
    {
        // The line says "tags = X"; the stamp is what makes X reach IC. Both or neither.
        var source = Read("src/Conduit.Sync/Orchestration/SyncProjectOrchestrator.cs");
        Assert.Contains("sinkObj.Attributes[\"_tags\"] = stepTagsCsv;", source);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(RepoFile(relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Start marker not found: {start}");
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"End marker not found after start: {end}");
        return source[from..to];
    }
}
