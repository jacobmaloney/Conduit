using System.Globalization;

namespace Conduit.Mapping;

/// <summary>
/// Conduit's saved expression dialect: upper, lower, trim, prefix:X, suffix:X,
/// substring:start[:length], replace:old:new, default:X and const:X, chained with '|'.
/// Casing and integer parsing are invariant. Unknown stages pass through unchanged.
/// Host adapters own other saved vocabularies, defaults and attribute selection.
/// </summary>
public static class AttributeTransformExpression
{
    // Authoring is strict while execution preserves the historical passthrough dialect.
    public static string? ValidationError(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        foreach (var raw in expression.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var (operation, argument) = SplitOpArg(raw.Trim());
            switch (operation.ToLowerInvariant())
            {
                case "upper": case "lower": case "trim":
                    if (argument != null) return "upper, lower and trim do not take arguments.";
                    break;
                case "prefix": case "suffix": case "default": case "const":
                    if (argument == null) return "Use a colon before the expression's value.";
                    break;
                case "replace":
                    if (argument == null || argument.IndexOf(':') <= 0) return "Use replace:old:new with a nonempty old value.";
                    break;
                case "substring":
                    var parts = (argument ?? "").Split(':');
                    if (parts.Length is < 1 or > 2 || parts.Any(p => !int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0))
                        return "Use substring:start or substring:start:length with nonnegative integers.";
                    break;
                default: return "Unknown transformation. Choose a supported expression operation.";
            }
        }
        return null;
    }

    public static object? Apply(string expression, object? input)
    {
        if (string.IsNullOrWhiteSpace(expression)) return input;

        var stages = expression.Split('|', StringSplitOptions.RemoveEmptyEntries);
        object? value = input;
        foreach (var rawStage in stages)
        {
            value = ApplySingle(rawStage.Trim(), value);
        }
        return value;
    }

    /// <summary>
    /// Returns true if this expression can produce a value even when the source
    /// attribute is missing entirely (e.g. <c>default:foo</c>). Used by the
    /// orchestrator to decide whether to write the sink slot at all.
    /// </summary>
    public static bool ProducesValueWhenSourceMissing(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;
        foreach (var rawStage in expression.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var s = rawStage.Trim();
            if (s.StartsWith("const:", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.StartsWith("default:", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.StartsWith("prefix:", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.StartsWith("suffix:", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static object? ApplySingle(string stage, object? input)
    {
        if (string.IsNullOrEmpty(stage)) return input;

        var (op, arg) = SplitOpArg(stage);

        switch (op.ToLowerInvariant())
        {
            case "upper":
                return TextTransformation.Apply(CoerceString(input), TextOperation.Upper, CultureInfo.InvariantCulture);
            case "lower":
                return TextTransformation.Apply(CoerceString(input), TextOperation.Lower, CultureInfo.InvariantCulture);
            case "trim":
                return TextTransformation.Apply(CoerceString(input), TextOperation.Trim, CultureInfo.InvariantCulture);
            case "prefix":
                return string.Concat(arg ?? string.Empty, CoerceString(input) ?? string.Empty);
            case "suffix":
                return string.Concat(CoerceString(input) ?? string.Empty, arg ?? string.Empty);
            case "default":
                {
                    var s = CoerceString(input);
                    return AttributeDefaults.Apply(s, arg, DefaultWhen.NullOrEmptyString);
                }
            case "const":
                // Always emit the literal, ignoring the source value. Pairs with a
                // blank SourceAttribute to stamp a fixed value on every object.
                return arg ?? string.Empty;
            case "substring":
                {
                    var s = CoerceString(input);
                    if (s is null) return null;
                    var parts = (arg ?? string.Empty).Split(':');
                    if (parts.Length == 0) return s;
                    if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)) return s;
                    if (start < 0 || start >= s.Length) return string.Empty;
                    if (parts.Length == 1) return s[start..];
                    if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var len)) return s[start..];
                    if (len <= 0) return string.Empty;
                    return start + len > s.Length ? s[start..] : s.Substring(start, len);
                }
            case "replace":
                {
                    var s = CoerceString(input);
                    if (s is null) return null;
                    // replace:OLD:NEW — split on the first ':' inside arg.
                    var first = arg?.IndexOf(':') ?? -1;
                    if (arg is null || first < 0) return s;
                    var oldPart = arg[..first];
                    var newPart = arg[(first + 1)..];
                    return s.Replace(oldPart, newPart, StringComparison.Ordinal);
                }
            default:
                // Unknown — pass through so a typo doesn't kill the row.
                return input;
        }
    }

    private static (string Op, string? Arg) SplitOpArg(string stage)
    {
        var idx = stage.IndexOf(':');
        if (idx < 0) return (stage, null);
        return (stage[..idx], stage[(idx + 1)..]);
    }

    private static string? CoerceString(object? v)
    {
        if (v is null) return null;
        if (v is string s) return s;
        if (v is System.Collections.IList list && list.Count > 0) return list[0]?.ToString();
        return v.ToString();
    }
}
