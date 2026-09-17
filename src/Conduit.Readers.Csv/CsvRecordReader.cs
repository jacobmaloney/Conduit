using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace Conduit.Readers.Csv;

/// <summary>
/// Single-pass reader shared by both hosts. Calls must be sequential. The caller owns
/// the TextReader and must dispose it, including after cancellation or early stopping.
/// Values remain strings (including empty strings); hosts choose their null/omission policy.
/// Headerless columns are named col0, col1, etc. Invalid records throw, never signal EOF.
/// </summary>
public sealed class CsvRecordReader : IDisposable
{
    private readonly CsvReader _csv;
    private readonly bool _hasHeader;
    private readonly CancellationToken _ct;
    private bool _initialized;
    private IReadOnlyList<string> _fields = Array.Empty<string>();

    public IReadOnlyList<string> FieldNames => _fields;

    public CsvRecordReader(TextReader input, CsvReadOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(options.Delimiter))
            throw new CsvReadException(CsvReadFailure.InvalidConfiguration);
        _ct = ct;
        _hasHeader = options.HasHeaderRow;
        try
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                Delimiter = options.Delimiter,
                HasHeaderRecord = options.HasHeaderRow,
                ExceptionMessagesContainRawData = false,
                DetectColumnCountChanges = true,
                LineBreakInQuotedFieldIsBadData = false,
                TrimOptions = options.TrimFields ? TrimOptions.Trim : TrimOptions.None
            };
            // CsvHelper's TextReader overload has no token. Forward its buffer reads
            // through the cancellation-aware runtime overload instead of merely checking
            // cancellation between records (a stream read itself can be blocked).
            _csv = new CsvReader(new CancellableReader(input, ct), config, leaveOpen: true);
        }
        catch (ArgumentException) { throw new CsvReadException(CsvReadFailure.InvalidConfiguration); }
        catch (CsvHelperException) { throw new CsvReadException(CsvReadFailure.InvalidConfiguration); }
    }

    public async Task<IReadOnlyList<string>> ReadHeaderAsync()
    {
        _ct.ThrowIfCancellationRequested();
        if (_initialized) return _fields;
        if (_hasHeader && await ReadNextAsync())
        {
            try
            {
                _csv.ReadHeader();
                var headers = _csv.HeaderRecord ?? Array.Empty<string>();
                if (headers.Any(string.IsNullOrWhiteSpace) ||
                    headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length)
                    throw new CsvReadException(CsvReadFailure.InvalidHeaders);
                _fields = Array.AsReadOnly(headers.ToArray());
            }
            catch (CsvHelperException) { throw new CsvReadException(CsvReadFailure.InvalidData); }
        }
        _initialized = true;
        return _fields;
    }

    public async Task<IReadOnlyDictionary<string, string?>?> ReadAsync()
    {
        await ReadHeaderAsync();
        if (!await ReadNextAsync()) return null;
        try
        {
            if (!_hasHeader && _fields.Count == 0)
                _fields = Array.AsReadOnly(Enumerable.Range(0, _csv.Parser.Count).Select(i => $"col{i}").ToArray());
            var row = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var i = 0; i < _fields.Count; i++) row.Add(_fields[i], _csv.GetField(i));
            _ct.ThrowIfCancellationRequested();
            return row;
        }
        catch (CsvHelperException) { throw new CsvReadException(CsvReadFailure.InvalidData); }
    }

    private async Task<bool> ReadNextAsync()
    {
        _ct.ThrowIfCancellationRequested();
        try
        {
            var found = await _csv.ReadAsync();
            _ct.ThrowIfCancellationRequested();
            return found;
        }
        catch (Exception) when (_ct.IsCancellationRequested) { throw new OperationCanceledException(_ct); }
        catch (CsvHelperException) { throw new CsvReadException(CsvReadFailure.InvalidData); }
        catch (IOException) { throw new CsvReadException(CsvReadFailure.Unreadable); }
    }

    public void Dispose() => _csv.Dispose();

    private sealed class CancellableReader(TextReader input, CancellationToken ct) : TextReader
    {
        public override async Task<int> ReadAsync(char[] buffer, int index, int count)
        {
            ct.ThrowIfCancellationRequested();
            return await input.ReadAsync(buffer.AsMemory(index, count), ct);
        }
        public override int Read(char[] buffer, int index, int count)
        {
            ct.ThrowIfCancellationRequested();
            return input.Read(buffer, index, count);
        }
    }
}
