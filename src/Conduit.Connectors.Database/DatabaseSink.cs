using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.Database;

/// <summary>
/// Database sink. Executes the user-supplied UpsertUserStatement (or
/// UpsertGroupStatement) with the ConnectorObject attributes bound as named
/// parameters. The user is responsible for MERGE / IF EXISTS patterns. Common
/// parameters: @objectGuid, @userName, @displayName, @mail, @firstName,
/// @lastName, etc.
/// </summary>
public sealed class DatabaseSink : IConnectorSink
{
    private readonly Guid _tenantId;
    private readonly CredentialProtector _protector;
    private readonly ILogger<DatabaseSink> _logger;

    public DatabaseSink(Guid tenantId, CredentialProtector protector, ILogger<DatabaseSink> logger)
    {
        _tenantId = tenantId;
        _protector = protector;
        _logger = logger;
    }

    public async Task<SinkWriteResult> UpsertAsync(ConnectorObject obj, CancellationToken cancellationToken)
    {
        try
        {
            var creds = await DatabaseCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException($"No 'database' credential for tenant {_tenantId}.");
            var stmt = string.Equals(obj.ObjectClass, "Group", StringComparison.OrdinalIgnoreCase)
                ? creds.UpsertGroupStatement
                : creds.UpsertUserStatement;
            if (string.IsNullOrWhiteSpace(stmt))
                return SinkWriteResult.Ok(SinkWriteOutcome.Skipped);

            var parameters = new DynamicParameters();
            parameters.Add("@objectGuid", obj.SourceId);
            parameters.Add("@sourceId", obj.SourceId);
            foreach (var (k, v) in obj.Attributes)
                parameters.Add($"@{k}", v is null ? null : (v is string s ? s : v.ToString()));

            using var conn = new SqlConnection(creds.ConnectionString);
            await conn.OpenAsync(cancellationToken);
            var rows = await conn.ExecuteAsync(stmt, parameters, commandTimeout: creds.TimeoutSeconds);
            // We can't tell create-vs-update generically; treat 0-or-1 rows
            // as Updated, >1 as Created (MERGE often returns affected rows).
            return SinkWriteResult.Ok(rows > 0 ? SinkWriteOutcome.Updated : SinkWriteOutcome.Skipped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database sink upsert failed for {SourceId}", obj.SourceId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    public async Task<SinkWriteResult> DeleteAsync(string sourceId, CancellationToken cancellationToken)
    {
        try
        {
            var creds = await DatabaseCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException($"No 'database' credential for tenant {_tenantId}.");
            if (string.IsNullOrWhiteSpace(creds.DeleteStatement))
                return SinkWriteResult.Ok(SinkWriteOutcome.Skipped);
            using var conn = new SqlConnection(creds.ConnectionString);
            await conn.OpenAsync(cancellationToken);
            var rows = await conn.ExecuteAsync(creds.DeleteStatement,
                new { objectGuid = sourceId, sourceId },
                commandTimeout: creds.TimeoutSeconds);
            return SinkWriteResult.Ok(rows > 0 ? SinkWriteOutcome.Updated : SinkWriteOutcome.Skipped);
        }
        catch (Exception ex)
        {
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var src = new DatabaseSource(_tenantId, _protector,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseSource>.Instance);
        return await src.TestConnectionAsync(cancellationToken);
    }
}
