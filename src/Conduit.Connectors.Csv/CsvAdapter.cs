using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Conduit.Readers.Csv;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.Csv;

/// <summary>
/// CSV adapter — source-only realistically (sink writes to file, but is rarely
/// useful as a target). Credentials are file path + optional delimiter +
/// optional encoding + optional id column.
/// </summary>
public sealed class CsvAdapter : IConnectorAdapter
{
    public string SystemType => "CSV";
    public string DisplayName => "CSV File";
    public bool SupportsSource => true;
    public bool SupportsSink => false;

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes { get; } = new[]
    {
        new CredentialTypeInfo
        {
            Name = "csv",
            DisplayName = "CSV File",
            Description = "Flat-file source. Path + delimiter + header flag + which column is the object id.",
            Fields = new[]
            {
                new CredentialFieldSpec { Key = "FilePath", Label = "File Path", IsRequired = true, Placeholder = @"C:\path\to\users.csv" },
                new CredentialFieldSpec { Key = "Delimiter", Label = "Delimiter", Placeholder = ",", DefaultValue = "," },
                new CredentialFieldSpec { Key = "HasHeader", Label = "First row is header", IsBoolean = true, DefaultValue = "true" },
                new CredentialFieldSpec { Key = "IdColumn", Label = "ID Column (column to use as objectGuid)", Placeholder = "EmployeeId" },
            }
        }
    };

    private readonly CredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public CsvAdapter(CredentialProtector protector, ILoggerFactory loggerFactory)
    {
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new CsvSource(tenantId, _protector, _loggerFactory.CreateLogger<CsvSource>());

    public IConnectorSink? CreateSink(Guid tenantId) => null;
}

internal sealed record CsvCredentials(string FilePath, string Delimiter, bool HasHeader, string Encoding, string? IdColumn);

internal static class CsvCredentialReader
{
    public const string CredentialName = "csv";

    public static async Task<CsvCredentials?> ReadAsync(CredentialProtector p, Guid tenantId)
    {
        // Phase 3: honor per-project credential-name override (source first, sink fallback).
        var name = CredentialNameContext.Resolve(CredentialName, CredentialSide.Source);
        var raw = await p.RetrieveAsync(tenantId, name);
        if (string.IsNullOrEmpty(raw))
        {
            var sinkName = CredentialNameContext.Resolve(CredentialName, CredentialSide.Sink);
            if (!string.Equals(sinkName, name, StringComparison.OrdinalIgnoreCase))
                raw = await p.RetrieveAsync(tenantId, sinkName);
        }
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var fp = doc.RootElement.TryGetProperty("FilePath", out var fpEl) ? fpEl.GetString() : null;
            if (string.IsNullOrEmpty(fp)) return null;
            return new CsvCredentials(
                fp!,
                doc.RootElement.TryGetProperty("Delimiter", out var dEl) ? (dEl.GetString() ?? ",") : ",",
                !doc.RootElement.TryGetProperty("HasHeader", out var hEl) || hEl.ValueKind != JsonValueKind.False,
                doc.RootElement.TryGetProperty("Encoding", out var eEl) ? (eEl.GetString() ?? "UTF-8") : "UTF-8",
                doc.RootElement.TryGetProperty("IdColumn", out var iEl) ? iEl.GetString() : null);
        }
        catch { return null; }
    }
}

public sealed class CsvSource : IConnectorSource
{
    private readonly Guid _tenantId;
    private readonly CredentialProtector _protector;
    private readonly ILogger<CsvSource> _logger;

    public CsvSource(Guid tenantId, CredentialProtector protector, ILogger<CsvSource> logger)
    {
        _tenantId = tenantId;
        _protector = protector;
        _logger = logger;
    }

    public async IAsyncEnumerable<ConnectorObject> ReadAsync(
        string objectClass,
        SyncProjectScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var creds = await CsvCredentialReader.ReadAsync(_protector, _tenantId);
        cancellationToken.ThrowIfCancellationRequested();
        if (creds is null) throw new InvalidOperationException($"No 'csv' credential for tenant {_tenantId}.");
        if (!File.Exists(creds.FilePath))
            throw new FileNotFoundException($"CSV file not found at {creds.FilePath}.", creds.FilePath);

        using var reader = new StreamReader(creds.FilePath, ResolveEncoding(creds.Encoding));
        using var csv = CreateReader(reader, creds, cancellationToken);
        var idCol = creds.IdColumn;
        var emitted = 0;
        var rowIndex = 0;
        // MaxObjects bounds the selected population; do not parse an extra record
        // beyond that scope. IC preview separately needs a lookahead for truncation.
        while ((!scope.MaxObjects.HasValue || emitted < scope.MaxObjects.Value) &&
               await csv.ReadAsync() is { } row)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowIndex++;
            var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["objectClass"] = objectClass.ToLowerInvariant()
            };
            foreach (var field in row)
                if (!string.IsNullOrEmpty(field.Value)) attrs[field.Key] = field.Value;
            string sourceId = null!;
            if (!string.IsNullOrEmpty(idCol) && attrs.TryGetValue(idCol, out var idVal))
                sourceId = idVal?.ToString() ?? string.Empty;
            sourceId ??= attrs.TryGetValue("objectGuid", out var og) ? og?.ToString() ?? string.Empty
                     : attrs.TryGetValue("id", out var idv) ? idv?.ToString() ?? string.Empty
                     : attrs.TryGetValue("EmployeeId", out var eid) ? eid?.ToString() ?? string.Empty
                     : $"row-{rowIndex}";

            attrs["objectGuid"] = sourceId;
            attrs["id"] = sourceId;
            emitted++;
            yield return new ConnectorObject
            {
                SourceId = sourceId!,
                ObjectClass = objectClass,
                Attributes = attrs
            };
        }
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var creds = await CsvCredentialReader.ReadAsync(_protector, _tenantId);
            cancellationToken.ThrowIfCancellationRequested();
            if (creds is null) return new ConnectorTestResult { IsSuccessful = false, Message = "No 'csv' credential stored." };
            if (!File.Exists(creds.FilePath))
                return new ConnectorTestResult { IsSuccessful = false, Message = $"File not found: {creds.FilePath}" };
            using var input = new StreamReader(creds.FilePath, ResolveEncoding(creds.Encoding));
            using var csv = CreateReader(input, creds, cancellationToken);
            if (await csv.ReadAsync() == null)
                return new ConnectorTestResult { IsSuccessful = false, Message = "CSV has no data rows. Add a data row before testing." };
            return new ConnectorTestResult { IsSuccessful = true, Message = $"CSV sample readable: {csv.FieldNames.Count} columns and first data row checked. Remaining rows are checked during sync." };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("CSV sample read failed ({ErrorType})", ex.GetType().Name);
            return new ConnectorTestResult { IsSuccessful = false, Message = ex is CsvReadException csv ? csv.Message : "CSV could not be read. Check file access and encoding." };
        }
    }

    private static CsvRecordReader CreateReader(TextReader input, CsvCredentials creds, CancellationToken ct) =>
        new(input, new CsvReadOptions
        {
            Delimiter = string.IsNullOrEmpty(creds.Delimiter) ? "," : creds.Delimiter,
            HasHeaderRow = creds.HasHeader,
            TrimFields = false
        }, ct);

    private static Encoding ResolveEncoding(string name) =>
        name.ToUpperInvariant() switch
        {
            "UTF-8" or "UTF8" => Encoding.UTF8,
            "UTF-16" or "UTF16" => Encoding.Unicode,
            "ASCII" => Encoding.ASCII,
            "WINDOWS-1252" or "CP1252" => Encoding.GetEncoding("Windows-1252"),
            _ => Encoding.UTF8
        };
}
