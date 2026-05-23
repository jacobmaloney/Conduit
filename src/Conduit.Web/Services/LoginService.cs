using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;

namespace Conduit.Web.Services
{
    /// <summary>
    /// Validates portal admin credentials. As of migration v10 these live in their own
    /// PortalAdmins table, decoupled from SCIM Users — so directory data ops can never
    /// invalidate the portal login.
    /// </summary>
    public class LoginService
    {
        private readonly PortalAdminRepository _admins;
        private readonly LoginThrottle _throttle;
        private readonly AuditLogService _audit;
        private readonly ILogger<LoginService> _logger;

        public LoginService(PortalAdminRepository admins, LoginThrottle throttle,
            AuditLogService audit, ILogger<LoginService> logger)
        {
            _admins = admins;
            _throttle = throttle;
            _audit = audit;
            _logger = logger;
        }

        /// <summary>
        /// Validates a portal admin credential. <paramref name="ipAddress"/> is used to
        /// scope brute-force tracking; pass the caller's connection remote IP. The
        /// returned <see cref="LoginOutcome"/> distinguishes the lockout case from a
        /// real bad-password case so the page can render the right message.
        /// </summary>
        public async Task<LoginOutcome> ValidateAsync(string username, string password, string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                _logger.LogWarning("Login rejected: empty username or password");
                return LoginOutcome.BadCredentials();
            }

            var lookup = username.Trim();
            var key = lookup.ToLowerInvariant();

            // Pre-check throttle so a locked account doesn't even reach the hasher.
            var gate = await _throttle.CheckAllowedAsync(key, ipAddress);
            if (!gate.Allowed)
            {
                _logger.LogWarning("Login locked: '{UserName}' from {Ip} — too many failures ({Count}), retry in {Sec}s",
                    lookup, ipAddress, gate.FailuresInWindow, (int)gate.RetryAfter.TotalSeconds);
                await SafeAudit("Login.Lockout", lookup, ipAddress, 429,
                    details: $"Lockout in effect; {gate.FailuresInWindow} failures within window.");
                return LoginOutcome.Locked(gate.RetryAfter);
            }

            try
            {
                var admin = await _admins.GetByUserNameAsync(lookup);
                if (admin == null || !admin.Active
                    || string.IsNullOrEmpty(admin.PasswordHash) || string.IsNullOrEmpty(admin.PasswordSalt)
                    || !PasswordHasher.Verify(password, admin.PasswordHash, admin.PasswordSalt))
                {
                    var failures = await _throttle.RegisterFailureAsync(key, ipAddress);
                    _logger.LogWarning("Login rejected for '{UserName}' from {Ip} (failure {N})", lookup, ipAddress, failures);
                    await SafeAudit("Login.Failed", lookup, ipAddress, 401,
                        details: $"Failure #{failures} within window.");
                    return LoginOutcome.BadCredentials();
                }

                await _throttle.RegisterSuccessAsync(key, ipAddress);
                await _admins.MarkLoggedInAsync(admin.Id);
                _logger.LogInformation("Portal admin login OK for '{UserName}' from {Ip}", admin.UserName, ipAddress);
                await SafeAudit("Login.Succeeded", admin.UserName, ipAddress, 200, userId: admin.Id.ToString());
                return LoginOutcome.Ok(new AdminLoginResult(admin.Id, admin.UserName, admin.DisplayName ?? admin.UserName));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Login validation threw exception for {Username}", lookup);
                return LoginOutcome.BadCredentials();
            }
        }

        // Audit writes must never propagate a failure into the auth path — if the audit
        // store is down, we still log the auth decision but don't reject the user.
        private async Task SafeAudit(string action, string userName, string ip, int statusCode,
            string? userId = null, string? details = null)
        {
            try
            {
                await _audit.LogAsync(action, resourceType: "PortalAdmin", resourceId: userId,
                    userId: userId, userName: userName, ipAddress: ip,
                    statusCode: statusCode, details: details);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audit log write failed for {Action}", action);
            }
        }
    }

    public record AdminLoginResult(Guid Id, string UserName, string DisplayName);

    public class LoginOutcome
    {
        public bool Success => Result is not null;
        public AdminLoginResult? Result { get; private init; }
        public bool LockedOut { get; private init; }
        public TimeSpan RetryAfter { get; private init; }

        public static LoginOutcome Ok(AdminLoginResult r) => new() { Result = r };
        public static LoginOutcome BadCredentials() => new();
        public static LoginOutcome Locked(TimeSpan retryAfter) => new() { LockedOut = true, RetryAfter = retryAfter };
    }
}
