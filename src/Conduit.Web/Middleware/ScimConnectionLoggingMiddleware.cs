using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Conduit.Web.Services;

namespace Conduit.Web.Middleware
{
    /// <summary>
    /// Middleware to log SCIM API connections for diagnostic purposes
    /// </summary>
    public class ScimConnectionLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ScimConnectionLoggingMiddleware> _logger;

        public ScimConnectionLoggingMiddleware(RequestDelegate next, ILogger<ScimConnectionLoggingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, AuditLogService auditLogService)
        {
            // Only log SCIM API requests
            if (!context.Request.Path.StartsWithSegments("/scim", StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            var requestId = Guid.NewGuid().ToString("N").Substring(0, 8);
            
            // Capture request details
            var method = context.Request.Method;
            var path = context.Request.Path + context.Request.QueryString;
            var ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var userAgent = context.Request.Headers["User-Agent"].ToString();
            var authHeader = context.Request.Headers["Authorization"].ToString();
            var hasAuth = !string.IsNullOrEmpty(authHeader);
            var authType = hasAuth ? authHeader.Split(' ')[0] : "None";
            
            // Log incoming connection
            _logger.LogInformation($"[{requestId}] SCIM Connection: {method} {path} from {ipAddress}");
            
            // Capture request body for POST/PUT/PATCH
            string? requestBody = null;
            if (context.Request.Method != "GET" && context.Request.Method != "DELETE")
            {
                context.Request.EnableBuffering();
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                requestBody = await reader.ReadToEndAsync();
                context.Request.Body.Position = 0;
                
                // Log request body (truncated AND secret-redacted). A SCIM password-set
                // PATCH (or a token/secret field) must never land in the logs in clear.
                if (!string.IsNullOrEmpty(requestBody))
                {
                    var safeBody = RedactSecrets(requestBody);
                    var truncatedBody = safeBody.Length > 500
                        ? safeBody.Substring(0, 500) + "..."
                        : safeBody;
                    _logger.LogDebug($"[{requestId}] Request Body: {truncatedBody}");
                }
            }

            // Capture original response body stream
            var originalBodyStream = context.Response.Body;
            string? responseBody = null;
            
            try
            {
                using var responseBodyStream = new MemoryStream();
                context.Response.Body = responseBodyStream;
                
                // Process the request
                await _next(context);
                
                // Capture response
                context.Response.Body.Seek(0, SeekOrigin.Begin);
                responseBody = await new StreamReader(context.Response.Body).ReadToEndAsync();
                context.Response.Body.Seek(0, SeekOrigin.Begin);
                
                // Copy response to original stream
                await responseBodyStream.CopyToAsync(originalBodyStream);
            }
            finally
            {
                context.Response.Body = originalBodyStream;
            }
            
            stopwatch.Stop();
            
            // Log connection result
            var statusCode = context.Response.StatusCode;
            var statusText = statusCode >= 200 && statusCode < 300 ? "SUCCESS" : 
                           statusCode >= 400 && statusCode < 500 ? "CLIENT_ERROR" : 
                           statusCode >= 500 ? "SERVER_ERROR" : "UNKNOWN";
            
            _logger.LogInformation($"[{requestId}] SCIM Response: {statusCode} {statusText} in {stopwatch.ElapsedMilliseconds}ms");
            
            // Log to audit database
            var action = $"SCIM_{method}";
            var resourceType = "Connection";
            var resourceId = path;
            
            // Extract user info from auth token if available
            string? userId = null;
            string? userName = null;
            if (context.User?.Identity?.IsAuthenticated == true)
            {
                userId = context.User.FindFirst("sub")?.Value ?? context.User.FindFirst("id")?.Value;
                userName = context.User.Identity.Name;
            }
            
            // The request body is also persisted to the audit DB below; redact secrets
            // there too so password/token/secret values never rest in the audit table.
            var auditRequestBody = requestBody != null ? RedactSecrets(requestBody) : null;

            // Build details for audit log
            var details = $"Auth: {authType}, Path: {path}";
            if (statusCode >= 400)
            {
                details += $", Error: {responseBody?.Substring(0, Math.Min(responseBody.Length, 200))}";
            }
            
            await auditLogService.LogAsync(
                action: action,
                resourceType: resourceType,
                resourceId: resourceId,
                userId: userId,
                userName: userName,
                details: details,
                oldValue: auditRequestBody?.Length > 1000 ? auditRequestBody.Substring(0, 1000) + "..." : auditRequestBody,
                newValue: responseBody?.Length > 1000 ? responseBody.Substring(0, 1000) + "..." : responseBody,
                statusCode: statusCode,
                ipAddress: ipAddress,
                userAgent: userAgent,
                duration: stopwatch.Elapsed
            );
            
            // Log detailed diagnostics for errors
            if (statusCode >= 400)
            {
                _logger.LogWarning($"[{requestId}] SCIM Error Details:\n" +
                    $"  Method: {method}\n" +
                    $"  Path: {path}\n" +
                    $"  Status: {statusCode}\n" +
                    $"  IP: {ipAddress}\n" +
                    $"  Auth: {authType}\n" +
                    $"  User: {userName ?? "anonymous"}\n" +
                    $"  Duration: {stopwatch.ElapsedMilliseconds}ms\n" +
                    $"  Response: {responseBody?.Substring(0, Math.Min(responseBody.Length, 500))}");
            }
        }

        // Keys whose values are masked anywhere they appear in a logged/audited body.
        private const string SecretKeyPattern = "password|secret|token|clientsecret|privatekey|credential|pwd|apikey";

        // JSON value form:  "password": "hunter2"  ->  "password": "***REDACTED***"
        private static readonly Regex JsonValueRegex = new(
            "(\"(?:" + SecretKeyPattern + ")\"\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // SCIM PATCH form: {"path":"password", ... "value":"hunter2"} — when the op
        // targets a secret path, mask the sibling "value".
        private static readonly Regex ScimPatchValueRegex = new(
            "(\"path\"\\s*:\\s*\"(?:" + SecretKeyPattern + ")\"[^}]*?\"value\"\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Masks likely-secret values (password/secret/token/...) in a JSON body before
        /// it is written to logs or the audit table. JSON-aware via regex so the rest of
        /// the body stays useful for diagnostics. Non-JSON bodies pass through unchanged.
        /// </summary>
        private static string RedactSecrets(string body)
        {
            if (string.IsNullOrEmpty(body)) return body;

            const string mask = "\"***REDACTED***\"";
            var result = JsonValueRegex.Replace(body, "$1" + mask);
            result = ScimPatchValueRegex.Replace(result, "$1" + mask);
            return result;
        }
    }
}