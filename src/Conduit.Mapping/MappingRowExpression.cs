namespace Conduit.Mapping;

/// <summary>
/// One mapping row as the single value expression the shared mapping editor shows, so what the editor shows is
/// what the sync runs. A row is written in the one value grammar (<see cref="ValuePatternEngine"/>): text with
/// fields in braces, <c>{givenName} {sn}</c>, <c>{givenName:4}</c> (first four characters), <c>{sn:-4}</c> (last
/// four), <c>{upper(sn)}</c>, <c>Contractor</c> (fixed text). The older step form keeps working unchanged and
/// round-trips exactly: <c>{sAMAccountName} | trim | lower</c>, <c>{department} | default:Unassigned</c>,
/// <c>const:Contractor</c>. A row saves as a source attribute and a transform; a pattern row saves with no
/// source attribute and the pattern after <see cref="PatternPrefix"/>.
/// </summary>
public static class MappingRowExpression
{
    /// <summary>The grammar in one line, for the editor's hint.</summary>
    public const string Hint = "Write text and put a field in { }. {givenName:4} takes the first 4 characters, {sn:-4} the last 4, {upper(sn)} makes it upper case; text outside the braces is written as it is. The older step form still works: {mail} | trim | lower, const:X.";

    /// <summary>How a pattern row is marked in the saved transform.</summary>
    public const string PatternPrefix = "pattern:";

    /// <summary>True when the saved transform holds a pattern, not a step chain.</summary>
    public static bool IsPattern(string? transform) =>
        transform is not null && transform.TrimStart().StartsWith(PatternPrefix, StringComparison.Ordinal);

    /// <summary>The pattern a saved row holds, or null when the row is in the step form.</summary>
    public static string? PatternOf(string? transform) =>
        IsPattern(transform) ? transform!.TrimStart()[PatternPrefix.Length..] : null;

    /// <summary>
    /// Every source attribute a saved row reads: the one a step-form row names, or each field a pattern names.
    /// The run asks the source for exactly these.
    /// </summary>
    public static IEnumerable<string> SourceAttributesOf(string? sourceAttribute, string? transform)
    {
        if (PatternOf(transform) is { } pattern) return ValuePatternEngine.SourceAttributes(pattern);
        return string.IsNullOrWhiteSpace(sourceAttribute) ? Array.Empty<string>() : new[] { sourceAttribute.Trim() };
    }

    /// <summary>
    /// True when the text is in the older step form: one <c>{name}</c> at the start followed by nothing or
    /// by <c>| steps</c>, or a step chain with no braces that the step grammar accepts (<c>const:X</c>).
    /// Everything else is a pattern, including plain text, which is a fixed value.
    /// </summary>
    public static bool IsStepForm(string? expression)
    {
        var text = expression?.Trim() ?? string.Empty;
        if (text.Length == 0) return true;
        if (text.StartsWith('{'))
        {
            var close = text.IndexOf('}');
            if (close < 0) return true;   // still being typed
            var name = text[1..close];
            if (name.IndexOfAny(new[] { ':', '(', '#' }) >= 0) return false;
            var rest = text[(close + 1)..].Trim();
            // A | straight after the first field always means steps, so a step that wrongly names a second field
            // is refused as a step and never run as text with a | in it.
            return rest.Length == 0 || rest.StartsWith('|');
        }
        return text.IndexOfAny(new[] { '{', '}' }) < 0 && AttributeTransformExpression.ValidationError(text) is null;
    }

    /// <summary>The expression for a saved row.</summary>
    public static string Format(string? sourceAttribute, string? transform)
    {
        if (PatternOf(transform) is { } pattern) return pattern;
        var source = sourceAttribute?.Trim() ?? string.Empty;
        var chain = transform?.Trim() ?? string.Empty;
        if (source.Length == 0) return chain;
        return chain.Length == 0 ? "{" + source + "}" : "{" + source + "} | " + chain;
    }

    /// <summary>The row an expression saves as: the source attribute (blank when none) and the transform chain.</summary>
    public static (string SourceAttribute, string Transform) Parse(string? expression)
    {
        var text = expression?.Trim() ?? string.Empty;
        if (!IsStepForm(text)) return (string.Empty, PatternPrefix + text);
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
        if (!IsStepForm(text)) return PatternProblem(text);
        if (ShapeProblem(text) is { } shape) return shape;
        var (source, chain) = Parse(text);
        if (AttributeTransformExpression.ValidationError(chain) is { } error) return error;
        if (source.Length == 0 && !AttributeTransformExpression.ProducesValueWhenSourceMissing(chain))
            return "Start with the source attribute in { }, or write a fixed value with const:.";
        return null;
    }

    /// <summary>
    /// Why a pattern cannot run in a sync mapping, in the operator's words, or null when it can. A mapping reads
    /// the source record only: it has no organisation settings, no initiator and no uniqueness number.
    /// </summary>
    public static string? PatternProblem(string? pattern)
    {
        var text = pattern?.Trim() ?? string.Empty;
        if (text.Length == 0) return null;
        var depth = 0;
        foreach (var c in text)
        {
            if (c == '{') depth++;
            else if (c == '}') depth--;
            if (depth is < 0 or > 1) return "Put each field in its own { }, for example {givenName} {sn}.";
        }
        if (depth != 0) return "Close the field with }.";
        if (ValuePatternEngine.HasCounter(text)) return "A uniqueness number {#} is not available in a sync mapping.";
        if (text.Contains("{Org.", StringComparison.OrdinalIgnoreCase) || text.Contains("{Initiator.", StringComparison.OrdinalIgnoreCase))
            return "A sync mapping reads the source record only: {Org.…} and {Initiator.…} are not available here.";
        // Every field the pattern names is given a value, so what is left unknown is the grammar itself.
        var bag = ValuePatternEngine.SourceAttributes(text).ToDictionary(n => n, _ => (string?)"x", StringComparer.OrdinalIgnoreCase);
        var unknown = ValuePatternEngine.Candidate(text, new ValuePatternEngine.PatternContext(bag, Now: DateTime.UnixEpoch), null, 0).UnknownTokens;
        return unknown.Count == 0 ? null : "This part cannot be read: " + string.Join(", ", unknown.Distinct()) + ".";
    }

    /// <summary>
    /// The value a pattern row writes for one source record. The record is read case-insensitively; a field the
    /// record does not carry renders as nothing. <paramref name="anyFieldPresent"/> says whether the record
    /// carried at least one of the fields the pattern names, so the caller can tell "came out empty" from
    /// "the source said nothing about it".
    /// </summary>
    public static string RenderPattern<T>(string pattern, IReadOnlyDictionary<string, T> source, out bool anyFieldPresent)
    {
        anyFieldPresent = false;
        var bag = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ValuePatternEngine.SourceAttributes(pattern))
        {
            var read = SourceAttributes.Read(source, name, SourceLookupMode.ExactThenLowerThenIgnoreCase);
            if (read.Found) anyFieldPresent = true;
            bag[name] = read.Value switch
            {
                null => null,
                string text => text,
                System.Collections.IEnumerable many => many.Cast<object?>().Select(v => v?.ToString()).FirstOrDefault(v => !string.IsNullOrEmpty(v)),
                var other => Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture),
            };
        }
        return ValuePatternEngine.Candidate(pattern, new ValuePatternEngine.PatternContext(bag), null, 0).Value;
    }

    /// <summary>
    /// What a row's expression writes for one source record, in either form, as text. For previews; the run
    /// itself goes through the mapping plan, which also decides whether the field is written at all.
    /// </summary>
    public static string Evaluate<T>(string? expression, IReadOnlyDictionary<string, T> source)
    {
        var (sourceAttribute, transform) = Parse(expression);
        if (PatternOf(transform) is { } pattern) return RenderPattern(pattern, source, out _);
        object? value = sourceAttribute.Length == 0 ? null : SourceAttributes.Read(source, sourceAttribute, SourceLookupMode.ExactThenLowerThenIgnoreCase).Value;
        if (transform.Length > 0) value = AttributeTransformExpression.Apply(transform, value);
        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>
    /// MAP-EDITOR-SOURCE-01. What is wrong with the expression's shape (the braces, the one source, the | separators),
    /// whatever the steps mean; a host with its own steps judges those itself.
    /// </summary>
    public static string? ShapeProblem(string? expression)
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
        var (_, chain) = Parse(text);
        return chain.IndexOfAny(new[] { '{', '}' }) >= 0 ? "Each row reads one source attribute, at the start: {name} | step | step." : null;
    }
}
