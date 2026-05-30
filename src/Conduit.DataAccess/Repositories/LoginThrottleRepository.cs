using System;
using System.Threading.Tasks;

namespace Conduit.DataAccess.Repositories;

/// <summary>
/// Dapper data access for the login brute-force tables (LoginAttempts +
/// LoginLockouts, migration v11). LoginThrottle keeps all policy logic —
/// thresholds, sliding window, fail-open behavior — this repository only
/// relocates the SQL. Methods do NOT swallow exceptions; the throttle's
/// fail-open catch blocks remain the single place errors are handled.
/// </summary>
public class LoginThrottleRepository : BaseRepository
{
    public LoginThrottleRepository(DatabaseConfig config) : base(config) { }

    public Task<DateTime?> GetActiveLockoutUntilAsync(string usernameLower, string ipAddress, DateTime now) =>
        QuerySingleOrDefaultAsync<DateTime?>(@"
            SELECT LockedUntil FROM LoginLockouts
            WHERE UsernameLower = @U AND IpAddress = @I AND LockedUntil > @Now",
            new { U = usernameLower, I = ipAddress, Now = now });

    public Task<int> GetLockoutFailureCountAsync(string usernameLower, string ipAddress) =>
        ExecuteScalarAsync<int>(@"
            SELECT FailureCount FROM LoginLockouts
            WHERE UsernameLower = @U AND IpAddress = @I",
            new { U = usernameLower, I = ipAddress });

    public Task<int> CountFailuresInWindowAsync(string usernameLower, string ipAddress, DateTime windowStart) =>
        ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM LoginAttempts
            WHERE UsernameLower = @U AND IpAddress = @I
              AND Success = 0 AND AttemptedAt > @WindowStart",
            new { U = usernameLower, I = ipAddress, WindowStart = windowStart });

    public Task RecordSuccessAsync(string usernameLower, string ipAddress) =>
        ExecuteAsync(@"
            INSERT INTO LoginAttempts (Id, UsernameLower, IpAddress, AttemptedAt, Success)
            VALUES (NEWID(), @U, @I, SYSUTCDATETIME(), 1);

            DELETE FROM LoginLockouts WHERE UsernameLower = @U AND IpAddress = @I;",
            new { U = usernameLower, I = ipAddress });

    public Task RecordFailureAsync(string usernameLower, string ipAddress, DateTime now) =>
        ExecuteAsync(@"
            INSERT INTO LoginAttempts (Id, UsernameLower, IpAddress, AttemptedAt, Success)
            VALUES (NEWID(), @U, @I, @Now, 0);",
            new { U = usernameLower, I = ipAddress, Now = now });

    public Task UpsertLockoutAsync(string usernameLower, string ipAddress, DateTime lockedUntil, int failures) =>
        ExecuteAsync(@"
            MERGE LoginLockouts AS target
            USING (SELECT @U AS UsernameLower, @I AS IpAddress) AS src
               ON target.UsernameLower = src.UsernameLower
              AND target.IpAddress = src.IpAddress
            WHEN MATCHED THEN
                UPDATE SET LockedUntil = @LockedUntil, FailureCount = @Failures
            WHEN NOT MATCHED THEN
                INSERT (UsernameLower, IpAddress, LockedUntil, FailureCount)
                VALUES (src.UsernameLower, src.IpAddress, @LockedUntil, @Failures);",
            new { U = usernameLower, I = ipAddress, LockedUntil = lockedUntil, Failures = failures });

    /// <summary>Pruner support: deletes old attempts and expired lockouts in one round-trip.</summary>
    public Task<int> PruneAsync(DateTime attemptCutoff) =>
        ExecuteAsync(@"
            DELETE FROM LoginAttempts WHERE AttemptedAt < @Cutoff;
            DELETE FROM LoginLockouts WHERE LockedUntil < SYSUTCDATETIME();",
            new { Cutoff = attemptCutoff });
}
