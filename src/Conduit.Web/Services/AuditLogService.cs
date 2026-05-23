using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Conduit.DataAccess;
using Conduit.Web.Models;

namespace Conduit.Web.Services
{
    /// <summary>
    /// Service for managing audit logs
    /// </summary>
    public class AuditLogService
    {
        private readonly DatabaseConfig _databaseConfig;

        public AuditLogService(DatabaseConfig databaseConfig)
        {
            _databaseConfig = databaseConfig;
        }

        /// <summary>
        /// Logs an audit event
        /// </summary>
        public async Task LogAsync(string action, string resourceType, string? resourceId = null,
            string? userId = null, string? userName = null, string? details = null,
            string? oldValue = null, string? newValue = null, int? statusCode = null,
            string? ipAddress = null, string? userAgent = null, TimeSpan? duration = null)
        {
            var log = new AuditLog
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTime.UtcNow,
                Action = action,
                ResourceType = resourceType,
                ResourceId = resourceId,
                UserId = userId,
                UserName = userName,
                Details = details,
                OldValue = oldValue,
                NewValue = newValue,
                StatusCode = statusCode,
                IpAddress = ipAddress,
                UserAgent = userAgent,
                Duration = duration
            };

            await InsertLogAsync(log);
        }

        /// <summary>
        /// Inserts an audit log entry
        /// </summary>
        private async Task InsertLogAsync(AuditLog log)
        {
            var sql = @"
                INSERT INTO AuditLogs (
                    Id, Timestamp, Action, ResourceType, ResourceId, UserId, UserName,
                    IpAddress, UserAgent, StatusCode, Details, OldValue, NewValue, Duration
                ) VALUES (
                    @Id, @Timestamp, @Action, @ResourceType, @ResourceId, @UserId, @UserName,
                    @IpAddress, @UserAgent, @StatusCode, @Details, @OldValue, @NewValue, @Duration
                );";

            using var connection = new SqlConnection(_databaseConfig.ConnectionString);
            await connection.ExecuteAsync(sql, log);
        }

        /// <summary>
        /// Gets audit logs with filtering and pagination
        /// </summary>
        public async Task<(List<AuditLog> Logs, int TotalCount)> GetLogsAsync(
            DateTime? startDate = null,
            DateTime? endDate = null,
            string? action = null,
            string? resourceType = null,
            string? userId = null,
            int page = 1,
            int pageSize = 50)
        {
            var sql = @"
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

            var startRow = (page - 1) * pageSize + 1;
            var endRow = page * pageSize;

            using var connection = new SqlConnection(_databaseConfig.ConnectionString);
            using var multi = await connection.QueryMultipleAsync(sql, new
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

        /// <summary>
        /// Gets audit log statistics
        /// </summary>
        public async Task<AuditLogStatistics> GetStatisticsAsync(DateTime? startDate = null, DateTime? endDate = null)
        {
            var sql = @"
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

            using var connection = new SqlConnection(_databaseConfig.ConnectionString);
            using var multi = await connection.QueryMultipleAsync(sql, new
            {
                StartDate = startDate,
                EndDate = endDate
            });

            var stats = await multi.ReadFirstAsync<dynamic>();
            var actionCounts = await multi.ReadAsync<(string Action, int Count)>();
            var resourceCounts = await multi.ReadAsync<(string ResourceType, int Count)>();

            return new AuditLogStatistics
            {
                TotalEvents = stats.TotalEvents,
                UniqueUsers = stats.UniqueUsers,
                UniqueIpAddresses = stats.UniqueIpAddresses,
                AverageDurationMs = stats.AvgDurationMs,
                ActionCounts = actionCounts.ToDictionary(x => x.Action, x => x.Count),
                ResourceTypeCounts = resourceCounts.ToDictionary(x => x.ResourceType, x => x.Count)
            };
        }

        /// <summary>
        /// Cleans up old audit logs
        /// </summary>
        public async Task<int> CleanupOldLogsAsync(int daysToKeep)
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-daysToKeep);
            var sql = "DELETE FROM AuditLogs WHERE Timestamp < @CutoffDate;";

            using var connection = new SqlConnection(_databaseConfig.ConnectionString);
            return await connection.ExecuteAsync(sql, new { CutoffDate = cutoffDate });
        }
    }

    /// <summary>
    /// Audit log statistics
    /// </summary>
    public class AuditLogStatistics
    {
        public int TotalEvents { get; set; }
        public int UniqueUsers { get; set; }
        public int UniqueIpAddresses { get; set; }
        public double? AverageDurationMs { get; set; }
        public Dictionary<string, int> ActionCounts { get; set; } = new();
        public Dictionary<string, int> ResourceTypeCounts { get; set; } = new();
    }
}