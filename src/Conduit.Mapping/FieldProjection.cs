namespace Conduit.Mapping;

public enum DefaultWhen { Null, NullOrEmptyString, NullOrWhiteSpaceString }
public enum OutputPresence { SourcePresent, NonNullValue, Always }

public sealed record DefaultValueRule(object? Value, DefaultWhen When = DefaultWhen.Null, bool Transform = false);

/// <summary>Host policy for one field, independent of either product's saved schema.</summary>
public sealed record FieldProjectionRule(string SourceAttribute, string TargetAttribute)
{
    public SourceLookupMode Lookup { get; init; } = SourceLookupMode.Dictionary;
    public DefaultValueRule? Default { get; init; }
    public Func<object?, object?>? Transform { get; init; }
    public OutputPresence Presence { get; init; } = OutputPresence.SourcePresent;
}

public readonly record struct ProjectedField(string TargetAttribute, object? Value, bool ShouldWrite,
    bool SourceFound, bool UsedDefault);

public static class AttributeDefaults
{
    public static bool NeedsDefault(object? value, DefaultWhen when) => when switch
    {
        DefaultWhen.Null => value is null,
        DefaultWhen.NullOrEmptyString => value is null || value is string { Length: 0 },
        DefaultWhen.NullOrWhiteSpaceString => value is null || value is string text && string.IsNullOrWhiteSpace(text),
        _ => throw new ArgumentOutOfRangeException(nameof(when), when, "Unsupported default condition.")
    };

    public static object? Apply(object? value, object? defaultValue, DefaultWhen when) =>
        NeedsDefault(value, when) ? defaultValue : value;
}

/// <summary>
/// Shared selection, default/transform ordering and target-presence decision.
/// Evaluates only: never mutates source or destination, performs I/O or catches
/// transformation failures. Hosts own target coercion and committing a projection.
/// </summary>
public static class FieldProjection
{
    public static ProjectedField Project<T>(IReadOnlyDictionary<string, T> source, FieldProjectionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Presence is not (OutputPresence.SourcePresent or OutputPresence.NonNullValue or OutputPresence.Always))
            throw new ArgumentOutOfRangeException(nameof(rule), "Unsupported output presence policy.");

        var selected = SourceAttributes.Read(source, rule.SourceAttribute, rule.Lookup);
        var usedDefault = rule.Default is not null && AttributeDefaults.NeedsDefault(selected.Value, rule.Default.When);
        var value = usedDefault ? rule.Default!.Value : selected.Value;
        if (rule.Transform is not null && (!usedDefault || rule.Default!.Transform))
            value = rule.Transform(value);

        var shouldWrite = rule.Presence switch
        {
            OutputPresence.Always => true,
            OutputPresence.NonNullValue => value is not null,
            _ => selected.Found || usedDefault
        };
        return new(rule.TargetAttribute, value, shouldWrite, selected.Found, usedDefault);
    }
}
