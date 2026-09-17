using System.Globalization;

namespace Conduit.Mapping;

public enum TextOperation
{
    Upper,
    Lower,
    Trim
}

/// <summary>
/// Shared text operations. Hosts select culture explicitly so moving a saved
/// mapping between implementations does not silently change its casing policy.
/// No host configuration, persistence, network access or mutable state.
/// </summary>
public static class TextTransformation
{
    public static string? Apply(string? value, TextOperation operation, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return operation switch
        {
            TextOperation.Upper => value?.ToUpper(culture),
            TextOperation.Lower => value?.ToLower(culture),
            TextOperation.Trim => value?.Trim(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported text operation.")
        };
    }
}
