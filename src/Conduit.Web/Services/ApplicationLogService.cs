using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Conduit.Web.Services
{
    /// <summary>
    /// Service for application logging and debugging
    /// </summary>
    public class ApplicationLogService
    {
        private readonly ConcurrentQueue<LogEntry> _logs = new();
        private readonly string _logDirectory;
        private readonly int _maxInMemoryLogs = 1000;

        public ApplicationLogService()
        {
            _logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            Directory.CreateDirectory(_logDirectory);
        }

        /// <summary>
        /// Log levels
        /// </summary>
        public enum LogLevel
        {
            Debug,
            Info,
            Warning,
            Error,
            Critical
        }

        /// <summary>
        /// Log entry
        /// </summary>
        public class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public LogLevel Level { get; set; }
            public string Category { get; set; } = "";
            public string Message { get; set; } = "";
            public string? Details { get; set; }
            public Exception? Exception { get; set; }
        }

        /// <summary>
        /// Log a message
        /// </summary>
        public async Task LogAsync(LogLevel level, string category, string message, string? details = null, Exception? exception = null)
        {
            var entry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = level,
                Category = category,
                Message = message,
                Details = details,
                Exception = exception
            };

            // Add to in-memory queue
            _logs.Enqueue(entry);
            
            // Trim queue if too large
            while (_logs.Count > _maxInMemoryLogs)
            {
                _logs.TryDequeue(out _);
            }

            // Also write to file
            await WriteToFileAsync(entry);
            
            // Also write to console for debugging
            var color = level switch
            {
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Info => ConsoleColor.Green,
                LogLevel.Debug => ConsoleColor.Gray,
                LogLevel.Critical => ConsoleColor.DarkRed,
                _ => ConsoleColor.White
            };
            
            var oldColor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"[{entry.Timestamp:HH:mm:ss}] [{level}] [{category}] {message}");
            if (!string.IsNullOrEmpty(details))
            {
                Console.WriteLine($"  Details: {details}");
            }
            if (exception != null)
            {
                Console.WriteLine($"  Exception: {exception.Message}");
                Console.WriteLine($"  Stack: {exception.StackTrace}");
            }
            Console.ForegroundColor = oldColor;
        }

        /// <summary>
        /// Log debug message
        /// </summary>
        public Task LogDebugAsync(string category, string message, string? details = null)
            => LogAsync(LogLevel.Debug, category, message, details);

        /// <summary>
        /// Log info message
        /// </summary>
        public Task LogInfoAsync(string category, string message, string? details = null)
            => LogAsync(LogLevel.Info, category, message, details);

        /// <summary>
        /// Log warning message
        /// </summary>
        public Task LogWarningAsync(string category, string message, string? details = null)
            => LogAsync(LogLevel.Warning, category, message, details);

        /// <summary>
        /// Log error message
        /// </summary>
        public Task LogErrorAsync(string category, string message, Exception? exception = null)
            => LogAsync(LogLevel.Error, category, message, null, exception);

        /// <summary>
        /// Get recent logs
        /// </summary>
        public List<LogEntry> GetRecentLogs(int count = 100, LogLevel? minLevel = null, string? category = null)
        {
            var logs = _logs.ToList();
            
            if (minLevel.HasValue)
            {
                logs = logs.Where(l => l.Level >= minLevel.Value).ToList();
            }
            
            if (!string.IsNullOrEmpty(category))
            {
                logs = logs.Where(l => l.Category.Contains(category, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            
            return logs.OrderByDescending(l => l.Timestamp).Take(count).ToList();
        }

        /// <summary>
        /// Clear in-memory logs
        /// </summary>
        public void ClearLogs()
        {
            while (_logs.TryDequeue(out _)) { }
        }

        /// <summary>
        /// Write log entry to file
        /// </summary>
        private async Task WriteToFileAsync(LogEntry entry)
        {
            try
            {
                var fileName = $"conduit_{DateTime.UtcNow:yyyy-MM-dd}.log";
                var filePath = Path.Combine(_logDirectory, fileName);
                
                var logLine = $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Level}] [{entry.Category}] {entry.Message}";
                if (!string.IsNullOrEmpty(entry.Details))
                {
                    logLine += $" | Details: {entry.Details}";
                }
                if (entry.Exception != null)
                {
                    logLine += $" | Exception: {entry.Exception}";
                }
                
                await File.AppendAllTextAsync(filePath, logLine + Environment.NewLine);
            }
            catch (Exception ex)
            {
                // Swallow file write errors to avoid breaking the app
                Console.WriteLine($"Failed to write log to file: {ex.Message}");
            }
        }
    }
}