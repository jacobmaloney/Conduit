using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Conduit.Sync.Security;

namespace Conduit.Web.Services;

/// <summary>
/// The one-time secret that authorizes zero-admin recovery: a random token written to
/// <c>%PROGRAMDATA%\Conduit\recovery.token</c> through <see cref="RestrictedFileWriter"/>,
/// with the same ACL as secrets.json (owner + LocalSystem + BuiltinAdministrators,
/// inheritance protected).
///
/// WHY A FILE. When the configured database has a schema but zero active portal admins,
/// nobody can sign in, so the recovery path cannot be authenticated against the database
/// it is recovering. Something else has to prove the caller is entitled to mint the first
/// admin. Network reachability does not: an install is reachable from wherever it is
/// deployed, and "reachable" was the whole of the old gate — which meant any anonymous
/// visitor could reach admin creation on an install that had already been set up.
///
/// A loopback bind is not the answer either. Reverse proxies and container port maps
/// routinely present remote traffic as loopback, so "came from 127.0.0.1" proves less than
/// it appears to. Reading a file under %PROGRAMDATA%\Conduit does prove host access — and
/// it is precisely the trust boundary that already guards the connection string. Someone
/// who can read this token can read secrets.json; they are already inside.
///
/// The token is issued once, held until used, and destroyed on use. It is never rendered,
/// logged, or returned to a caller — the only way to learn it is to read the file.
///
/// IT ALSO EXPIRES. Consumption on success is not enough on its own: an operator who
/// recovers some other way — a restore, a PrepareDatabaseAsync into a fresh database, a
/// repoint — never consumes it, and the file would then sit on disk silently authorizing
/// the NEXT zero-admin event, possibly years later. Every backup of %PROGRAMDATA%\Conduit
/// taken in the meantime carries a live standing credential outside that directory's ACL.
/// <see cref="Lifetime"/> bounds that window. Expiry never strands an operator: the status
/// probe re-issues whenever it observes the zero-admin state, and an expired file is
/// replaced rather than left in place.
/// </summary>
public static class AdminRecoveryToken
{
    /// <summary>Sits alongside secrets.json, under the same ACL-restricted directory.</summary>
    public static string DefaultPath => ConduitDataPaths.FilePath("recovery.token");

    /// <summary>
    /// How long an issued token stays valid. Long enough for a real operator round trip —
    /// read the log line, get to the host, open an ACL-restricted file, paste the value —
    /// and short enough that a forgotten token is not a standing credential. Recovery does
    /// not depend on this window: whenever the probe still sees zero admins it re-issues.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>The expiry decision, isolated from the clock and the filesystem.</summary>
    public static bool IsExpired(DateTime issuedAtUtc, DateTime nowUtc) =>
        nowUtc - issuedAtUtc >= Lifetime;

    /// <summary>
    /// Issues a token if none is USABLE; a no-op when a live one already exists. An expired,
    /// undated or malformed file is replaced — leaving it would let the TTL brick recovery
    /// permanently, since the file's mere existence used to suppress re-issue.
    ///
    /// Idempotent on purpose. The detection point is the database status probe, which runs
    /// on a five-second cache — rotating on every probe would invalidate the token between
    /// the operator opening the file and pasting the value.
    /// </summary>
    public static void EnsureIssued(string? path = null)
    {
        var target = path ?? DefaultPath;
        if (IsOutstanding(target))
        {
            return;
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var document = new JsonObject
        {
            ["Token"] = token,
            ["IssuedAtUtc"] = DateTime.UtcNow.ToString("O")
        };

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        RestrictedFileWriter.Write(target,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// True when the presented value matches a LIVE outstanding token. False when no token
    /// is outstanding, the file is unreadable, malformed or undated, the token has expired,
    /// or the value differs. One answer for all of them.
    ///
    /// Both sides are hashed before comparison so the fixed-time compare does not leak the
    /// token's length through an early length mismatch.
    /// </summary>
    public static bool Validate(string? presented, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(presented))
        {
            return false;
        }

        var issued = TryReadToken(path);
        if (issued is null || IsExpired(issued.Value.IssuedAtUtc, DateTime.UtcNow))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(issued.Value.Token)),
            SHA256.HashData(Encoding.UTF8.GetBytes(presented.Trim())));
    }

    /// <summary>
    /// Destroys the token file so it authorizes exactly one recovery. Returns true when a
    /// file was actually removed.
    ///
    /// Called after the recovery it authorized has succeeded, and also once an active admin
    /// is observed by any other route — see SetupService.RetireRecoveryToken. A FAILED
    /// attempt leaves it in place so the operator can retry without going back to the host
    /// for a new one.
    /// </summary>
    public static bool Consume(string? path = null)
    {
        var target = path ?? DefaultPath;
        try
        {
            if (!File.Exists(target))
            {
                return false;
            }

            File.Delete(target);
            return true;
        }
        catch
        {
            // A token that survives its use is not a hole on its own — every caller
            // re-checks that the database still has zero active admins, and the moment
            // recovery succeeds it does not. Failing the recovery over a stuck delete
            // would strand the operator for no gain.
            return false;
        }
    }

    /// <summary>
    /// True when a USABLE token exists: present, well-formed, and not expired. Never reveals
    /// its value.
    ///
    /// Expiry is part of this answer on purpose. It drives both the re-issue decision in
    /// <see cref="EnsureIssued"/> and the operator hint on the recovery page, and a dead
    /// token must not satisfy either — otherwise the page tells the operator to go read a
    /// file that will refuse whatever they paste out of it.
    /// </summary>
    public static bool IsOutstanding(string? path = null)
    {
        var issued = TryReadToken(path);
        return issued is not null && !IsExpired(issued.Value.IssuedAtUtc, DateTime.UtcNow);
    }

    /// <summary>
    /// Reads the token and its issue time. Null when absent, unparseable, missing either
    /// field, or carrying an IssuedAtUtc that is not a ZONED round-trip timestamp — an
    /// undated token cannot be aged, and something that cannot be aged must not be trusted
    /// forever.
    ///
    /// An unzoned timestamp is rejected rather than assumed local. <see cref="EnsureIssued"/>
    /// always writes "O" off DateTime.UtcNow, so a value without an offset did not come from
    /// this code; converting it as local time would age the token by the host's UTC offset,
    /// and WEST of UTC that makes it outlive its TTL. Fail closed on the ambiguity.
    /// </summary>
    private static (string Token, DateTime IssuedAtUtc)? TryReadToken(string? path)
    {
        var target = path ?? DefaultPath;
        try
        {
            if (!File.Exists(target))
            {
                return null;
            }

            if (JsonNode.Parse(File.ReadAllText(target)) is not JsonObject root)
            {
                return null;
            }

            var value = root["Token"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var issuedAt = root["IssuedAtUtc"]?.GetValue<string>();
            if (!DateTime.TryParse(issuedAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var issuedAtUtc) ||
                issuedAtUtc.Kind == DateTimeKind.Unspecified)
            {
                return null;
            }

            return (value, issuedAtUtc.ToUniversalTime());
        }
        catch
        {
            return null;
        }
    }
}
