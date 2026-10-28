using System.Globalization;

namespace Conduit.Web.Services;

/// <summary>
/// ALLOW-LIST-02 (IdentityCenter UC015-D06 / UC017-F03, F06). The agent writes attribute values as the
/// strings it is sent; most AD attributes take them as-is. <c>accountExpires</c> does not: it is a large
/// integer (Windows FILETIME, 100-ns ticks since 1601-01-01 UTC), and the sender holds a date. This is the
/// ONE place that turns what IdentityCenter sends into what AD stores, used by both the update executor
/// (SetAttributes) and the create executor (the attribute bag), so the two cannot disagree.
///
/// Accepted shapes for accountExpires:
///  * null, empty, "0", "never" (any case)      -> "0" (never expires; AD's canonical "no expiry")
///  * a string of digits                        -> passed through (already a FILETIME)
///  * yyyy-MM-dd                                -> the END of that day in the DC's local time, the way the
///                                                 AD Users and Computers "End of" picker stores it
///  * an ISO 8601 date-time (with or without an offset; no offset = UTC) -> that instant
/// Anything else is refused with a reason, before any LDAP call.
/// Every other attribute is returned unchanged.
/// </summary>
public static class AdAttributeValueShaper
{
    public const string AccountExpires = "accountExpires";

    /// <summary>AD's "never expires". 0x7FFFFFFFFFFFFFFF is the other spelling AD accepts; we write the canonical 0.</summary>
    public const string Never = "0";

    public static bool TryShape(string canonicalName, string? value, out string? shaped, out string? error)
    {
        error = null;
        if (!string.Equals(canonicalName, AccountExpires, StringComparison.OrdinalIgnoreCase))
        {
            shaped = value;
            return true;
        }
        return TryShapeAccountExpires(value, out shaped, out error);
    }

    public static bool TryShapeAccountExpires(string? value, out string? shaped, out string? error)
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
            shaped = long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                ? text
                : null;
            if (shaped is null) error = $"'{text}' is not a FILETIME that fits in 64 bits.";
            return shaped is not null;
        }
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            // End of the chosen day, DC-local: midnight at the start of the NEXT day.
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
