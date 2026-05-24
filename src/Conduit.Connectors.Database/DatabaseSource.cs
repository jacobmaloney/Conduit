using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.Database;

/// <summary>
/// Database source. Executes the user-supplied UserQuery (or GroupQuery for
/// objectClass=Group). Must include a column named one of: objectGuid, id,
/// Id, EmployeeId, UserId, GroupId — used as the SourceId. Common column-name
/// fallbacks (FirstName → givenName, Email → mail, etc.) mirror IC for
/// downstream sink consistency.
/// </summary>
public sealed class DatabaseSource : IConnectorSource
{
    private readonly Guid _tenantId;
    private readonly CredentialProtector _protector;
    private readonly ILogger<DatabaseSource> _logger;

    public DatabaseSource(Guid tenantId, CredentialProtector protector, ILogger<DatabaseSource> logger)
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
        var creds = await DatabaseCredentialReader.ReadAsync(_protector, _tenantId)
            ?? throw new InvalidOperationException($"No 'database' credential for tenant {_tenantId}.");
        var query = string.Equals(objectClass, "Group", StringComparison.OrdinalIgnoreCase)
            ? creds.GroupQuery
            : creds.UserQuery;
        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogWarning("Database source: no {Class} query configured for tenant {TenantId}", objectClass, _tenantId);
            yield break;
        }

        using var conn = new SqlConnection(creds.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        var rows = await conn.QueryAsync(query, commandTimeout: creds.TimeoutSeconds);
        var emitted = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
            var dict = (IDictionary<string, object?>)row;
            var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in dict)
            {
                if (v is null || v is DBNull) continue;
                attrs[k] = v is string s ? s : v.ToString();
            }
            attrs["objectClass"] = objectClass.ToLowerInvariant();

            var sourceId = Pick(attrs, "objectGuid", "id", "Id", "EmployeeId", "UserId", "GroupId");
            if (string.IsNullOrEmpty(sourceId)) continue;
            attrs["objectGuid"] = sourceId;
            attrs["id"] = sourceId;

            // Common-name aliases (IC parity).
            Alias(attrs, "FirstName", "givenName");
            Alias(attrs, "LastName", "sn");
            Alias(attrs, "LastName", "surname");
            Alias(attrs, "LastName", "familyName");
            Alias(attrs, "Email", "mail");
            Alias(attrs, "Email", "email");
            Alias(attrs, "DisplayName", "displayName");
            Alias(attrs, "Department", "department");
            Alias(attrs, "Title", "title");
            Alias(attrs, "Title", "jobTitle");
            Alias(attrs, "Phone", "telephoneNumber");
            Alias(attrs, "Manager", "manager");
            Alias(attrs, "Username", "userName");
            Alias(attrs, "Username", "sAMAccountName");
            Alias(attrs, "Company", "company");
            Alias(attrs, "Company", "companyName");
            if (!attrs.ContainsKey("cn"))
                attrs["cn"] = (attrs.TryGetValue("displayName", out var dn) ? dn : null) ?? sourceId;

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
            var creds = await DatabaseCredentialReader.ReadAsync(_protector, _tenantId);
            if (creds is null) return new ConnectorTestResult { IsSuccessful = false, Message = "No 'database' credential stored." };
            using var conn = new SqlConnection(creds.ConnectionString);
            await conn.OpenAsync(cancellationToken);
            var info = await conn.ExecuteScalarAsync<string>("SELECT @@VERSION;");
            return new ConnectorTestResult { IsSuccessful = true, Message = $"Connected. {info?.Split('\n')[0]}" };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

    private static string? Pick(IDictionary<string, object?> dict, params string[] keys)
    {
        foreach (var k in keys)
            if (dict.TryGetValue(k, out var v) && v is not null) return v.ToString();
        return null;
    }

    private static void Alias(IDictionary<string, object?> dict, string from, string to)
    {
        if (dict.ContainsKey(to)) return;
        if (dict.TryGetValue(from, out var v) && v is not null) dict[to] = v;
    }
}
