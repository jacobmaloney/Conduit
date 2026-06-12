using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.ActiveRoles;

/// <summary>
/// Phase 2 fast-read virtual-attribute source. Reads Active Roles virtual
/// attribute (VA) values straight out of the ARS config DB's <c>CVSAValues</c>
/// table — bypassing the AR Administration Service entirely — and joins them onto
/// AD objects by <c>objectGUID</c>.
///
/// CVSAValues schema (proven live on the 8.3 build, 2026-06-11):
///   directoryObjectGUID  uniqueidentifier  = the AD object's objectGUID (= the
///                                             SourceId the Conduit AD source emits).
///                                             THE JOIN KEY.
///   attributeSchemaIDGUID uniqueidentifier = the VA's schemaIDGUID (which VA).
///   valueBool / valueText / valueInt / valueLargeInt / valueDateTime
///                                           = the value, by VA syntax.
///   objectGUID                              = the row's own id (ignored).
///
/// VirtualSchema maps schemaIDGUID → (lDAPDisplayName, attributeSyntax/oMSyntax)
/// so we know which value column to read per VA. The map is read ONCE per run.
///
/// This is the speed crux: a single SQL round-trip resolves every VA for a whole
/// batch of objects, vs. the policy-mode path that binds EDMS://&lt;dn&gt; and
/// RefreshCache's each object through the AR service one at a time.
/// </summary>
internal sealed class CvsaValueReader
{
    private readonly string _connString;
    private readonly ILogger _logger;

    /// <summary>schemaIDGUID → (vaName, value-column selector). Built from VirtualSchema.</summary>
    private Dictionary<Guid, VaSchema>? _schemaMap;

    public CvsaValueReader(string connString, ILogger logger)
    {
        _connString = connString;
        _logger = logger;
    }

    /// <summary>
    /// One resolved virtual-attribute definition: its LDAP display name and the
    /// CVSAValues column its value lives in (chosen by attributeSyntax/oMSyntax).
    /// </summary>
    internal sealed record VaSchema(string Name, VaValueColumn ValueColumn);

    internal enum VaValueColumn { Bool, Text, Int, LargeInt, DateTime }

    /// <summary>
    /// Load schemaIDGUID → VA definition from VirtualSchema ONCE. Picks the value
    /// column by the directory attribute syntax: 2.5.5.8/oM1 = Boolean → valueBool;
    /// 2.5.5.12/oM64 = String → valueText; 2.5.5.9/oM2 = Integer → valueInt;
    /// 2.5.5.16/oM65 = LargeInteger → valueLargeInt; 2.5.5.11/oM23/24 =
    /// GeneralizedTime/UTCTime → valueDateTime. Anything else falls back to
    /// valueText (the safe stringly default).
    /// </summary>
    public async Task EnsureSchemaMapAsync(CancellationToken cancellationToken)
    {
        if (_schemaMap is not null) return;

        var map = new Dictionary<Guid, VaSchema>();
        await using var conn = new SqlConnection(_connString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT schemaIDGUID, lDAPDisplayName, attributeSyntax, oMSyntax FROM VirtualSchema";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0)) continue;
            var guid = reader.GetGuid(0);
            var name = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var syntax = reader.IsDBNull(2) ? null : reader.GetString(2);
            var oMSyntax = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);
            map[guid] = new VaSchema(name!, PickColumn(syntax, oMSyntax));
        }

        _schemaMap = map;
        _logger.LogInformation(
            "ARS fast read: loaded {Count} virtual-attribute definitions from VirtualSchema.", map.Count);
    }

    private static VaValueColumn PickColumn(string? attributeSyntax, int? oMSyntax) =>
        attributeSyntax switch
        {
            "2.5.5.8"  => VaValueColumn.Bool,      // Boolean
            "2.5.5.9"  => VaValueColumn.Int,       // Integer / Enumeration
            "2.5.5.16" => VaValueColumn.LargeInt,  // LargeInteger / Interval
            "2.5.5.11" => VaValueColumn.DateTime,  // Generalized/UTC Time
            "2.5.5.12" => VaValueColumn.Text,      // Unicode String
            "2.5.5.4"  => VaValueColumn.Text,      // Case-insensitive String
            "2.5.5.5"  => VaValueColumn.Text,      // IA5/Printable String
            "2.5.5.6"  => VaValueColumn.Text,      // Numeric String
            _ => oMSyntax switch
            {
                1  => VaValueColumn.Bool,
                2  => VaValueColumn.Int,
                65 => VaValueColumn.LargeInt,
                23 => VaValueColumn.DateTime,
                24 => VaValueColumn.DateTime,
                _  => VaValueColumn.Text,
            }
        };

    /// <summary>
    /// For a batch of objectGUIDs, return a dict objectGUID → (vaName → value).
    /// One SQL query (IN-list, auto-chunked) covers the whole batch. Values are
    /// typed per the VirtualSchema map; Boolean VAs come back as a CLR
    /// <see cref="bool"/> to MATCH what the EDMS:// (policy) path emits, so the
    /// downstream attribute mapping is identical regardless of read mode.
    /// VAs whose schemaIDGUID isn't in VirtualSchema (or whose mapped column is
    /// NULL for the row) are simply omitted.
    /// </summary>
    public async Task<Dictionary<Guid, Dictionary<string, object?>>> ReadVirtualAttributesAsync(
        IReadOnlyCollection<Guid> objectGuids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, Dictionary<string, object?>>();
        if (objectGuids.Count == 0) return result;
        await EnsureSchemaMapAsync(cancellationToken);
        var schemaMap = _schemaMap!;

        await using var conn = new SqlConnection(_connString);
        await conn.OpenAsync(cancellationToken);

        // Chunk the IN-list so the parameter count stays well under SQL Server's
        // 2100-parameter ceiling and the plan cache doesn't churn on huge lists.
        const int chunkSize = 1000;
        foreach (var chunk in Chunk(objectGuids, chunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var cmd = conn.CreateCommand();
            var paramNames = new List<string>(chunk.Count);
            for (var i = 0; i < chunk.Count; i++)
            {
                var p = "@g" + i;
                paramNames.Add(p);
                cmd.Parameters.Add(new SqlParameter(p, SqlDbType.UniqueIdentifier) { Value = chunk[i] });
            }
            cmd.CommandText =
                "SELECT directoryObjectGUID, attributeSchemaIDGUID, valueBool, valueText, " +
                "valueInt, valueLargeInt, valueDateTime " +
                "FROM CVSAValues WHERE directoryObjectGUID IN (" + string.Join(",", paramNames) + ")";

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                var dirGuid = reader.GetGuid(0);
                var schemaGuid = reader.GetGuid(1);
                if (!schemaMap.TryGetValue(schemaGuid, out var va)) continue; // unknown VA

                var value = va.ValueColumn switch
                {
                    VaValueColumn.Bool     => reader.IsDBNull(2) ? null : (object)reader.GetBoolean(2),
                    VaValueColumn.Text     => reader.IsDBNull(3) ? null : reader.GetString(3),
                    VaValueColumn.Int      => reader.IsDBNull(4) ? null : (object)reader.GetInt32(4),
                    VaValueColumn.LargeInt => reader.IsDBNull(5) ? null : (object)reader.GetInt64(5),
                    VaValueColumn.DateTime => reader.IsDBNull(6) ? null : (object)reader.GetDateTime(6),
                    _ => null
                };
                if (value is null) continue; // VA row exists but the mapped column is NULL for this object

                if (!result.TryGetValue(dirGuid, out var attrs))
                {
                    attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    result[dirGuid] = attrs;
                }
                attrs[va.Name] = value;
            }
        }

        return result;
    }

    /// <summary>Probe used by TestConnection: open the conn and read VirtualSchema count.</summary>
    public async Task<int> CountVirtualSchemaAsync(CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM VirtualSchema";
        var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
        return scalar is int n ? n : Convert.ToInt32(scalar);
    }

    private static IEnumerable<List<T>> Chunk<T>(IEnumerable<T> source, int size)
    {
        var bucket = new List<T>(size);
        foreach (var item in source)
        {
            bucket.Add(item);
            if (bucket.Count == size)
            {
                yield return bucket;
                bucket = new List<T>(size);
            }
        }
        if (bucket.Count > 0) yield return bucket;
    }
}
