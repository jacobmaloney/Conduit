using System.Text;
using System.Text.Json;
using Conduit.Core.SyncModels;
using Conduit.Readers.Csv;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Connectors.Csv.Tests;

public class CsvSourceTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"conduit-cc04-{Guid.NewGuid():N}.csv");
    private readonly Guid _tenant = Guid.NewGuid();

    private (CsvSource Source, FakeCredentials Credentials) Source(bool header = true, string? idColumn = null, string encoding = "UTF-8", string delimiter = ",")
    {
        var credentials = new FakeCredentials(_tenant, JsonSerializer.Serialize(new { FilePath = _file, HasHeader = header, IdColumn = idColumn, Encoding = encoding, Delimiter = delimiter }));
        return (new CsvSource(_tenant, credentials, NullLogger<CsvSource>.Instance), credentials);
    }
    private static async Task<List<ConnectorObject>> Rows(CsvSource source, int? limit = null, CancellationToken ct = default)
    {
        var rows = new List<ConnectorObject>();
        await foreach (var row in source.ReadAsync("User", new SyncProjectScope { MaxObjects = limit }, ct)) rows.Add(row);
        return rows;
    }

    [Fact]
    public async Task SourcePreservesIdentityFieldsCaseWhitespaceAndOmission()
    {
        await File.WriteAllTextAsync(_file, "EmployeeId,Name,blank\nE1,  Ada  ,\nE2,Grace,");
        var (source, _) = Source();
        var rows = await Rows(source);
        Assert.Equal(new[] { "E1", "E2" }, rows.Select(r => r.SourceId));
        Assert.Equal("  Ada  ", rows[0].Attributes["NAME"]);
        Assert.False(rows[0].Attributes.ContainsKey("blank"));
        Assert.Equal("User", rows[0].ObjectClass);
        Assert.Equal("user", rows[0].Attributes["objectClass"]);
        Assert.Equal("E1", rows[0].Attributes["objectGuid"]);
        Assert.Equal("E1", rows[0].Attributes["id"]);
    }

    [Theory]
    [InlineData("chosen,objectGuid,id,EmployeeId\nC,G,I,E", "CHOSEN", "C")]
    [InlineData("objectGuid,id,EmployeeId\nG,I,E", null, "G")]
    [InlineData("id,EmployeeId\nI,E", null, "I")]
    [InlineData("Name\nAda", null, "row-1")]
    public async Task IdPrecedenceIsPreserved(string csv, string? configured, string expected)
    {
        await File.WriteAllTextAsync(_file, csv);
        Assert.Equal(expected, (await Rows(Source(idColumn: configured).Source))[0].SourceId);
    }

    [Fact]
    public async Task HeaderlessSourceIncludesFirstRecordAndConfiguredId()
    {
        await File.WriteAllTextAsync(_file, "1,Ada\n2,Grace");
        var rows = await Rows(Source(false, "col0").Source);
        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0].SourceId);
        Assert.Equal("Ada", rows[0].Attributes["col1"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ObjectLimitAndEarlyDisposalReleaseFile(int limit)
    {
        await File.WriteAllTextAsync(_file, "id,name\n1,Ada\n2,Grace\n3,Katherine");
        Assert.Equal(limit, (await Rows(Source().Source, limit)).Count);
        using var exclusive = File.Open(_file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("id,ID\n1,2", "headers")]
    [InlineData("id,name\n1,private,extra", "could not be read")]
    [InlineData("id,name\n1,\"private", "could not be read")]
    public async Task InvalidDataFailsConnectionCheckAndRead(string csv, string reason)
    {
        await File.WriteAllTextAsync(_file, csv);
        var source = Source().Source;
        var check = await source.TestConnectionAsync(default);
        Assert.False(check.IsSuccessful);
        Assert.Contains(reason, check.Message);
        var error = await Assert.ThrowsAsync<CsvReadException>(() => Rows(source));
        Assert.Contains(reason, error.Message);
        Assert.DoesNotContain("private", error.ToString());
    }

    [Fact]
    public async Task ObjectLimitDoesNotValidateRecordsOutsideSelectedScope()
    {
        await File.WriteAllTextAsync(_file, "id,name\n1,Ada\n2,private,extra");
        var source = Source().Source;
        Assert.Equal("1", Assert.Single(await Rows(source, 1)).SourceId);
        await Assert.ThrowsAsync<CsvReadException>(() => Rows(source));
    }

    [Fact]
    public async Task SampleCheckDoesNotClaimWholeFileValidation()
    {
        await File.WriteAllTextAsync(_file, "id,name\n1,Ada\n2,private,extra");
        var source = Source().Source;
        var check = await source.TestConnectionAsync(default);
        Assert.True(check.IsSuccessful);
        Assert.Contains("first data row checked", check.Message);
        Assert.Contains("Remaining rows", check.Message);
        await Assert.ThrowsAsync<CsvReadException>(() => Rows(source));
    }

    [Theory]
    [InlineData("")]
    [InlineData("id,name\n")]
    public async Task EmptySourcesAreEnumerableButNotSuccessfulConnectionSamples(string csv)
    {
        await File.WriteAllTextAsync(_file, csv);
        var source = Source().Source;
        Assert.Empty(await Rows(source));
        var check = await source.TestConnectionAsync(default);
        Assert.False(check.IsSuccessful);
        Assert.Contains("no data rows", check.Message);
    }

    [Fact]
    public async Task MissingFilesHaveSpecificFailure()
    {
        var source = Source().Source;
        Assert.Contains("File not found", (await source.TestConnectionAsync(default)).Message);
        await Assert.ThrowsAsync<FileNotFoundException>(() => Rows(source));
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public async Task CancelledCallsNeverResolveCredentials()
    {
        var (source, credentials) = Source();
        var ct = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Rows(source, ct: ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.TestConnectionAsync(ct));
        Assert.Empty(credentials.Requests);
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public async Task CancellationDuringCredentialResolutionDoesNotBecomeMissingCredentials()
    {
        using var cts = new CancellationTokenSource();
        var (_, credentials) = Source();
        credentials.OnRetrieve = cts.Cancel;
        // Missing credentials returned after cancellation must not mask cancellation.
        var source = new CsvSource(Guid.NewGuid(), credentials, NullLogger<CsvSource>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Rows(source, ct: cts.Token));
        Assert.NotEmpty(credentials.Requests);
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public async Task TenantAndSourceCredentialOverrideStayHostOwned()
    {
        await File.WriteAllTextAsync(_file, "id\n1");
        var (source, credentials) = Source();
        using var context = CredentialNameContext.Push("csv-source-override", "unused-sink");
        Assert.Single(await Rows(source));
        Assert.Equal(new[] { (_tenant, "csv-source-override") }, credentials.Requests);
        var otherTenant = new CsvSource(Guid.NewGuid(), credentials, NullLogger<CsvSource>.Instance);
        var check = await otherTenant.TestConnectionAsync(default);
        Assert.False(check.IsSuccessful);
        Assert.Contains("No 'csv' credential", check.Message);
    }

    [Fact]
    public async Task EncodingAndDelimiterStayHostConfigured()
    {
        // No BOM: this verifies the saved encoding, not StreamReader's BOM detection.
        await File.WriteAllTextAsync(_file, "id;name\n1;Zoë", new UnicodeEncoding(false, false));
        Assert.Equal("Zoë", (await Rows(Source(encoding: "UTF16", delimiter: ";").Source))[0].Attributes["name"]);
    }

    public void Dispose() { if (File.Exists(_file)) File.Delete(_file); }

    private sealed class FakeCredentials(Guid tenant, string json) : CredentialProtector(null!, null!, null!)
    {
        public List<(Guid Tenant, string Name)> Requests { get; } = new();
        public Action? OnRetrieve { get; set; }
        public override Task<string?> RetrieveAsync(Guid tenantId, string credentialName)
        {
            Requests.Add((tenantId, credentialName));
            OnRetrieve?.Invoke();
            return Task.FromResult(tenantId == tenant ? json : null);
        }
        public override Task StoreAsync(Guid tenantId, string credentialName, string plaintext) =>
            throw new InvalidOperationException("Read-only CSV flow must never store credentials.");
    }
}
