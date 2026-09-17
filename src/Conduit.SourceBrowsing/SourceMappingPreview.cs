using Conduit.Mapping;

namespace Conduit.SourceBrowsing;

public sealed record SourceMappingRule(string Source, string Target, string Transformation = "", string? DefaultValue = null, bool Required = false, Guid? Id = null, string TargetType = "");
public sealed record SourceMappingContext(string Profile, string Help, bool Expression, bool Defaults, bool Required,
    IReadOnlyList<string> Transformations, IReadOnlyList<SourceMappingRule> SavedRules, string? Notice = null, SourceMappingEditContext? Edit = null);
public sealed record SourceMappedField(string Source, string Target, string Input, string Output, string Outcome);

/// <summary>Bounded preview policy; host callbacks execute the existing runtime mapping dialect on raw source values.</summary>
public static class SourceMappingPreview
{
    public const int MaximumRules = 50;
    public static IReadOnlyList<SourceMappingRule> Snapshot(IReadOnlyList<SourceMappingRule> rules)
    {
        if (rules.Count is < 1 or > MaximumRules)
            throw new SourceBrowseException("InvalidMappings", "Choose between 1 and 50 fields to preview.");
        var snapshot = rules.ToArray();
        foreach (var rule in snapshot)
        {
            if (rule == null || rule.Source == null || string.IsNullOrWhiteSpace(rule.Target) || rule.Source.Length > 256 || rule.Target.Length > 256 || rule.Transformation?.Length > 1024 || rule.DefaultValue?.Length > 1024)
                throw new SourceBrowseException("InvalidMappings", "Each mapping needs a target field. Field names are limited to 256 characters and transformations/defaults to 1,024.");
            if (SourceBrowseSample.IsSensitive(rule.Source) || SourceBrowseSample.IsSensitive(rule.Target))
                throw new SourceBrowseException("SensitiveMapping", "Secret source and target fields cannot be previewed, including through a renamed field.");
        }
        if (snapshot.GroupBy(r => r.Target, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new SourceBrowseException("DuplicateTarget", "Use one mapping per target field in this preview.");
        return snapshot;
    }

    public static SourceMappingContext WithSaved(SourceMappingContext context, IEnumerable<SourceMappingRule> saved)
    {
        var rules = saved.ToArray();
        var safe = rules.Where(r => !SourceBrowseSample.IsSensitive(r.Source) && !SourceBrowseSample.IsSensitive(r.Target)).Take(MaximumRules).ToArray();
        return context with { SavedRules = safe, Notice = safe.Length == rules.Length ? null : $"Showing {safe.Length} of {rules.Length} saved mappings. Secret fields are excluded; previews support up to {MaximumRules} mappings." };
    }

    public static IReadOnlyList<SourceMappedField> Render(IReadOnlyList<SourceMappingRule> rules,
        Func<SourceMappingRule, SelectedAttribute> read, Func<SourceMappingRule, ProjectedField> project, SourceMappingSchema? schema = null)
    {
        // Validate at this boundary too: a future caller cannot bypass secret suppression.
        return Snapshot(rules).Select(rule =>
        {
            var source = read(rule);
            if (source.Value is byte[]) return new SourceMappedField(rule.Source, rule.Target, "[binary value]", "[not previewed]", "Binary field hidden");
            ProjectedField field;
            try { field = project(rule); }
            catch (SourceBrowseException) { throw; }
            catch { throw new SourceBrowseException("MappingFailed", "A transformation could not be evaluated. Check its syntax and arguments; no partial mapping preview was retained."); }
            var target = schema == null ? null : SourceMappingEdit.FindTarget(schema, rule);
            var valueError = target == null ? null : SourceMappingEdit.ValueError(field.ShouldWrite ? field.Value : null, target);
            return new SourceMappedField(rule.Source, rule.Target,
                source.Found ? SourceBrowseSample.Display(source.Value) : "(missing)",
                field.ShouldWrite ? SourceBrowseSample.Display(field.Value) : "(omitted)",
                valueError != null ? "Invalid: " + valueError : !field.ShouldWrite ? "Omitted" : field.UsedDefault ? "Default applied" : field.Value == null ? "Null output" : "Projected");
        }).ToArray();
    }
}
