using Conduit.Connectors.Csv;
using Conduit.Connectors.IdentityCenter;
using Conduit.Core.Models;
using Conduit.Core.SyncModels;
using Conduit.Mapping;
using Conduit.Sync.Connectors;

namespace Conduit.Web.Services;

/// <summary>
/// The mapping for a file of people going to IdentityCenter, proposed from what both ends really have: the
/// file's own header row, and the person fields IdentityCenter says it accepts. The class template assumed
/// conventional column names instead, so a project made from a real HR file mapped columns the file did not
/// have (ManagerId, UserPrincipalName, PhoneNumber) and left out ones it did (StartDate, EndDate, City), and
/// could not offer a person's type or dates as targets at all.
/// </summary>
public sealed class FileToPeopleMapping(ConnectorRegistry connectors, IIdentityCenterSourceCatalogClient identityCenter)
{
    /// <summary>The file's columns, the person fields the target accepts, the proposed rows, and the columns left out.</summary>
    public sealed record Result(IReadOnlyList<string> Columns, IReadOnlyList<string> Targets,
        IReadOnlyList<AttributeMapping> Mappings, IReadOnlyList<string> Unmapped)
    {
        /// <summary>The first rows of the file, for the builder's sample values and the preview.</summary>
        public IReadOnlyList<IReadOnlyDictionary<string, string?>> SampleRows { get; init; } = Array.Empty<IReadOnlyDictionary<string, string?>>();

        /// <summary>The values the target keeps a list of, by person field (a person's type: Employee, Contractor).</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> TargetValues { get; init; } =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The file's columns as the builder offers them: a sample value, and the distinct values seen when they are few.</summary>
        public IReadOnlyList<IdentityCenter.Brand.Models.BrandValueField> Fields => SourceSampleReader.Fields(Columns, SampleRows);
    }

    /// <summary>How many rows of the file are read for samples.</summary>
    public const int SampleRowCount = 500;


    /// <summary>True for a CSV file source writing people (not accounts) into IdentityCenter.</summary>
    public static bool Applies(Tenant? source, Tenant? sink, string? sinkTable) =>
        source is not null && sink is not null
        && string.Equals(source.SystemType, "CSV", StringComparison.OrdinalIgnoreCase)
        && sink.SystemType is "IdentityCenter" or "CertificationCenterSaaS"
        && string.Equals(sinkTable, "Identities", StringComparison.OrdinalIgnoreCase);

    public async Task<Result> ProposeAsync(Tenant source, Tenant sink, string? sinkCredentialName, CancellationToken ct)
    {
        var adapter = connectors.Get(source.SystemType) as CsvAdapter
            ?? throw new InvalidOperationException("The CSV connector is not available on this server.");
        var columns = await adapter.ReadHeaderAsync(source.Id, ct);
        var schema = await identityCenter.MappingSchemaAsync(sink.Id, "Identities", sinkCredentialName, ct);
        var sample = await adapter.ReadSampleAsync(source.Id, SampleRowCount, ct);
        return Build(columns, schema.Targets.Select(t => t.Name)) with
        {
            SampleRows = sample,
            TargetValues = schema.Targets.Where(t => t.Values is { Count: > 0 })
                .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Values!, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>The proposal itself, with nothing read: rows in file order, the key first-class and required.</summary>
    public static Result Build(IReadOnlyList<string> columns, IEnumerable<string> targets)
    {
        var targetList = targets.Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        var proposal = ColumnTargetProposal.Propose(columns, targetList,
            ColumnTargetProposal.PersonAliases, ColumnTargetProposal.PersonAlsoFills);
        var mappings = proposal.Rows.Select((row, index) => new AttributeMapping
        {
            SourceAttribute = row.Column,
            SinkAttribute = row.Target,
            // The person's key is the one field a row cannot be written without.
            IsRequired = string.Equals(row.Target, "EmployeeId", StringComparison.OrdinalIgnoreCase),
            SortOrder = index,
        }).ToList();
        return new Result(columns, targetList, mappings, proposal.Unmapped);
    }

    /// <summary>One sentence for the operator: how many columns were mapped and which were left out.</summary>
    public static string Summary(Result result) =>
        string.Concat("Mapped ", result.Mappings.Select(m => m.SourceAttribute).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString(),
            " of ", result.Columns.Count.ToString(), " columns from the file's header.",
            result.Unmapped.Count == 0 ? string.Empty : string.Concat(" Not mapped: ", string.Join(", ", result.Unmapped), "."));
}
