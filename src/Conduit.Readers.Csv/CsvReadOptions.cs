namespace Conduit.Readers.Csv;

/// <summary>Parsing policy only. Hosts retain file access, encoding and row conversion.</summary>
public sealed record CsvReadOptions
{
    public string Delimiter { get; init; } = ",";
    public bool HasHeaderRow { get; init; } = true;
    public bool TrimFields { get; init; }
}
