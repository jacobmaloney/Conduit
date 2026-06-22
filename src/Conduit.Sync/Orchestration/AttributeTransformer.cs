using System;
using System.Globalization;

namespace Conduit.Sync.Orchestration;

/// <summary>
/// Deliberately small DSL for AttributeMapping.TransformExpr. The whitelist:
///
///   upper              — ToUpperInvariant
///   lower              — ToLowerInvariant
///   trim               — Trim whitespace
///   prefix:X           — prepend literal X (X may contain ':')
///   suffix:X           — append literal X
///   substring:N        — start at N (length = remainder)
///   substring:N:M      — start at N, length M
///   replace:OLD:NEW    — String.Replace(OLD, NEW)
///   default:VAL        — return VAL when input is null/empty
///   const:VAL          — ALWAYS return VAL, ignoring the source value entirely
///                        (ARS Synchronization Service "constant" value-generation:
///                         set the sink attribute to a fixed literal on every object,
///                         e.g. const:Synced by Conduit to stamp every description).
///
/// Chains via "|": e.g. "trim | lower | prefix:user_" → trim, lowercase, prefix.
/// Unknown expressions are skipped (logged-but-non-fatal at orchestration time
/// — we keep transforms forgiving so a typo on Operator A's mapping doesn't
/// brick Operator B's run).
///
/// Intentionally NOT Roslyn / Scriban / etc. — IC's mapping engine uses a
/// custom mini-language with the same surface; mirroring it minimally avoids
/// a 3MB dependency that does 50× more than we need.
/// </summary>
public static class AttributeTransformer
{
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
                return CoerceString(input)?.ToUpperInvariant();
            case "lower":
                return CoerceString(input)?.ToLowerInvariant();
            case "trim":
                return CoerceString(input)?.Trim();
            case "prefix":
                return string.Concat(arg ?? string.Empty, CoerceString(input) ?? string.Empty);
            case "suffix":
                return string.Concat(CoerceString(input) ?? string.Empty, arg ?? string.Empty);
            case "default":
                {
                    var s = CoerceString(input);
                    return string.IsNullOrEmpty(s) ? arg : s;
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
