using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Conduit.Mapping;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// MAP-EDITOR-CONDUIT-01. The step Mappings dialog shows each row in the shared BrandAttributeMapEditor as one
/// expression, "{source} | step | step". The expression must round-trip the saved row exactly (source attribute +
/// transform chain), so the editor shows what the sync runs and an untouched row saves unchanged; and every problem the
/// operator can type is named, never run.
/// </summary>
public class MappingRowExpressionTests
{
    [Theory]
    [InlineData("sAMAccountName", "trim | lower", "{sAMAccountName} | trim | lower")]
    [InlineData("mail", "", "{mail}")]
    [InlineData("mail", null, "{mail}")]
    [InlineData("department", "default:Unassigned", "{department} | default:Unassigned")]
    [InlineData("", "const:Contractor", "const:Contractor")]
    [InlineData(null, "const:Contractor", "const:Contractor")]
    [InlineData("", "", "")]
    public void A_saved_row_reads_as_one_expression_and_parses_back_to_itself(string? source, string? transform, string expression)
    {
        Assert.Equal(expression, MappingRowExpression.Format(source, transform));
        var (parsedSource, parsedTransform) = MappingRowExpression.Parse(expression);
        Assert.Equal(source ?? string.Empty, parsedSource);
        Assert.Equal(transform ?? string.Empty, parsedTransform);
        Assert.Null(MappingRowExpression.Problem(expression));
    }

    [Theory]
    [InlineData("{mail}|lower", "mail", "lower")]
    [InlineData("  { mail }  |  trim|lower ", "mail", "trim|lower")]
    [InlineData("{sAMAcc", "sAMAcc", "")]   // still being typed
    public void Loose_spacing_and_half_typed_text_parse_to_what_runs(string expression, string source, string transform) =>
        Assert.Equal((source, transform), MappingRowExpression.Parse(expression));

    [Theory]
    [InlineData("{sAMAcc", "Close the source attribute")]
    [InlineData("{ } | lower", "Name the source attribute")]
    [InlineData("{mail} lower", "Separate the source attribute")]
    [InlineData("{givenName} | prefix:{sn}", "one source attribute")]
    [InlineData("{mail} | shout", "Unknown transformation")]
    [InlineData("{mail} | substring:x", "substring:start")]
    [InlineData("lower", "Start with the source attribute")]
    [InlineData("Contractor", "Unknown transformation")]
    public void A_row_the_sync_cannot_run_is_named(string expression, string expected) =>
        Assert.Contains(expected, MappingRowExpression.Problem(expression));

    [Fact]
    public void The_step_Mappings_dialog_is_the_shared_editor_not_a_local_table()
    {
        var page = File.ReadAllText(Path.Combine(WebRoot(), "Pages", "Sync", "SyncProjects.razor"));
        Assert.Contains("<BrandAttributeMapEditor", page);
        Assert.Contains("ExpressionProblem=\"Conduit.Mapping.MappingRowExpression.Problem\"", page);
        Assert.DoesNotContain("mapping-modal-table", page);
        Assert.DoesNotContain("SetMappingMode", page);
    }

    private static string WebRoot([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "src", "Conduit.Web"));
}
