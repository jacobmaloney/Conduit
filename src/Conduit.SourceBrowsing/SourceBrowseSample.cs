using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace Conduit.SourceBrowsing;

/// <summary>One bounded, non-HTML display policy for either execution host. No source values in errors.</summary>
public static class SourceBrowseSample
{
    public static SourceBrowseRow Row(int number, IEnumerable<KeyValuePair<string, object?>> values)
    {
        var fields = values.Take(SourceBrowseLimits.MaximumFields + 1).ToList();
        var display = fields.Take(SourceBrowseLimits.MaximumFields)
            .Select(p => new SourceBrowseField(Clip(p.Key), IsSensitive(p.Key) ? "[hidden]" : Display(p.Value)))
            .ToList();
        if (fields.Count > SourceBrowseLimits.MaximumFields)
            display.Add(new("Additional fields", "Not shown: field limit reached."));
        var label = new[] { "displayName", "name", "cn", "userPrincipalName", "sAMAccountName", "id", "EmployeeId" }
            .Select(key => display.FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && v != "[hidden]") ?? $"Row {number}";
        return new(number, label, display);
    }

    public static bool IsSensitive(string name)
    {
        var key = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        // AD password age metadata is a timestamp, not a password value. Keep the exception exact.
        if (key == "passwordlastset") return false;
        return key.Contains("password") || key.Contains("secret") || key.Contains("token") ||
            key.Contains("credential") || key.Contains("privatekey") || key.Contains("apikey") ||
            key.Contains("unicodepwd") || key.Contains("userpwd") || key.Contains("supplementalcredentials") ||
            key.Contains("pwdhistory") || key.Contains("managedpassword") || key.Contains("laps") ||
            key.Contains("admpwd") || key.Contains("authenticator") || key.Contains("recoverycode");
    }

    public static string Display(object? value) => value switch
    {
        null => "(null)",
        byte[] => "[binary value]",
        string s => s.Length == 0 ? "(empty)" : Clip(s),
        JsonElement e => e.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? "[structured value]" : Clip(e.ToString()),
        IDictionary => "[structured value]",
        IEnumerable items => Clip(string.Join("; ", items.Cast<object?>().Take(20).Select(DisplayScalar))),
        _ => DisplayScalar(value)
    };
    private static string DisplayScalar(object? value) => value switch
    {
        null => "(null)",
        string s => Clip(s),
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
        Guid g => g.ToString(),
        bool b => b ? "true" : "false",
        byte or short or int or long or float or double or decimal or uint or ulong or ushort or sbyte => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        _ => "[structured or binary value]"
    };
    private static string Clip(string text) => text.Length <= SourceBrowseLimits.MaximumCellCharacters
        ? text : text[..SourceBrowseLimits.MaximumCellCharacters] + "… [shortened]";
}
