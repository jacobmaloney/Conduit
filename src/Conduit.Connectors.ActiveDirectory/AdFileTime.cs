using System.Globalization;

namespace Conduit.Connectors.ActiveDirectory;

/// <summary>
/// The ONE owner of AD FILETIME text in Conduit, both directions (REUSE-PASS-01, 2026-10-28).
/// A FILETIME is 100-ns ticks since 1601-01-01 UTC, carried by LDAP as an 18-digit decimal string;
/// 0 and 0x7FFFFFFFFFFFFFFF are AD's "never" sentinels (never logged on / never expires).
///
/// Read direction (<see cref="ToIsoUtc"/>): what <c>ActiveDirectorySource</c> does to lastLogon,
/// lastLogonTimestamp and pwdLastSet on the way into a ConnectorObject.
/// Write direction (<see cref="TryToFileTimeString"/>): what the agent executors do to the
/// accountExpires value IdentityCenter sends (a date, an ISO date-time, a FILETIME, "0" or "never").
/// </summary>
public static class AdFileTime
{
    /// <summary>AD's canonical "never expires"; 0x7FFFFFFFFFFFFFFF is the other spelling AD accepts.</summary>
    public const string Never = "0";

    public static bool IsSentinel(long ticks) => ticks <= 0 || ticks == long.MaxValue;

    /// <summary>
    /// A FILETIME string to an ISO-8601 UTC string ("o"); null for absent / unparseable / sentinel
    /// values; an already-ISO date-time is normalised to UTC so this is safe to call on either shape.
    /// </summary>
    public static string? ToIsoUtc(object? raw)
    {
        if (raw is not string s || string.IsNullOrWhiteSpace(s)) return null;
        if (long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
        {
            if (IsSentinel(ticks)) return null;
            try { return DateTime.FromFileTimeUtc(ticks).ToString("o", CultureInfo.InvariantCulture); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            return dt.ToString("o", CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>
    /// What IdentityCenter sends for accountExpires, as the FILETIME string AD stores:
    ///  * null, empty, "0", "never" (any case)      -> "0"
    ///  * a string of digits                        -> passed through (already a FILETIME)
    ///  * yyyy-MM-dd                                -> the END of that day in the DC's local time, the way
    ///                                                 the AD Users and Computers "End of" picker stores it
    ///  * an ISO 8601 date-time (no offset = UTC)   -> that instant
    /// Anything else is refused with a reason; the caller refuses the whole write before any LDAP call.
    /// </summary>
    public static bool TryToFileTimeString(string? value, out string? shaped, out string? error)
    {
        error = null;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text == "0" || string.Equals(text, "never", StringComparison.OrdinalIgnoreCase))
        {
            shaped = Never;
            return true;
        }
        if (text.All(char.IsAsciiDigit))
        {
            if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                shaped = text;
                return true;
            }
            shaped = null;
            error = $"'{text}' is not a FILETIME that fits in 64 bits.";
            return false;
        }
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            var endOfDay = DateTime.SpecifyKind(day.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Local);
            shaped = endOfDay.ToFileTime().ToString(CultureInfo.InvariantCulture);
            return true;
        }
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
        {
            if (instant.Year < 1601)
            {
                shaped = null;
                error = $"'{text}' is before 1601, which a FILETIME cannot hold.";
                return false;
            }
            shaped = instant.UtcDateTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
            return true;
        }
        shaped = null;
        error = $"'{text}' is not a date (yyyy-MM-dd), an ISO 8601 date-time, a FILETIME, '0' or 'never'.";
        return false;
    }
}
