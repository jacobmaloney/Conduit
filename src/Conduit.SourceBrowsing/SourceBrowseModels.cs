namespace Conduit.SourceBrowsing;

// Stable read-only wire contract. IDs are resolved by the execution host; callers never supply credentials or file paths.
public sealed record SourceBrowseSelection(Guid ConnectionId, Guid? ProjectId = null, Guid? StepId = null);
public sealed record SourceBrowseQuery(int Limit = 50, string? ObjectClass = null, string? BaseDn = null, string? Filter = null, IReadOnlyList<SourceMappingRule>? Mappings = null);
public sealed record SourceBrowseRequest(SourceBrowseSelection Selection, SourceBrowseQuery Query, Guid? ExpectedInstanceId = null);
public sealed record SourceBrowseStep(Guid Id, string Name);
public sealed record SourceBrowseField(string Name, string Value);
public sealed record SourceBrowseRow(int Number, string Label, IReadOnlyList<SourceBrowseField> Fields, IReadOnlyList<SourceMappedField>? MappedFields = null);

public sealed record SourceBrowseContext
{
    public SourceMappingContext? Mapping { get; init; }
    public string ConnectionName { get; init; } = "";
    public string ExecutionLocation { get; init; } = "";
    public Guid? ExecutionInstanceId { get; init; }
    public string? ProjectName { get; init; }
    public Guid? StepId { get; init; }
    public string ObjectClass { get; init; } = "User";
    public IReadOnlyList<string> ObjectClasses { get; init; } = [];
    public bool CanEditScope { get; init; }
    public bool CanBrowse { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<SourceBrowseStep> Steps { get; init; } = [];
    public IReadOnlyList<SourceBrowseField> Scope { get; init; } = [];
}

public sealed record SourceBrowsePage(SourceBrowseContext Context, IReadOnlyList<SourceBrowseRow> Rows,
    bool HasMore, DateTimeOffset ReadAtUtc, string Note = "Source sample before mappings. This does not run or change the sync project.");

public sealed class SourceBrowseException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class SourceBrowseLimits
{
    public const int MaximumRows = 200;
    public const int MaximumFields = 80;
    public const int MaximumCellCharacters = 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static void Validate(SourceBrowseQuery query)
    {
        if (query.Limit is < 1 or > MaximumRows)
            throw new SourceBrowseException("InvalidLimit", "Choose a sample size between 1 and 200 rows.");
        if (query.BaseDn?.Length > 2048 || query.Filter?.Length > 4096 || query.ObjectClass?.Length > 100)
            throw new SourceBrowseException("InvalidScope", "The browse scope is too long. Shorten the filter or container.");
    }
}
