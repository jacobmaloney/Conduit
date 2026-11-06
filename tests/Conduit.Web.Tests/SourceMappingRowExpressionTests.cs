using System.Reflection;
using Conduit.SourceBrowsing;
using IdentityCenter.Brand.Components.Modals;
using IdentityCenter.Brand.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// MAP-EDITOR-SOURCE-01. The source browser's field mappings are the shared BrandAttributeMapEditor, one row per rule
/// written as "{source} | steps" in the host's own grammar. A chain host is exactly MappingRowExpression; an operation
/// host (IC) reads one operation and a default placed where that host applies it, so the order on screen is the order
/// that runs. Every expression the host cannot run is named, never sent.
/// </summary>
public class SourceMappingRowExpressionTests
{
    private static readonly SourceMappingContext Chain = new("Chain", "", true, false, true, [], []);
    private static readonly SourceMappingContext Directory = new("Directory", "", false, true, false, ["Direct", "ToUpper", "ToLower", "Trim"], []);
    private static readonly SourceMappingContext Hr = new("HR", "", false, true, true, ["Direct", "Uppercase", "Lowercase", "TitleCase", "Trim", "DateParse"], [], DefaultBeforeTransform: true);

    [Theory]
    [InlineData("chain", "sAMAccountName", "trim | lower", null, "{sAMAccountName} | trim | lower")]
    [InlineData("chain", "", "const:Contractor", null, "const:Contractor")]
    [InlineData("directory", "department", "ToUpper", "Unassigned", "{department} | ToUpper | default:Unassigned")]
    [InlineData("directory", "mail", "", null, "{mail}")]
    [InlineData("directory", "title", "", "", "{title} | default:")]
    [InlineData("hr", "department", "Uppercase", "Unassigned", "{department} | default:Unassigned | Uppercase")]
    [InlineData("hr", "startDate", "DateParse", null, "{startDate} | DateParse")]
    public void A_saved_rule_reads_as_one_expression_and_parses_back_to_itself(string host, string source, string transformation, string? defaultValue, string expression)
    {
        var context = Host(host);
        Assert.Equal(expression, SourceMappingRowExpression.Format(context, source, transformation, defaultValue));
        Assert.Equal((source, transformation, defaultValue), SourceMappingRowExpression.Parse(context, expression));
        Assert.Null(SourceMappingRowExpression.Problem(context, expression));
    }

    [Fact]
    public void Direct_is_the_plain_copy_and_an_operation_takes_the_hosts_spelling()
    {
        Assert.Equal("{mail}", SourceMappingRowExpression.Format(Directory, "mail", "Direct", null));
        Assert.Equal(("mail", "ToUpper", (string?)null), SourceMappingRowExpression.Parse(Directory, "{mail} | toupper"));
    }

    [Theory]
    [InlineData("directory", "{mail} | shout", "shout is not an operation here")]
    [InlineData("directory", "{mail} | ToUpper | Trim", "one operation per field")]
    [InlineData("directory", "{mail} | default:x | ToUpper", "Write default: last")]
    [InlineData("hr", "{mail} | Uppercase | default:x", "Write default: before Uppercase")]
    [InlineData("hr", "{mail} | default:a | default:b", "default: once")]
    [InlineData("directory", "const:Contractor", "Start with the source field")]
    [InlineData("directory", "{mail", "Close the source attribute")]
    [InlineData("chain", "{mail} | shout", "Unknown transformation")]
    public void A_row_the_host_cannot_run_is_named(string host, string expression, string expected) =>
        Assert.Contains(expected, SourceMappingRowExpression.Problem(Host(host), expression));

    [Fact]
    public void An_empty_row_is_not_a_problem_yet()
    {
        Assert.Null(SourceMappingRowExpression.Problem(Directory, ""));
        Assert.Null(Conduit.Mapping.MappingRowExpression.Problem("  "));
    }

    [Fact]
    public async Task The_browser_shows_the_shared_editor_with_the_hosts_rows_and_no_local_cards()
    {
        var html = await Render(Context(Grammar(Directory), [new("department", "Department", "ToUpper", "Unassigned")]));
        Assert.Contains("icb-map-editor", html);
        Assert.Contains("{department} | ToUpper | default:Unassigned", html);
        Assert.DoesNotContain("icb-source-mapping-rule", html);
        Assert.DoesNotContain("icb-source-notice", html);
        Assert.Contains("icb-notice", html);
    }

    [Fact]
    public void An_edited_row_is_read_by_the_host_grammar_and_an_untouched_row_is_sent_as_saved()
    {
        using var component = new BrandSourceBrowser();
        var context = Context(Grammar(Hr), [new("dept", "Department", "uppercase", "None", Id: Guid.NewGuid()), new("given", "FirstName", "Trim")]);
        typeof(BrandSourceBrowser).GetField("context", Flags)!.SetValue(component, context);
        Call(component, "ResetMappings");
        var rows = (List<BrandAttributeMapRow>)Field(component, "editorRows")!;
        rows[1].Expression = "{givenName} | default:Unknown | TitleCase";
        rows[1].Required = true;
        Call(component, "OnMappingRowsChanged", rows);
        var rules = (BrandMappingRule[])Call(component, "Rules")!;
        Assert.Equal(context.Mapping!.SavedRules[0], rules[0]);   // still "uppercase", as saved
        Assert.Equal(new BrandMappingRule("givenName", "FirstName", "TitleCase", "Unknown", true), rules[1]);
    }

    [Fact]
    public void A_row_the_host_cannot_run_blocks_the_preview()
    {
        using var component = new BrandSourceBrowser();
        typeof(BrandSourceBrowser).GetField("context", Flags)!.SetValue(component, Context(Grammar(Directory), [new("mail", "Email", "ToUpper")]));
        Call(component, "ResetMappings");
        var rows = (List<BrandAttributeMapRow>)Field(component, "editorRows")!;
        rows[0].Expression = "{mail} | shout";
        Call(component, "OnMappingRowsChanged", rows);
        Assert.True((bool)typeof(BrandSourceBrowser).GetProperty("HasRowProblems", Flags)!.GetValue(component)!);
    }

    private static SourceMappingContext Host(string name) => name switch { "chain" => Chain, "hr" => Hr, _ => Directory };

    // The same adapter both hosts' SourceDataBrowser wrappers pass.
    private static BrandMappingRowGrammar Grammar(SourceMappingContext m) => new(
        r => SourceMappingRowExpression.Format(m, r.Source, r.Transformation, r.DefaultValue),
        e => SourceMappingRowExpression.Parse(m, e) is var (source, transformation, value) ? new BrandMappingRowParts(source, transformation, value) : default,
        e => SourceMappingRowExpression.Problem(m, e), SourceMappingRowExpression.Hint(m), SourceMappingRowExpression.Placeholder(m));

    private static BrandSourceContext Context(BrandMappingRowGrammar grammar, IReadOnlyList<BrandMappingRule> saved) => new("Example AD", "Conduit test host", "Example project",
        Guid.NewGuid(), "User", ["User"], false, true, null, [], [],
        new("Fixture", "Fixture help", false, true, true, ["Direct", "ToUpper"], saved, Grammar: grammar));

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static object? Field(object obj, string name) => obj.GetType().GetField(name, Flags)!.GetValue(obj);
    private static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Flags)!.Invoke(obj, args);

    private static async Task<string> Render(BrandSourceContext context)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        Func<Guid?, CancellationToken, Task<BrandSourceContext>> describe = (_, _) => Task.FromResult(context);
        Func<BrandSourceQuery, CancellationToken, Task<BrandSourcePage>> read = (_, _) => throw new Exception("No reads");
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<BrandSourceBrowser>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["Describe"] = describe, ["Read"] = read }))).ToHtmlString());
    }
}
