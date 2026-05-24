using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using CsvHelper;
using CsvHelper.Configuration;
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
        var creds = await CsvCredentialReader.ReadAsync(_protector, _tenantId)
            ?? throw new InvalidOperationException($"No 'csv' credential for tenant {_tenantId}.");
        if (!File.Exists(creds.FilePath))
            throw new FileNotFoundException($"CSV file not found at {creds.FilePath}.", creds.FilePath);

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = string.IsNullOrEmpty(creds.Delimiter) ? "," : creds.Delimiter,
            HasHeaderRecord = creds.HasHeader,
            MissingFieldFound = null,
            BadDataFound = null,
            HeaderValidated = null
        };
        var encoding = ResolveEncoding(creds.Encoding);
        using var reader = new StreamReader(creds.FilePath, encoding);
        using var csv = new CsvReader(reader, config);
        if (creds.HasHeader)
        {
            await csv.ReadAsync();
            csv.ReadHeader();
        }
        var idCol = creds.IdColumn;
        var emitted = 0;
        var rowIndex = 0;
        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
            rowIndex++;
            var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["objectClass"] = objectClass.ToLowerInvariant()
            };
            if (csv.HeaderRecord is not null)
            {
                foreach (var h in csv.HeaderRecord)
                {
                    var val = csv.GetField(h);
                    if (!string.IsNullOrEmpty(val)) attrs[h] = val;
                }
            }
            else
            {
                for (int i = 0; csv.TryGetField<string>(i, out var v); i++)
                    if (!string.IsNullOrEmpty(v)) attrs[$"col{i}"] = v;
            }
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
        try
        {
            var creds = await CsvCredentialReader.ReadAsync(_protector, _tenantId);
            if (creds is null) return new ConnectorTestResult { IsSuccessful = false, Message = "No 'csv' credential stored." };
            if (!File.Exists(creds.FilePath))
                return new ConnectorTestResult { IsSuccessful = false, Message = $"File not found: {creds.FilePath}" };
            return new ConnectorTestResult { IsSuccessful = true, Message = $"File readable: {creds.FilePath}" };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

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
