using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Conduit.Web.Models;

namespace Conduit.DataAccess.Repositories;

/// <summary>
/// Dapper data access for the AuditLogs table. AuditLogService keeps the
/// public surface (LogAsync, statistics shaping); this repository owns the SQL.
/// </summary>
public class AuditLogRepository : BaseRepository
{
    public AuditLogRepository(DatabaseConfig config) : base(config) { }

    public Task InsertAsync(AuditLog log) =>
        ExecuteAsync(@"
            INSERT INTO AuditLogs (
                Id, Timestamp, Action, ResourceType, ResourceId, UserId, UserName,
                IpAddress, UserAgent, StatusCode, Details, OldValue, NewValue, Duration
            ) VALUES (
                @Id, @Timestamp, @Action, @ResourceType, @ResourceId, @UserId, @UserName,
                @IpAddress, @UserAgent, @StatusCode, @Details, @OldValue, @NewValue, @Duration
            );",
            log);

    public async Task<(List<AuditLog> Logs, int TotalCount)> GetPagedAsync(
        DateTime? startDate, DateTime? endDate, string? action,
        string? resourceType, string? userId, int startRow, int endRow)
    {
        const string sql = @"
            SELECT * FROM (
                SELECT *, ROW_NUMBER() OVER (ORDER BY Timestamp DESC) AS RowNum
                FROM AuditLogs
                WHERE (@StartDate IS NULL OR Timestamp >= @StartDate)
                AND (@EndDate IS NULL OR Timestamp <= @EndDate)
                AND (@Action IS NULL OR Action = @Action)
                AND (@ResourceType IS NULL OR ResourceType = @ResourceType)
                AND (@UserId IS NULL OR UserId = @UserId)
            ) AS Results
            WHERE RowNum BETWEEN @StartRow AND @EndRow;

            SELECT COUNT(*)
            FROM AuditLogs
            WHERE (@StartDate IS NULL OR Timestamp >= @StartDate)
            AND (@EndDate IS NULL OR Timestamp <= @EndDate)
            AND (@Action IS NULL OR Action = @Action)
            AND (@ResourceType IS NULL OR ResourceType = @ResourceType)
            AND (@UserId IS NULL OR UserId = @UserId);";

        using var multi = await QueryMultipleAsync(sql, new
        {
            StartDate = startDate,
            EndDate = endDate,
            Action = action,
            ResourceType = resourceType,
            UserId = userId,
            StartRow = startRow,
            EndRow = endRow
        });

        var logs = (await multi.ReadAsync<AuditLog>()).ToList();
        var totalCount = await multi.ReadFirstAsync<int>();
        return (logs, totalCount);
    }

    public async Task<(dynamic Stats, IEnumerable<(string Action, int Count)> ActionCounts, IEnumerable<(string ResourceType, int Count)> ResourceCounts)>
        GetStatisticsAsync(DateTime? startDate, DateTime? endDate)
    {
        const string sql = @"
            SELECT
                COUNT(*) as TotalEvents,
                COUNT(DISTINCT UserId) as UniqueUsers,
                COUNT(DISTINCT IpAddress) as UniqueIpAddresses,
                AVG(DATEDIFF(MILLISECOND, '00:00:00', Duration)) as AvgDurationMs
            FROM AuditLogs
            WHERE (@StartDate IS NULL OR Timestamp >= @StartDate)
            AND (@EndDate IS NULL OR Timestamp <= @EndDate);

            SELECT Action, COUNT(*) as Count
            FROM AuditLogs
            WHERE (@StartDate IS NULL OR Timestamp >= @StartDate)
            AND (@EndDate IS NULL OR Timestamp <= @EndDate)
            GROUP BY Action
            ORDER BY Count DESC;

            SELECT ResourceType, COUNT(*) as Count
            FROM AuditLogs
            WHERE (@StartDate IS NULL OR Timestamp >= @StartDate)
            AND (@EndDate IS NULL OR Timestamp <= @EndDate)
            GROUP BY ResourceType
            ORDER BY Count DESC;";

        using var multi = await QueryMultipleAsync(sql, new { StartDate = startDate, EndDate = endDate });

        var stats = await multi.ReadFirstAsync<dynamic>();
        var actionCounts = await multi.ReadAsync<(string Action, int Count)>();
        var resourceCounts = await multi.ReadAsync<(string ResourceType, int Count)>();
        return (stats, actionCounts, resourceCounts);
    }

    public Task<int> DeleteOlderThanAsync(DateTime cutoffDate) =>
        ExecuteAsync("DELETE FROM AuditLogs WHERE Timestamp < @CutoffDate;",
            new { CutoffDate = cutoffDate });
}
