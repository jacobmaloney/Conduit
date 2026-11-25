using System.Runtime.CompilerServices;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Web.Services;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// The mapping editor shows real records from any source, not only a file: each field with a sample value,
/// the values of a short list, and a record to preview a row against. Jacob, 2026-11-24, on the builder with
/// the HR file's values in it: "keep building, I love that".
/// </summary>
public class SourceSampleReaderTests
{
    [Fact]
    public void A_sample_read_keeps_the_steps_scope_and_asks_for_a_handful_of_whole_records()
    {
        var step = new SyncProjectScope
        {
            BaseDN = "OU=Granite Construction,OU=Organization,DC=Domain,DC=local",
            IncludedBaseDNs = "OU=Employees", ExcludedBaseDNs = "OU=Disabled", LdapFilter = "(objectClass=user)",
            PageSize = 1000, MaxObjects = null, RequestedAttributes = new() { "mail" }, ContainerOnly = true,
        };
        var sample = SourceSampleReader.SampleScope(step);
        Assert.Equal(step.BaseDN, sample.BaseDN);
        Assert.Equal(step.IncludedBaseDNs, sample.IncludedBaseDNs);
        Assert.Equal(step.ExcludedBaseDNs, sample.ExcludedBaseDNs);
        Assert.Equal(step.LdapFilter, sample.LdapFilter);
        Assert.True(sample.ContainerOnly);
        Assert.Equal(SourceSampleReader.SampleCount, sample.MaxObjects);
        Assert.Equal(SourceSampleReader.SampleCount, sample.PageSize);
        Assert.Null(sample.RequestedAttributes);          // every attribute, so unmapped fields are offered too
        Assert.True(sample.SuppressTombstones);
        Assert.NotSame(step, sample);
        Assert.Equal(1000, step.PageSize);                // the step's own scope is not touched
        Assert.Equal(SourceSampleReader.SampleCount, SourceSampleReader.SampleScope(null).MaxObjects);
    }

    [Fact]
    public void A_record_becomes_text_a_person_can_read()
    {
        var record = new ConnectorObject { SourceId = "1", ObjectClass = "user" };
        record.Attributes["givenName"] = "Paula";
        record.Attributes["memberOf"] = new[] { "", "CN=Staff", "CN=Finance" };
        record.Attributes["userAccountControl"] = 512;
        record.Attributes["thumbnailPhoto"] = new byte[] { 1, 2, 3 };
        record.Attributes["title"] = null;
        record.Attributes["_tags"] = "Employee";
        var row = SourceSampleReader.ToRow(record);
        Assert.Equal("Paula", row["GIVENNAME"]);          // read whatever the case
        Assert.Equal("CN=Staff", row["memberOf"]);
        Assert.Equal("512", row["userAccountControl"]);
        Assert.Null(row["thumbnailPhoto"]);
        Assert.Null(row["title"]);
        Assert.False(row.ContainsKey("_tags"));           // the connector's own field, not the source's
    }

    [Fact]
    public void Mapped_fields_come_first_and_only_a_repeating_field_is_offered_as_a_list()
    {
        var rows = Enumerable.Range(1, 10).Select(i => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["sAMAccountName"] = "user" + i,
            ["department"] = i % 2 == 0 ? "Finance" : "Field",
            ["mail"] = null,
        }).ToList();
        var fields = SourceSampleReader.Fields(new[] { "mail", "", "department" }, rows);
        Assert.Equal(new[] { "mail", "department", "sAMAccountName" }, fields.Select(f => f.Name));
        Assert.Null(fields[0].Sample);
        Assert.Null(fields[0].Values);
        Assert.Equal("Field", fields[1].Sample);
        Assert.Equal(new[] { "Field", "Finance" }, fields[1].Values);
        Assert.Equal("user1", fields[2].Sample);
        Assert.Null(fields[2].Values);                    // ten different names are not a list
    }

    [Fact]
    public void The_step_editor_reads_samples_when_its_mappings_open_and_a_failed_read_costs_nothing()
    {
        var page = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Pages", "Sync", "SyncProjects.razor")));
        Assert.Contains("await LoadMappingSamplesAsync(stepId);", page);
        Assert.Contains("mappingSamples = await SampleReader.ReadAsync(source, step.ObjectClass, scope, CancellationToken.None);", page);
        Assert.Contains("var scope = stepScopes.TryGetValue(stepId, out var own) && own is not null ? own : scopeForm;", page);
        Assert.Contains("PreviewSubject=\"@(MappingPreview() is null ? null : MappingSampleSubject)\"", page);

        var reader = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Services", "SourceSampleReader.cs")));
        Assert.Contains("timeout.CancelAfter(Limit);", reader);
        Assert.Contains("if (rows.Count >= SampleCount) break;", reader);
        Assert.Contains("catch (Exception ex)", reader);
        Assert.DoesNotContain("CreateSink", reader);       // it only ever reads

        var program = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Program.cs")));
        Assert.Equal(2, program.Split("Conduit.Web.Services.SourceSampleReader").Length);
    }

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));
}
