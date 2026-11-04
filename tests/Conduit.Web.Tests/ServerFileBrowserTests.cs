using System.Runtime.CompilerServices;
using Conduit.Connectors.Csv;
using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Security;
using Conduit.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// CSV-FILE-PICKER-01 (Jacob, 2026-11-04, on the New Connection wizard: "needed a way to browse the file
/// system and choose the csv file"). The file is on the job server, so the Browse button lists the Conduit
/// host's own disk through one service: only absolute local paths, never UNC/device/relative/dot segments,
/// folders plus files of the field's extensions, capped, and never contents.
/// </summary>
public sealed class ServerFileBrowserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "conduit-file-browser-" + Guid.NewGuid().ToString("N"));
    private readonly ServerFileBrowser _browser = new();
    private static readonly string[] Csv = { ".csv", ".txt" };

    public ServerFileBrowserTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Granite"));
        Directory.CreateDirectory(Path.Combine(_root, "Archive"));
        File.WriteAllText(Path.Combine(_root, "employees.csv"), "EmployeeId,FirstName\nGC10001,Paula\n");
        File.WriteAllText(Path.Combine(_root, "notes.TXT"), "x");
        File.WriteAllText(Path.Combine(_root, "payroll.xlsx"), "x");
        File.WriteAllText(Path.Combine(_root, "run.ps1"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_folder_lists_its_folders_first_then_only_the_fields_files_and_no_contents()
    {
        var listing = _browser.List(_root, Csv);

        Assert.Null(listing.Error);
        Assert.Equal(Path.GetFullPath(_root), listing.Path);
        Assert.Equal(new[] { "Archive", "Granite", "employees.csv", "notes.TXT" }, listing.Entries.Select(e => e.Name));
        Assert.True(listing.Entries[0].IsFolder && listing.Entries[1].IsFolder);
        var csv = listing.Entries.Single(e => e.Name == "employees.csv");
        Assert.False(csv.IsFolder);
        Assert.Equal(Path.Combine(Path.GetFullPath(_root), "employees.csv"), csv.FullPath);
        Assert.True(csv.Size > 0);
        Assert.NotNull(csv.ModifiedUtc);
        Assert.Equal(Directory.GetParent(Path.GetFullPath(_root))!.FullName, listing.Parent);
        Assert.DoesNotContain(typeof(ServerFileEntry).GetProperties(), p => p.Name.Contains("Content", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extensions_match_without_case_and_with_or_without_the_dot()
    {
        var listing = _browser.List(_root, new[] { "CSV" });
        Assert.Equal(new[] { "Archive", "Granite", "employees.csv" }, listing.Entries.Select(e => e.Name));
    }

    [Fact]
    public void A_full_folder_is_capped_and_says_so()
    {
        for (var i = 0; i < 6; i++) File.WriteAllText(Path.Combine(_root, $"feed{i}.csv"), "x");

        var listing = _browser.List(_root, Csv, max: 4);

        Assert.Equal(4, listing.Entries.Count);
        Assert.True(listing.Capped);
        Assert.False(_browser.List(_root, Csv).Capped);
    }

    [Theory]
    [InlineData(@"\\fileserver\share\feeds")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData("//fileserver/share")]
    [InlineData(@"HRFeeds\Granite")]
    [InlineData(@"..\..\Windows")]
    [InlineData(@"C:Windows")]
    [InlineData(@"C:\HRFeeds\..\Windows")]
    [InlineData(@"C:\HRFeeds\.\Granite")]
    [InlineData(@"C:\HRFeeds.")]
    public void Network_device_relative_and_dot_paths_are_refused_before_the_disk_is_read(string path)
    {
        Assert.False(ServerFileBrowser.TryNormalise(path, out _, out var refusal));
        Assert.False(string.IsNullOrWhiteSpace(refusal));

        var listing = _browser.List(path, Csv);
        Assert.Empty(listing.Entries);
        Assert.Equal(refusal, listing.Error);
        Assert.Null(listing.Parent);
    }

    [Fact]
    public void A_missing_folder_is_reported_not_thrown()
    {
        var listing = _browser.List(Path.Combine(_root, "nope"), Csv);
        Assert.Empty(listing.Entries);
        Assert.Contains("does not exist", listing.Error);
    }

    [Fact]
    public void The_top_is_the_hosts_ready_fixed_drives()
    {
        var listing = _browser.List(null, Csv);
        Assert.Null(listing.Error);
        Assert.Null(listing.Path);
        Assert.Null(listing.Parent);
        Assert.NotEmpty(listing.Entries);
        Assert.All(listing.Entries, e => Assert.True(e.IsFolder && Path.IsPathFullyQualified(e.FullPath)));
    }

    [Fact]
    public void The_dialog_opens_at_the_folder_of_the_typed_value()
    {
        var file = Path.Combine(_root, "employees.csv");
        Assert.Equal(Path.GetFullPath(_root), ServerFileBrowser.StartFolderFor(file));
        Assert.Equal(Path.GetFullPath(_root), ServerFileBrowser.StartFolderFor(Path.Combine(_root, "not-yet.csv")));
        Assert.Equal(Path.Combine(Path.GetFullPath(_root), "Granite"), ServerFileBrowser.StartFolderFor(Path.Combine(_root, "Granite")));
        Assert.Null(ServerFileBrowser.StartFolderFor(@"\\fileserver\share\x.csv"));
        Assert.Null(ServerFileBrowser.StartFolderFor(""));
    }

    [Fact]
    public void The_csv_file_path_field_carries_the_browse_hint_and_no_other_csv_field_does()
    {
        // The adapter's field specs are static data; the protector is never asked for anything here.
        var dbConfig = new DatabaseConfig { ConnectionString = "Server=(local);Database=__none__;Trusted_Connection=False;" };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Sync:CredentialKey"] = Convert.ToBase64String(new byte[32]) })
            .Build();
        var protector = new CredentialProtector(config, new ConnectionCredentialRepository(dbConfig), new CredentialKeyringRepository(dbConfig));
        var fields = new CsvAdapter(protector, NullLoggerFactory.Instance).CredentialTypes.Single().Fields;
        Assert.Equal(new[] { ".csv", ".txt" }, fields.Single(f => f.Key == "FilePath").BrowseServerFileExtensions);
        Assert.All(fields.Where(f => f.Key != "FilePath"), f => Assert.Empty(f.BrowseServerFileExtensions));
    }

    [Fact]
    public void Both_credential_renderers_offer_browse_for_a_hinted_field_through_the_one_adapter()
    {
        var page = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Pages", "ConnectedSystems.razor"))).Replace("\r\n", "\n");
        Assert.Contains("else if (field.BrowseServerFileExtensions.Count > 0)\n", page);                          // the create/edit wizard
        Assert.Contains("else if (field.BrowseServerFileExtensions.Count > 0 && !field.IsSecret)", page);        // Credentials & test
        Assert.Contains("@onclick=\"() => BrowseCreateFileAsync(browseTypeName, browseField, browseDefault)\"", page);
        Assert.Contains("@onclick=\"() => BrowseGenericFileAsync(browseGenericField, browseGenericDefault)\"", page);
        Assert.Contains("path => { SetCreateString(credTypeName, field.Key, path);", page);                      // a pick invalidates a green test like typing
        Assert.Equal(1, page.Split("<ServerFileBrowserModal @ref=\"serverFileBrowser\" />").Length - 1);

        var adapter = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Shared", "Design", "ServerFileBrowserModal.razor")));
        Assert.Contains("<BrandFileBrowserDialog", adapter);                                                     // the shared dialog, not a local copy
        Assert.Contains("!user.IsInRole(\"Admin\")", adapter);                                                    // only an administrator is listed anything
        Assert.DoesNotContain("style=", adapter);

        var program = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Program.cs")));
        Assert.Contains("builder.Services.AddSingleton<Conduit.Web.Services.ServerFileBrowser>();", program);
    }

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));
}
