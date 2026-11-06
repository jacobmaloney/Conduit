namespace Conduit.Mapping;

/// <summary>
/// MAP-EDITOR-CONDUIT-01. One mapping row (source attribute + saved transform chain) as the single value expression the
/// shared mapping editor shows: <c>{sAMAccountName} | trim | lower</c> (copy the source through stages),
/// <c>{department} | default:Unassigned</c> (fall back when it is empty), <c>const:Contractor</c> (no source, a fixed
/// value). It is exactly <c>{SourceAttribute}</c> followed by <see cref="AttributeTransformExpression"/>'s chain, so a row
/// round-trips unchanged and what the editor shows is what the sync runs.
/// </summary>
public static class MappingRowExpression
{
    /// <summary>The expression for a saved row.</summary>
    public static string Format(string? sourceAttribute, string? transform)
    {
        var source = sourceAttribute?.Trim() ?? string.Empty;
        var chain = transform?.Trim() ?? string.Empty;
        if (source.Length == 0) return chain;
        return chain.Length == 0 ? "{" + source + "}" : "{" + source + "} | " + chain;
    }

    /// <summary>The row an expression saves as: the source attribute (blank when none) and the transform chain.</summary>
    public static (string SourceAttribute, string Transform) Parse(string? expression)
    {
        var text = expression?.Trim() ?? string.Empty;
        if (!text.StartsWith('{')) return (string.Empty, text);
        var close = text.IndexOf('}');
        if (close < 0) return (text[1..].Trim(), string.Empty);   // still being typed
        var rest = text[(close + 1)..].Trim();
        if (rest.StartsWith('|')) rest = rest[1..].Trim();
        return (text[1..close].Trim(), rest);
    }

    /// <summary>Why the sync cannot run this expression, in the operator's words, or null when it can.</summary>
    public static string? Problem(string? expression)
    {
        var text = expression?.Trim() ?? string.Empty;
        if (text.Length == 0) return null;
        if (text.StartsWith('{'))
        {
            var close = text.IndexOf('}');
            if (close < 0) return "Close the source attribute with }.";
            if (text[1..close].Trim().Length == 0) return "Name the source attribute inside { }.";
            var after = text[(close + 1)..].Trim();
            if (after.Length > 0 && !after.StartsWith('|')) return "Separate the source attribute from each step with |, for example {mail} | lower.";
        }
        var (source, chain) = Parse(text);
        if (chain.IndexOfAny(new[] { '{', '}' }) >= 0) return "Each row reads one source attribute, at the start: {name} | step | step.";
        if (AttributeTransformExpression.ValidationError(chain) is { } error) return error;
        if (source.Length == 0 && !AttributeTransformExpression.ProducesValueWhenSourceMissing(chain))
            return "Start with the source attribute in { }, or write a fixed value with const:.";
        return null;
    }
}
