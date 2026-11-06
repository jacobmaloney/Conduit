using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Conduit.Readers.Csv;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// NO-BROWSER-DIALOGS-01. The browser's own confirm / alert / prompt boxes are not Brand: they cannot be styled, they
/// block every other tab's script while open, and a confirm opened from a stacked dialog answers a question the
/// operator cannot see the context of. Every question is Brand's BrandConfirmationModal and every outcome a BrandNotice.
/// </summary>
public class NoBrowserDialogsTests
{
    private static readonly Regex BrowserDialog = new(
        @"Invoke(?:Void)?Async(?:<[^>]+>)?\(\s*""(?:window\.)?(?:confirm|alert|prompt)""|\bwindow\.(?:confirm|alert|prompt)\s*\(|(?<![\w.])(?:confirm|alert|prompt)\s*\(\s*['""`]",
        RegexOptions.Compiled);

    [Fact]
    public void No_page_or_script_opens_a_browser_dialog()
    {
        var src = Path.GetFullPath(Path.Combine(Root(), "src"));
        var files = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor") || f.EndsWith(".cs") || f.EndsWith(".js") || f.EndsWith(".cshtml"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.Contains(files, f => f.EndsWith("SyncProjects.razor"));   // an empty scan is not a pass
        Assert.True(files.Count > 300, $"Scanned only {files.Count} files under {src}");
        var hits = files
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => BrowserDialog.IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(src, x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();
        Assert.True(hits.Count == 0, "Use BrandConfirmationModal or BrandNotice instead of a browser dialog:\n" + string.Join("\n", hits));
    }

    [Theory]
    [InlineData("await JS.InvokeAsync<bool>(\"confirm\", text);")]
    [InlineData("await JSRuntime.InvokeVoidAsync(\"alert\", \"x\");")]
    [InlineData("if (!window.confirm('Delete?')) return;")]
    [InlineData("alert(\"done\");")]
    public void The_guard_sees_each_way_a_dialog_is_opened(string line) => Assert.Matches(BrowserDialog, line);

    [Theory]
    [InlineData("<div class=\"icb-notice\" role=\"alert\">")]
    [InlineData("var confirmed = await confirmModal!.ShowAsync(\"Deactivate?\", text);")]
    [InlineData("private string _confirm = \"\";")]
    public void The_guard_leaves_brand_dialogs_and_alert_roles_alone(string line) => Assert.DoesNotMatch(BrowserDialog, line);

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData(null, "")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData(" padded", "\" padded\"")]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("+1 555", "'+1 555")]
    [InlineData("-5", "'-5")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    public void A_csv_field_is_quoted_and_a_formula_is_written_as_text(string? value, string expected) =>
        Assert.Equal(expected, CsvText.Field(value));

    [Fact]
    public void A_csv_document_is_header_then_rows_in_crlf_lines_the_reader_reads_back()
    {
        var text = CsvText.Document(["Name", "Note"], [["Ada", "a,b"], ["Grace", null]]);
        Assert.Equal("Name,Note\r\nAda,\"a,b\"\r\nGrace,\r\n", text);
    }

    [Fact]
    public void The_audit_export_is_a_real_csv_link_not_a_placeholder()
    {
        var page = File.ReadAllText(Path.Combine(Root(), "src", "Conduit.Web", "Pages", "AuditLog.razor"));
        Assert.Contains("CsvText.Document(", page);
        Assert.Contains("data:text/csv", page);
        Assert.DoesNotContain("would be implemented", page);
    }

    private static string Root([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", ".."));
}
