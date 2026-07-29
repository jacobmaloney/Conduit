using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Conduit.DataAccess.Repositories
{
    /// <summary>One remembered SQL SPN host from the last sweep of an AD connection.</summary>
    public sealed class SqlSpnSnapshotRow
    {
        public Guid Id { get; set; }
        public Guid AdConnectionId { get; set; }
        public string HostKey { get; set; } = "";
        public string? Spn { get; set; }
        public DateTime FirstSeenAt { get; set; }
        public DateTime LastSeenAt { get; set; }
    }

    /// <summary>
    /// Persists the last-seen set of SQL SPN hosts per AD connection so the SPN
    /// watcher can diff two sweeps and tell a genuinely NEW instance from one it
    /// already knew about across restarts. State only — no discovery data lives here.
    /// </summary>
    public class SqlSpnSnapshotRepository : BaseRepository
    {
        public SqlSpnSnapshotRepository(DatabaseConfig config) : base(config) { }

        /// <summary>Every host key currently remembered for one AD connection.</summary>
        public async Task<HashSet<string>> GetHostKeysAsync(Guid adConnectionId)
        {
            var rows = await QueryAsync<string>(
                "SELECT HostKey FROM SqlSpnSnapshot WHERE AdConnectionId = @AdConnectionId",
                new { AdConnectionId = adConnectionId });
            return new HashSet<string>(rows, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Records a host as seen right now: inserts it (stamping FirstSeenAt) or
        /// bumps LastSeenAt if already known. The insert is guarded so concurrent
        /// sweeps cannot double-insert the same (AdConnectionId, HostKey).
        /// </summary>
        public Task UpsertSeenAsync(Guid adConnectionId, string hostKey, string? spn, DateTime nowUtc) =>
            ExecuteAsync(@"
                UPDATE SqlSpnSnapshot
                   SET LastSeenAt = @Now, Spn = @Spn
                 WHERE AdConnectionId = @AdConnectionId AND HostKey = @HostKey;
                IF @@ROWCOUNT = 0
                    INSERT INTO SqlSpnSnapshot (Id, AdConnectionId, HostKey, Spn, FirstSeenAt, LastSeenAt)
                    SELECT @Id, @AdConnectionId, @HostKey, @Spn, @Now, @Now
                    WHERE NOT EXISTS (SELECT 1 FROM SqlSpnSnapshot
                                       WHERE AdConnectionId = @AdConnectionId AND HostKey = @HostKey);",
                new
                {
                    Id = Guid.NewGuid(),
                    AdConnectionId = adConnectionId,
                    HostKey = hostKey,
                    Spn = spn,
                    Now = nowUtc
                });

        /// <summary>Forget hosts that a sweep no longer returned (instance retired).</summary>
        public Task RemoveAsync(Guid adConnectionId, IEnumerable<string> hostKeys)
        {
            var keys = hostKeys.ToArray();
            if (keys.Length == 0) return Task.CompletedTask;
            return ExecuteAsync(
                "DELETE FROM SqlSpnSnapshot WHERE AdConnectionId = @AdConnectionId AND HostKey IN @Keys",
                new { AdConnectionId = adConnectionId, Keys = keys });
        }
    }
}
