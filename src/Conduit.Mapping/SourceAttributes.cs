namespace Conduit.Mapping;

public enum SourceLookupMode
{
    Dictionary,
    ExactThenLowerThenIgnoreCase,
    FirstIgnoreCase
}

public readonly record struct SelectedAttribute(bool Found, object? Value);

/// <summary>Preserves absence independently of an explicitly stored null.</summary>
public static class SourceAttributes
{
    public static SelectedAttribute Read<T>(IReadOnlyDictionary<string, T> source, string name,
        SourceLookupMode mode = SourceLookupMode.Dictionary)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(name);
        if (mode is not (SourceLookupMode.Dictionary or SourceLookupMode.ExactThenLowerThenIgnoreCase or SourceLookupMode.FirstIgnoreCase))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported source lookup mode.");

        if (mode == SourceLookupMode.FirstIgnoreCase)
        {
            foreach (var field in source)
                if (field.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) return new(true, field.Value);
            return new(false, null);
        }

        if (source.TryGetValue(name, out var value)) return new(true, value);
        if (mode == SourceLookupMode.ExactThenLowerThenIgnoreCase)
        {
            if (source.TryGetValue(name.ToLowerInvariant(), out value)) return new(true, value);
            foreach (var field in source)
                if (field.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) return new(true, field.Value);
        }
        return new(false, null);
    }
}
