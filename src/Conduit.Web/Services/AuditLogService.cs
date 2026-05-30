using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Conduit.DataAccess.Repositories;
using Conduit.Web.Models;

namespace Conduit.Web.Services
{
    /// <summary>
    /// Service for managing audit logs
    /// </summary>
    public class AuditLogService
    {
        private readonly AuditLogRepository _repository;

        public AuditLogService(AuditLogRepository repository)
        {
            _repository = repository;
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
        private Task InsertLogAsync(AuditLog log) => _repository.InsertAsync(log);

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
            var startRow = (page - 1) * pageSize + 1;
            var endRow = page * pageSize;

            return await _repository.GetPagedAsync(
                startDate, endDate, action, resourceType, userId, startRow, endRow);
        }

        /// <summary>
        /// Gets audit log statistics
        /// </summary>
        public async Task<AuditLogStatistics> GetStatisticsAsync(DateTime? startDate = null, DateTime? endDate = null)
        {
            var (stats, actionCounts, resourceCounts) =
                await _repository.GetStatisticsAsync(startDate, endDate);

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
        public Task<int> CleanupOldLogsAsync(int daysToKeep)
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-daysToKeep);
            return _repository.DeleteOlderThanAsync(cutoffDate);
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