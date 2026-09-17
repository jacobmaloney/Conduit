using Conduit.Readers.Csv;
using Xunit;

namespace Conduit.Connectors.Csv.Tests;

public class CsvRecordReaderTests
{
    [Theory]
    [InlineData(false, "  Ada  ")]
    [InlineData(true, "Ada")]
    public async Task DelimitersQuotesNewlinesAndEmptyValues(bool trim, string expected)
    {
        using var input = new StringReader("id;name;note;blank\r\n1;  Ada  ;\"comma, semi; and \"\"quote\"\"\nnext line\";\r\n");
        using var csv = new CsvRecordReader(input, new() { Delimiter = ";", TrimFields = trim });
        Assert.Equal(new[] { "id", "name", "note", "blank" }, await csv.ReadHeaderAsync());
        var row = (await csv.ReadAsync())!;
        Assert.Equal(expected, row["name"]);
        Assert.Equal("comma, semi; and \"quote\"\nnext line", row["note"]);
        Assert.Equal("", row["blank"]);
        Assert.Null(await csv.ReadAsync());
    }

    [Theory]
    [InlineData("id,ID\n1,2", CsvReadFailure.InvalidHeaders)]
    [InlineData("id,\n1,2", CsvReadFailure.InvalidHeaders)]
    [InlineData("id, \n1,2", CsvReadFailure.InvalidHeaders)]
    [InlineData("id,name\n1,private,extra", CsvReadFailure.InvalidData)]
    [InlineData("id,name\n1", CsvReadFailure.InvalidData)]
    [InlineData("id,name\n1,\"private", CsvReadFailure.InvalidData)]
    [InlineData("id,name\n1,pr\"ivate", CsvReadFailure.InvalidData)]
    public async Task InvalidInputFailsWithSafeReason(string text, CsvReadFailure reason)
    {
        using var input = new StringReader(text);
        using var csv = new CsvRecordReader(input, new());
        var error = await Assert.ThrowsAsync<CsvReadException>(() => csv.ReadAsync());
        Assert.Equal(reason, error.Reason);
        Assert.DoesNotContain("private", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("id,name\n")]
    public async Task EmptyAndHeaderOnlyInputAreNotErrors(string text)
    {
        using var input = new StringReader(text);
        using var csv = new CsvRecordReader(input, new());
        Assert.Null(await csv.ReadAsync());
        Assert.Equal(text.StartsWith("id") ? 2 : 0, csv.FieldNames.Count);
    }

    [Fact]
    public async Task HeaderlessInputRetainsFirstRowAndRejectsChangingWidths()
    {
        using var input = new StringReader("1,Ada\n2,Grace\n3,extra,column");
        using var csv = new CsvRecordReader(input, new() { HasHeaderRow = false });
        Assert.Empty(await csv.ReadHeaderAsync());
        Assert.Equal("1", (await csv.ReadAsync())!["col0"]);
        Assert.Equal("Grace", (await csv.ReadAsync())!["col1"]);
        Assert.Equal(new[] { "col0", "col1" }, csv.FieldNames);
        Assert.Equal(CsvReadFailure.InvalidData, (await Assert.ThrowsAsync<CsvReadException>(() => csv.ReadAsync())).Reason);
    }

    [Fact]
    public async Task HeaderLookupKeepsSourceCaseAndDoesNotConsumeData()
    {
        using var input = new StringReader("EmployeeID,Name\n1,Ada");
        using var csv = new CsvRecordReader(input, new());
        Assert.Equal(await csv.ReadHeaderAsync(), await csv.ReadHeaderAsync());
        var row = (await csv.ReadAsync())!;
        Assert.True(row.ContainsKey("EmployeeID"));
        Assert.False(row.ContainsKey("employeeid"));
        Assert.Equal("1", row["EmployeeID"]);
    }

    [Fact]
    public async Task CancellationInterruptsAnInFlightRead()
    {
        using var cts = new CancellationTokenSource();
        using var input = new BlockingReader();
        using var csv = new CsvRecordReader(input, new(), cts.Token);
        var pending = csv.ReadAsync();
        await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CancellationAlsoStopsBufferedRows()
    {
        using var cts = new CancellationTokenSource();
        using var input = new StringReader("id\n1\n2");
        using var csv = new CsvRecordReader(input, new(), cts.Token);
        Assert.Equal("1", (await csv.ReadAsync())!["id"]);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => csv.ReadAsync());
    }

    [Fact]
    public void PreCancelledReaderDoesNotReadOrOwnInput()
    {
        using var input = new StringReader("id\n1");
        Assert.ThrowsAny<OperationCanceledException>(() => new CsvRecordReader(input, new(), new CancellationToken(true)));
        Assert.Equal('i', (char)input.Read());
    }

    [Fact]
    public void DisposingReaderLeavesHostInputOpen()
    {
        using var input = new StringReader("id\n1");
        new CsvRecordReader(input, new()).Dispose();
        Assert.Equal('i', (char)input.Read());
    }

    [Fact]
    public async Task IoFailureIsNotEndOfFileAndDoesNotLeakSourceDetails()
    {
        using var input = new BrokenReader();
        using var csv = new CsvRecordReader(input, new());
        var error = await Assert.ThrowsAsync<CsvReadException>(() => csv.ReadAsync());
        Assert.Equal(CsvReadFailure.Unreadable, error.Reason);
        Assert.DoesNotContain("private", error.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\"")]
    public void InvalidDelimiterHasNamedReason(string delimiter)
    {
        using var input = new StringReader("id\n1");
        Assert.Equal(CsvReadFailure.InvalidConfiguration,
            Assert.Throws<CsvReadException>(() => new CsvRecordReader(input, new() { Delimiter = delimiter })).Reason);
    }

    private sealed class BlockingReader : TextReader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }
    private sealed class BrokenReader : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken ct = default) =>
            ValueTask.FromException<int>(new IOException("private file content"));
    }
}
