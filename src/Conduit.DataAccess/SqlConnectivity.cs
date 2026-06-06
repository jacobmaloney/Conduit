using Microsoft.Data.SqlClient;

namespace Conduit.DataAccess
{
    /// <summary>
    /// Single source of truth for classifying a <see cref="SqlException"/> as a transient
    /// connectivity failure (the SQL host is down / unreachable) versus a fatal failure
    /// (bad credentials, missing permission, a genuine schema/migration error).
    ///
    /// "Transient" means: waiting and retrying has a real chance of succeeding because the
    /// problem is the network or the server being momentarily absent. "Fatal" means: the
    /// connection got far enough to be rejected for a reason that will NOT fix itself by
    /// waiting (e.g. 18456 login failed) — those must surface fast, not be masked behind an
    /// infinite retry loop.
    ///
    /// Numbers covered: 53 (network path/server not found), 40 (could not open connection),
    /// -2 (client-side command/connect timeout), 10060 (TCP connect timeout), 10061
    /// (connection actively refused), 11001 (host not found / DNS), 233 (no process on the
    /// pipe / shared-memory), 4060 (cannot open the requested database). 18456 is explicitly
    /// EXCLUDED — that's an auth failure (bad password), not network-down.
    ///
    /// Both <c>SetupService</c> (Conduit.Web) and <c>DatabaseInitializer</c> (this assembly)
    /// classify through here so the error-number list lives in exactly one place.
    /// </summary>
    public static class SqlConnectivity
    {
        /// <summary>
        /// Returns true if the exception is a connectivity-class failure that is worth
        /// retrying (the host is unreachable), false if it is a fatal failure that should
        /// surface immediately.
        /// </summary>
        public static bool IsTransient(SqlException ex)
        {
            if (ex == null)
            {
                return false;
            }

            foreach (SqlError err in ex.Errors)
            {
                switch (err.Number)
                {
                    case 53:     // network path not found / server not found
                    case 40:     // could not open a connection to SQL Server
                    case -2:     // timeout (client)
                    case 10060:  // TCP connect timeout
                    case 10061:  // connection actively refused
                    case 11001:  // host not found (DNS)
                    case 233:    // no process is on the other end of the pipe
                    case 4060:   // cannot open database requested by the login
                        return true;
                }
            }

            return false;
        }
    }
}
