namespace Conduit.Readers.Csv;

public enum CsvReadFailure { InvalidConfiguration, InvalidHeaders, InvalidData, Unreadable }

/// <summary>Safe diagnostic: never carries raw CSV data, parser context or an inner exception.</summary>
public sealed class CsvReadException : Exception
{
    public CsvReadFailure Reason { get; }

    public CsvReadException(CsvReadFailure reason) : base(reason switch
    {
        CsvReadFailure.InvalidConfiguration => "CSV delimiter configuration is invalid.",
        CsvReadFailure.InvalidHeaders => "CSV headers must be non-empty and unique. Correct duplicate or blank column names.",
        CsvReadFailure.InvalidData => "CSV could not be read. Check column counts, delimiter and quoting.",
        _ => "CSV could not be read. Check file access and encoding."
    }) => Reason = reason;
}
