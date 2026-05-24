using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.Database;

/// <summary>
/// Generic SQL Server database adapter — source AND sink via user-supplied
/// SELECT / MERGE statements. Credentials: { ConnectionString, UserQuery,
/// GroupQuery?, UpsertUserStatement?, UpsertGroupStatement?, DeleteStatement?,
/// TimeoutSeconds? } under "database". Phase 1.5 ships SQL Server only;
/// PostgreSQL / MySQL planned via the same adapter shape.
/// </summary>
public sealed class DatabaseAdapter : IConnectorAdapter
{
    public string SystemType => "Database";
    public string DisplayName => "Database (SQL)";
    public bool SupportsSource => true;
    public bool SupportsSink => true;

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes { get; } = new[]
    {
        new CredentialTypeInfo
        {
            Name = "database",
            DisplayName = "SQL Database",
            Description = "Connection string + user-supplied SELECT / MERGE statements. SQL Server today.",
            Fields = new[]
            {
                new CredentialFieldSpec { Key = "ConnectionString", Label = "Connection String", IsRequired = true, IsSecret = true, Placeholder = "Server=...;Database=...;User Id=...;Password=...;Encrypt=true;" },
                new CredentialFieldSpec { Key = "UserQuery", Label = "User Query (SELECT, must yield objectGuid)", IsMultiline = true },
                new CredentialFieldSpec { Key = "GroupQuery", Label = "Group Query (optional)", IsMultiline = true },
                new CredentialFieldSpec { Key = "UpsertUserStatement", Label = "Upsert User Statement (optional — sink)", IsMultiline = true, Placeholder = "MERGE / INSERT / UPDATE with @objectGuid, @userName, ..." },
            }
        }
    };

    private readonly CredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public DatabaseAdapter(CredentialProtector protector, ILoggerFactory loggerFactory)
    {
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    public IConnectorSource? CreateSource(Guid tenantId) =>
        new DatabaseSource(tenantId, _protector, _loggerFactory.CreateLogger<DatabaseSource>());

    public IConnectorSink? CreateSink(Guid tenantId) =>
        new DatabaseSink(tenantId, _protector, _loggerFactory.CreateLogger<DatabaseSink>());
}

internal sealed record DatabaseCredentials(
    string ConnectionString,
    string? UserQuery,
    string? GroupQuery,
    string? UpsertUserStatement,
    string? UpsertGroupStatement,
    string? DeleteStatement,
    int TimeoutSeconds);

internal static class DatabaseCredentialReader
{
    public const string CredentialName = "database";

    public static async Task<DatabaseCredentials?> ReadAsync(CredentialProtector p, Guid tenantId)
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
            var cs = doc.RootElement.TryGetProperty("ConnectionString", out var csEl) ? csEl.GetString() : null;
            if (string.IsNullOrEmpty(cs)) return null;
            return new DatabaseCredentials(
                cs!,
                doc.RootElement.TryGetProperty("UserQuery", out var uqEl) ? uqEl.GetString() : null,
                doc.RootElement.TryGetProperty("GroupQuery", out var gqEl) ? gqEl.GetString() : null,
                doc.RootElement.TryGetProperty("UpsertUserStatement", out var uuEl) ? uuEl.GetString() : null,
                doc.RootElement.TryGetProperty("UpsertGroupStatement", out var ugEl) ? ugEl.GetString() : null,
                doc.RootElement.TryGetProperty("DeleteStatement", out var dsEl) ? dsEl.GetString() : null,
                doc.RootElement.TryGetProperty("TimeoutSeconds", out var tsEl) && tsEl.TryGetInt32(out var ts) ? ts : 120);
        }
        catch { return null; }
    }
}
