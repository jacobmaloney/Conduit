using System.Runtime.CompilerServices;
using Conduit.Core.SyncModels;
using Conduit.Mapping;
using Conduit.Sync.Connectors;
using Conduit.Sync.Orchestration;
using IdentityCenter.Brand.Models;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// One value grammar for both products, and a point-and-click builder that writes it. Jacob, 2026-11-24, having
/// typed a fixed value into a sync mapping and been told "Unknown transformation": "you can say FirstName first
/// 3 letters, or last 4 letters, for example, point and click ... a built rule like 'His name is ' + givenName +
/// ' ' + sn + '.' ... plus I can update the text".
/// </summary>
public class OneValueGrammarTests
{
    private static ValuePatternEngine.PatternContext Person(params (string Name, string? Value)[] fields) =>
        new(fields.ToDictionary(f => f.Name, f => f.Value, StringComparer.OrdinalIgnoreCase), Now: new DateTime(2026, 11, 24));

    private static string Render(string pattern, params (string Name, string? Value)[] fields) =>
        ValuePatternEngine.Candidate(pattern, Person(fields), null, 0).Value;

    [Fact]
    public void A_field_can_be_cut_to_its_first_or_last_characters()
    {
        Assert.Equal("Paul", Render("{FirstName:4}", ("FirstName", "Paula")));
        Assert.Equal("0042", Render("{EmployeeId:-4}", ("EmployeeId", "GC10042")));
        Assert.Equal("Al", Render("{FirstName:4}", ("FirstName", "Al")));
        Assert.Equal("Alxx", Render("{FirstName:4,x}", ("FirstName", "Al")));
        Assert.Equal("0007", Render("{EmployeeId:-4,0}", ("EmployeeId", "7")));
        Assert.Equal("7", Render("{EmployeeId:-4}", ("EmployeeId", "7")));
        Assert.Single(ValuePatternEngine.Candidate("{FirstName:0}", Person(("FirstName", "Al")), null, 0).UnknownTokens);
    }

    [Fact]
    public void A_sentence_is_written_from_text_and_fields()
    {
        Assert.Equal("His name is Paul Ortiz.", Render("His name is {givenName} {sn}.", ("givenName", "Paul"), ("sn", "Ortiz")));
        Assert.Equal(new[] { "givenName", "sn" }, ValuePatternEngine.SourceAttributes("His name is {givenName} {upper(sn)}.").OrderBy(n => n));
    }

    [Theory]
    [InlineData("{mail}", true)]
    [InlineData("{mail} | trim | lower", true)]
    [InlineData("const:Contractor", true)]
    [InlineData("", true)]
    [InlineData("{mail", true)]                       // still being typed
    [InlineData("Employee", false)]                   // plain text is a fixed value
    [InlineData("{givenName} {sn}", false)]
    [InlineData("{givenName:4}", false)]
    [InlineData("{upper(sn)}", false)]
    [InlineData("His name is {givenName}.", false)]
    public void The_older_step_form_is_told_from_a_pattern(string expression, bool stepForm)
    {
        Assert.Equal(stepForm, MappingRowExpression.IsStepForm(expression));
    }

    [Fact]
    public void A_row_saves_and_shows_again_unchanged_in_either_form()
    {
        foreach (var text in new[] { "{mail} | trim | lower", "const:Contractor", "{mail}" })
        {
            var (source, transform) = MappingRowExpression.Parse(text);
            Assert.False(MappingRowExpression.IsPattern(transform));
            Assert.Equal(text, MappingRowExpression.Format(source, transform));
        }
        foreach (var text in new[] { "Employee", "{givenName:4}", "His name is {givenName} {sn}.", "{upper(substring(sn, -4))}" })
        {
            var (source, transform) = MappingRowExpression.Parse(text);
            Assert.Equal(string.Empty, source);
            Assert.Equal(MappingRowExpression.PatternPrefix + text, transform);
            Assert.Equal(text, MappingRowExpression.Format(source, transform));
            Assert.Null(MappingRowExpression.Problem(text));
            Assert.Null(AttributeTransformExpression.ValidationError(transform));
            Assert.True(AttributeTransformExpression.ProducesValueWhenSourceMissing(transform));
        }
    }

    [Fact]
    public void A_pattern_a_sync_cannot_run_is_refused_in_plain_words()
    {
        Assert.Equal("Close the field with }.", MappingRowExpression.Problem("Hello {givenName"));
        Assert.Contains("uniqueness number", MappingRowExpression.Problem("{givenName}{#}"));
        Assert.Contains("reads the source record only", MappingRowExpression.Problem("{givenName}@{Org.EmailDomain}"));
        Assert.Contains("{shout(sn)}", MappingRowExpression.Problem("{givenName} {shout(sn)}"));
        Assert.Contains("own { }", MappingRowExpression.Problem("{a {b}}"));
        // The older form keeps its own messages.
        Assert.Equal("Unknown transformation. Choose a supported expression operation.", MappingRowExpression.Problem("{mail} | shout"));
    }

    [Fact]
    public void The_run_asks_the_source_for_every_field_a_row_reads()
    {
        Assert.Equal(new[] { "mail" }, MappingRowExpression.SourceAttributesOf("mail", "trim"));
        Assert.Empty(MappingRowExpression.SourceAttributesOf("", "const:x"));
        Assert.Equal(new[] { "givenName", "sn" },
            MappingRowExpression.SourceAttributesOf("", MappingRowExpression.PatternPrefix + "{givenName:1}{sn}").OrderBy(n => n));

        var orchestrator = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Sync", "Orchestration", "SyncProjectOrchestrator.cs")));
        Assert.Contains(".SelectMany(m => Conduit.Mapping.MappingRowExpression.SourceAttributesOf(m.SourceAttribute, m.TransformExpr))", orchestrator);
    }

    [Fact]
    public void A_pattern_row_is_written_from_the_whole_record()
    {
        var source = new ConnectorObject { SourceId = "1", ObjectClass = "user" };
        source.Attributes["givenName"] = "Paula";
        source.Attributes["SN"] = "Ortiz";             // read whatever the case
        source.Attributes["employeeID"] = "GC10042";
        source.Attributes["title"] = "";               // carried, and empty

        AttributeMapping Row(string target, string expression, bool always = false)
        {
            var (s, t) = MappingRowExpression.Parse(expression);
            return new AttributeMapping { SourceAttribute = s, SinkAttribute = target, TransformExpr = t, IsRequired = always };
        }

        var result = new ConnectorMappingPlan(new[]
        {
            Row("description", "Her name is {givenName} {sn}."),
            Row("userName", "{lower(substring(givenName, 0, 1))}{lower(sn)}"),
            Row("badge", "{employeeID:-4}"),
            Row("type", "Employee"),
            Row("jobTitle", "{title}"),                 // step form: one field, unchanged
            Row("cleared", "{upper(title)}"),           // a pattern over a field the record carried empty
            Row("untouched", "{upper(mobile)}"),        // a pattern over a field the record did not carry
            Row("forced", "{upper(mobile)}", always: true),
        }).Apply(source);

        Assert.Equal("Her name is Paula Ortiz.", result.Attributes["description"]);
        Assert.Equal("portiz", result.Attributes["userName"]);
        Assert.Equal("0042", result.Attributes["badge"]);
        Assert.Equal("Employee", result.Attributes["type"]);
        Assert.Equal("", result.Attributes["jobTitle"]);
        Assert.Equal("", result.Attributes["cleared"]);
        Assert.False(result.Attributes.ContainsKey("untouched"));
        Assert.Equal("", result.Attributes["forced"]);
    }

    [Fact]
    public void A_project_of_only_pattern_rows_is_still_a_mapped_project()
    {
        var source = new ConnectorObject { SourceId = "1", ObjectClass = "user" };
        source.Attributes["a"] = "x";
        source.Attributes["unmapped"] = "omit";
        var (s, t) = MappingRowExpression.Parse("{a}-{a}");
        var result = new ConnectorMappingPlan(new[] { new AttributeMapping { SourceAttribute = s, SinkAttribute = "out", TransformExpr = t } }).Apply(source);
        Assert.Equal("x-x", result.Attributes["out"]);
        Assert.False(result.Attributes.ContainsKey("unmapped"));
    }

    // ---- the builder's model: it reads a typed pattern into parts and writes the same pattern back

    [Theory]
    [InlineData("His name is {givenName} {sn}.")]
    [InlineData("{FirstName:1}{LastName}{#}")]
    [InlineData("{FirstName:4,x}-{EmployeeId:-4}")]
    [InlineData("{upper(LastName)}, {proper(FirstName)}")]
    [InlineData("{upper(substring(FirstName, 0, 4))}{lower(substring(LastName, -3))}")]
    [InlineData("{Date:yyyyMMdd}-{date(Now, \"+30d\", \"yyyy-MM-dd\")}-{date(Now, \"-7d\", \"M/d/yyyy\")}")]
    [InlineData("{lookup(Department, \"Sales=S;Engineering=E\", \"X\")}{Org.EmailDomain}")]
    [InlineData("Employee")]
    [InlineData("")]
    public void Opening_a_value_in_the_builder_never_changes_it(string pattern)
    {
        Assert.Equal(pattern, BrandValuePattern.Compose(BrandValuePattern.Parse(pattern)));
    }

    [Fact]
    public void The_builder_reads_the_shapes_it_draws_and_keeps_the_rest_as_typed()
    {
        var parts = BrandValuePattern.Parse("ID {upper(substring(FirstName, 0, 4))}{EmployeeId:-4}{date(Now, \"+30d\", \"yyyy-MM-dd\")}{if(Department, Department, \"none\")}");
        Assert.Equal(new[] { BrandValuePartKind.Text, BrandValuePartKind.Field, BrandValuePartKind.Field, BrandValuePartKind.Date, BrandValuePartKind.Typed },
            parts.Select(p => p.Kind));
        Assert.Equal("ID ", parts[0].Text);
        Assert.Equal(("FirstName", 4, BrandValueCase.Upper), (parts[1].Field, parts[1].Take, parts[1].Case));
        Assert.Equal(("EmployeeId", -4, BrandValueCase.AsIs), (parts[2].Field, parts[2].Take, parts[2].Case));
        Assert.Equal((30, "yyyy-MM-dd"), (parts[3].OffsetDays, parts[3].DateFormat));
        Assert.Equal("{if(Department, Department, \"none\")}", parts[4].Text);
    }

    [Fact]
    public void What_the_builder_writes_is_what_the_engine_runs()
    {
        var parts = new List<BrandValuePart>
        {
            new() { Kind = BrandValuePartKind.Text, Text = "His name is " },
            new() { Kind = BrandValuePartKind.Field, Field = "FirstName", Take = 4 },
            new() { Kind = BrandValuePartKind.Text, Text = " " },
            new() { Kind = BrandValuePartKind.Field, Field = "LastName", Take = -4, Case = BrandValueCase.Upper },
            new() { Kind = BrandValuePartKind.Text, Text = ", " },
            new() { Kind = BrandValuePartKind.Field, Field = "Department", Case = BrandValueCase.Lower },
            new() { Kind = BrandValuePartKind.Text, Text = ", from " },
            new() { Kind = BrandValuePartKind.Date, DateFormat = "yyyy-MM-dd", OffsetDays = 30 },
            new() { Kind = BrandValuePartKind.Text, Text = "." },
        };
        var pattern = BrandValuePattern.Compose(parts);
        Assert.Equal("His name is {FirstName:4} {upper(substring(LastName, -4))}, {lower(Department)}, from {date(Now, \"+30d\", \"yyyy-MM-dd\")}.", pattern);
        Assert.Equal("His name is Paul RTIZ, finance, from 2026-12-24.",
            Render(pattern, ("FirstName", "Paula"), ("LastName", "Ortiz"), ("Department", "Finance")));
        Assert.Null(MappingRowExpression.Problem(pattern));
        Assert.Equal("The text “His name is ”, then the first 4 characters of First name, then the text “ ”, then the last 4 characters of LastName in upper case, "
            + "then the text “, ”, then Department in lower case, then the text “, from ”, then the date 30 days from today as yyyy-MM-dd, then the text “.”.",
            BrandValuePattern.Describe(parts, name => name == "FirstName" ? "First name" : null));
    }

    [Fact]
    public void A_field_is_translated_through_a_table_and_the_builder_draws_it()
    {
        var part = new BrandValuePart { Kind = BrandValuePartKind.Translate, Field = "EmployeeType", Otherwise = BrandValueOtherwise.KeepValue };
        part.Pairs.Add(new BrandValuePair { From = "FTE", To = "Employee" });
        part.Pairs.Add(new BrandValuePair { From = " CTR ", To = "Contractor" });
        part.Pairs.Add(new BrandValuePair { From = "", To = "ignored" });           // a line with nothing to match is not written
        var pattern = BrandValuePattern.Compose(new[] { part });
        Assert.Equal("{lookup(EmployeeType, \"FTE=Employee;CTR=Contractor\", EmployeeType)}", pattern);
        Assert.Equal("Employee", Render(pattern, ("EmployeeType", "fte")));        // compared without regard to capitals
        Assert.Equal("Contractor", Render(pattern, ("EmployeeType", "CTR")));
        Assert.Equal("Intern", Render(pattern, ("EmployeeType", "Intern")));       // anything else is kept
        Assert.Null(MappingRowExpression.Problem(pattern));

        var read = Assert.Single(BrandValuePattern.Parse(pattern));
        Assert.Equal(BrandValuePartKind.Translate, read.Kind);
        Assert.Equal(BrandValueOtherwise.KeepValue, read.Otherwise);
        Assert.Equal(new[] { "FTE>Employee", "CTR>Contractor" }, read.Pairs.Select(pair => pair.From + ">" + pair.To));
        Assert.Equal("EmployeeType translated (\u201CFTE\u201D becomes \u201CEmployee\u201D; \u201CCTR\u201D becomes \u201CContractor\u201D; anything else is left as it is).",
            BrandValuePattern.Describe(new[] { read }));

        part.Otherwise = BrandValueOtherwise.WriteText;
        part.OtherwiseText = "Un\"known;";   // the grammar's own marks are dropped
        var fixedElse = BrandValuePattern.Compose(new[] { part });
        Assert.Equal("{lookup(EmployeeType, \"FTE=Employee;CTR=Contractor\", \"Unknown\")}", fixedElse);
        Assert.Equal("Unknown", Render(fixedElse, ("EmployeeType", "Intern")));
        part.Otherwise = BrandValueOtherwise.LeaveEmpty;
        Assert.Equal("", Render(BrandValuePattern.Compose(new[] { part }), ("EmployeeType", "Intern")));
        // A lookup that falls back to a different field is not something the builder draws: it stays typed.
        Assert.Equal(BrandValuePartKind.Typed, BrandValuePattern.Parse("{lookup(EmployeeType, \"a=b\", Department)}").Single().Kind);
    }

    [Fact]
    public void A_preview_says_what_a_row_writes_in_either_form()
    {
        var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["FirstName"] = " Paula ", ["LastName"] = "Ortiz", ["EmployeeType"] = "Contractor" };
        Assert.Equal("Paula", MappingRowExpression.Evaluate("{FirstName} | trim", row));
        Assert.Equal("Contractor", MappingRowExpression.Evaluate("{employeetype}", row));
        Assert.Equal("Fixed", MappingRowExpression.Evaluate("const:Fixed", row));
        Assert.Equal("Paul O.", MappingRowExpression.Evaluate("{FirstName:4} {LastName:1}.", row));
        Assert.Equal("", MappingRowExpression.Evaluate("{Missing}", row));
    }

    [Fact]
    public void The_builder_is_given_the_files_sample_values_and_the_targets_lists()
    {
        var result = Conduit.Web.Services.FileToPeopleMapping.Build(new[] { "EmployeeId", "EmployeeType", "Notes" }, new[] { "EmployeeId", "EmployeeType", "IdentityType" }) with
        {
            SampleRows = new[]
            {
                new Dictionary<string, string?> { ["EmployeeId"] = "GC1", ["EmployeeType"] = "Employee", ["Notes"] = "" },
                new Dictionary<string, string?> { ["EmployeeId"] = "GC2", ["EmployeeType"] = "contractor", ["Notes"] = null },
                new Dictionary<string, string?> { ["EmployeeId"] = "GC3", ["EmployeeType"] = "Employee" },
            },
        };
        var fields = result.Fields;
        Assert.Equal(new[] { "EmployeeId", "EmployeeType", "Notes" }, fields.Select(f => f.Name));
        Assert.Equal("GC1", fields[0].Sample);
        Assert.Equal(new[] { "contractor", "Employee" }, fields[1].Values);
        Assert.Null(fields[2].Sample);
        Assert.Null(fields[2].Values);

        var page = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Pages", "Sync", "SyncProjects.razor")));
        Assert.Contains("BuilderFields=\"@MappingBuilderFields()\" BuilderTargetValues=\"@MappingTargetValues\"", page);
        Assert.Contains("Preview=\"@MappingPreview()\"", page);
        Assert.Contains("if (fromTemplate) templateMappingIds.UnionWith(", page);   // rows proposed from the file are not chipped as Template
        var adapter = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Connectors.Csv", "CsvAdapter.cs")));
        Assert.Contains("while (rows.Count < maximumRows && await csv.ReadAsync() is { } row)", adapter);   // never the whole file
    }

    [Fact]
    public void The_fields_offered_come_from_the_simple_tokens_a_product_suggests()
    {
        var fields = BrandValuePattern.FieldsFromTokens(new[] { "{FirstName}", "{upper(LastName)}", "{FirstName}", "{Now}", "{EmployeeId:4}", "{Custom8}" },
            token => token == "{Custom8}" ? "HR status" : null);
        Assert.Equal(new[] { "FirstName", "Custom8" }, fields.Select(f => f.Name));
        Assert.Equal("HR status", fields[1].Display);
        Assert.Equal("FirstName", fields[0].Display);
    }

    [Fact]
    public void Every_mapping_row_has_a_build_button_and_the_engine_is_the_shared_one()
    {
        var brand = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(RepoFile("x"))!, "..", "IdentityCenter.Brand", "src", "IdentityCenter.Brand"));
        var editor = File.ReadAllText(Path.Combine(brand, "Components", "Inputs", "BrandAttributeMapEditor.razor"));
        Assert.Contains("icb-map-editor-build", editor);
        Assert.Contains("<BrandValueBuilder Id=\"@(Id + \"-builder\")\" @bind-Value=\"buildingValue\"", editor);
        Assert.Contains("[Parameter] public bool Builder { get; set; } = true;", editor);

        var engine = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Mapping", "ValuePatternEngine.cs")));
        Assert.Contains("namespace Conduit.Mapping;", engine);
        Assert.DoesNotContain("DataAccessLibrary", engine);

        var page = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Pages", "Sync", "SyncProjects.razor")));
        Assert.Contains("ExpressionProblem=\"Conduit.Mapping.MappingRowExpression.Problem\"", page);
    }

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));
}
