using System;

namespace Conduit.Web.Models
{
    /// <summary>
    /// Represents an audit log entry
    /// </summary>
    public class AuditLog
    {
        public Guid Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string Action { get; set; } = "";
        public string ResourceType { get; set; } = "";
        public string? ResourceId { get; set; }
        public string? UserId { get; set; }
        public string? UserName { get; set; }
        public string? IpAddress { get; set; }
        public string? UserAgent { get; set; }
        public int? StatusCode { get; set; }
        public string? Details { get; set; }
        public string? OldValue { get; set; }
        public string? NewValue { get; set; }
        public TimeSpan? Duration { get; set; }
    }

    /// <summary>
    /// Audit log action types
    /// </summary>
    public static class AuditActions
    {
        // User actions
        public const string UserCreated = "USER_CREATED";
        public const string UserUpdated = "USER_UPDATED";
        public const string UserDeleted = "USER_DELETED";
        public const string UserActivated = "USER_ACTIVATED";
        public const string UserDeactivated = "USER_DEACTIVATED";
        public const string UserPasswordChanged = "USER_PASSWORD_CHANGED";
        
        // Group actions
        public const string GroupCreated = "GROUP_CREATED";
        public const string GroupUpdated = "GROUP_UPDATED";
        public const string GroupDeleted = "GROUP_DELETED";
        public const string GroupMemberAdded = "GROUP_MEMBER_ADDED";
        public const string GroupMemberRemoved = "GROUP_MEMBER_REMOVED";
        
        // Authentication actions
        public const string LoginSuccess = "LOGIN_SUCCESS";
        public const string LoginFailed = "LOGIN_FAILED";
        public const string TokenCreated = "TOKEN_CREATED";
        public const string TokenRevoked = "TOKEN_REVOKED";
        public const string TokenExpired = "TOKEN_EXPIRED";
        
        // System actions
        public const string SystemStartup = "SYSTEM_STARTUP";
        public const string SystemShutdown = "SYSTEM_SHUTDOWN";
        public const string ConfigurationChanged = "CONFIGURATION_CHANGED";
        public const string DatabaseInitialized = "DATABASE_INITIALIZED";
        
        // API actions
        public const string ApiCallSuccess = "API_CALL_SUCCESS";
        public const string ApiCallFailed = "API_CALL_FAILED";
        public const string RateLimitExceeded = "RATE_LIMIT_EXCEEDED";
    }
}