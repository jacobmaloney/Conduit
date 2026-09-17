using System.Text.Json;

namespace Conduit.SourceBrowsing;

public static class SourceBrowseScope
{
    public static void ValidateLists(params string?[] lists)
    {
        try
        {
            foreach (var json in lists.Where(s => !string.IsNullOrWhiteSpace(s)))
                if (JsonSerializer.Deserialize<string[]>(json!) is not { } items || items.Any(string.IsNullOrWhiteSpace)) throw new JsonException();
        }
        catch (JsonException) { throw new SourceBrowseException("InvalidScope", "The saved include/exclude scope is invalid. Correct it before browsing."); }
    }
    public static string ClassFilter(string objectClass, string? filter)
    {
        var escaped = objectClass.Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");
        var typeFilter = objectClass.Equals("User", StringComparison.OrdinalIgnoreCase) ? "(&(objectClass=user)(objectCategory=person))" : $"(objectClass={escaped})";
        return string.IsNullOrWhiteSpace(filter) ? typeFilter : $"(&{typeFilter}{filter.Trim()})";
    }
}
