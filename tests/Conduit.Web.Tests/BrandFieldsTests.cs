using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using IdentityCenter.Brand.Components.Inputs;
using IdentityCenter.Brand.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// FIELDS-01. Conduit's form fields are Brand's BrandTextField / BrandNumberField / BrandSelectField: the label is tied
/// to the field, help or an error is read with it, an error outlines it, and the field binds what the raw input bound.
/// The raw fields that remain (table cells, search boxes, values mixing text and code) are held under a ceiling that
/// can only go down.
/// </summary>
public class BrandFieldsTests
{
    // Raw text-like fields left in Conduit after FIELDS-01 (170 before). Lower it when you convert one; never raise it.
    private const int RawFieldCeiling = 47;
    private static readonly Regex RawField = new(@"<input\b(?![^>]*type=""(?:checkbox|radio|hidden|file|color|range|submit|button)"")|<select\b|<textarea\b", RegexOptions.Compiled);

    [Fact]
    public void Raw_form_fields_stay_under_the_ceiling()
    {
        var src = Path.Combine(Root(), "src");
        var files = Directory.EnumerateFiles(src, "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")).ToList();
        Assert.Contains(files, f => f.EndsWith("ConnectedSystems.razor"));   // an empty scan is not a pass
        var perFile = files.Select(f => (File: Path.GetRelativePath(src, f), Count: RawField.Matches(File.ReadAllText(f)).Count)).Where(x => x.Count > 0).ToList();
        var total = perFile.Sum(x => x.Count);
        Assert.True(total <= RawFieldCeiling, $"{total} raw form fields (ceiling {RawFieldCeiling}). Use BrandTextField, BrandNumberField or BrandSelectField:\n"
            + string.Join("\n", perFile.OrderByDescending(x => x.Count).Select(x => $"{x.Count,4}  {x.File}")));
    }

    [Fact]
    public async Task A_text_field_ties_its_label_and_reads_its_help_with_it()
    {
        var html = await Render<BrandTextField>(new() { ["Label"] = "Display name", ["Id"] = "dn", ["Help"] = "As people see it.", ["Required"] = true, ["Value"] = "Ada" });
        Assert.Contains("<label class=\"icb-form-label\" for=\"dn\">Display name<span class=\"icb-form-required\" aria-hidden=\"true\">*</span></label>", html);
        Assert.Matches("<input[^>]*id=\"dn\"[^>]*aria-describedby=\"dn-help\"", html);
        Assert.Contains("<span id=\"dn-help\" class=\"icb-form-help\">As people see it.</span>", html);
        Assert.DoesNotContain("aria-invalid", html);
    }

    [Fact]
    public async Task An_error_outlines_the_field_and_replaces_the_help()
    {
        var html = await Render<BrandTextField>(new() { ["Label"] = "Email", ["Id"] = "em", ["Help"] = "Work address.", ["Error"] = "Enter an email address." });
        Assert.Matches("<input[^>]*aria-invalid=\"true\"[^>]*aria-describedby=\"em-error\"", html);
        Assert.Contains("role=\"alert\">Enter an email address.</span>", html);
        Assert.DoesNotContain("Work address.", html);
    }

    [Fact]
    public async Task Rows_make_a_textarea_and_other_attributes_reach_the_field()
    {
        var html = await Render<BrandTextField>(new() { ["Label"] = "Notes", ["Rows"] = 4, ["aria-label"] = "Notes for the run", ["spellcheck"] = "false" });
        Assert.Matches("<textarea[^>]*aria-label=\"Notes for the run\"[^>]*spellcheck=\"false\"[^>]*class=\"icb-form-input icb-form-textarea\"[^>]*rows=\"4\"", html);
    }

    [Fact]
    public async Task A_text_field_binds_on_change_unless_immediate()
    {
        string? seen = null;
        var field = new BrandTextField { ValueChanged = EventCallback.Factory.Create<string?>(this, v => seen = v) };
        await Invoke(field, "OnInput", "typing");
        Assert.Null(seen);
        await Invoke(field, "OnChange", "done");
        Assert.Equal("done", seen);
        field.Immediate = true;
        await Invoke(field, "OnInput", "each key");
        Assert.Equal("each key", seen);
    }

    [Fact]
    public async Task A_select_binds_a_guid_and_its_placeholder_is_nothing_chosen()
    {
        var id = Guid.NewGuid();
        Guid? seen = Guid.Empty;
        var field = new BrandSelectField<Guid?> { Value = null, ValueChanged = EventCallback.Factory.Create<Guid?>(this, v => seen = v) };
        await Invoke(field, "OnChange", id.ToString());
        Assert.Equal(id, seen);
        await Invoke(field, "OnChange", "");
        Assert.Null(seen);
        var html = await Render<BrandSelectField<Guid>>(new() { ["Label"] = "Source", ["Value"] = id, ["Placeholder"] = "Pick one" });
        Assert.Matches($"<select[^>]*value=\"{id}\"", html);
        Assert.Matches("<select[^>]*><option[^>]*>Pick one</option>", html);   // the first option, standing for nothing chosen
    }

    [Fact]
    public async Task A_number_field_binds_numbers_and_names_text_that_is_not_one()
    {
        int? seen = 5;
        var field = new BrandNumberField<int?> { Value = 5, ValueChanged = EventCallback.Factory.Create<int?>(this, v => seen = v) };
        await Invoke(field, "OnChange", "12");
        Assert.Equal(12, seen);
        await Invoke(field, "OnChange", "");
        Assert.Null(seen);
        await Invoke(field, "OnChange", "twelve");
        Assert.Null(seen);
        Assert.Equal("Enter a number.", typeof(BrandNumberField<int?>).GetField("parseError", Flags)!.GetValue(field));

        var required = new BrandNumberField<int> { Value = 3 };
        await Invoke(required, "OnChange", "");
        Assert.Equal(3, required.Value);   // an int cannot be empty
    }

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static Task Invoke(object component, string method, string value) =>
        (Task)component.GetType().GetMethod(method, Flags)!.Invoke(component, [new ChangeEventArgs { Value = value }])!;

    private static async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    private static string Root([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", ".."));
}
