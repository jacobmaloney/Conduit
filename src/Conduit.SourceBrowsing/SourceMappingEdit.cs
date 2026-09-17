using System.Globalization;

namespace Conduit.SourceBrowsing;

public sealed record SourceMappingTarget(string Name, string Kind = "", string DataType = "String", bool Required = false, int? MaxLength = null, string? AliasOf = null)
{
    public string Key => Kind + ":" + Name;
}
public sealed record SourceMappingSchema(string Name, IReadOnlyList<SourceMappingTarget> Targets, string Note, string? AdditionalKind = null);
public sealed record SourceMappingEditContext(string Revision, SourceMappingSchema Schema, bool CanSave, string? Reason = null);
public sealed record SourceMappingSaveRequest(SourceBrowseSelection Selection, IReadOnlyList<SourceMappingRule> Rules, string Revision, Guid? ExpectedInstanceId = null);
public sealed record SourceMappingSaveResult(string Message, SourceBrowseContext Context);

/// <summary>Schema checks shared by the editor hosts. Does not infer a target schema from sampled values.</summary>
public static class SourceMappingEdit
{
    public static IReadOnlyList<SourceMappingRule> Validate(IReadOnlyList<SourceMappingRule> rules, SourceMappingSchema schema, bool allowEmptySource = false)
    {
        var snapshot = SourceMappingPreview.Snapshot(rules);
        if (schema.Targets.Count == 0) throw new SourceBrowseException("TargetSchemaUnavailable", "This destination has no supported mapping schema. Use the project editor.");
        if (snapshot.Where(r => r.Id.HasValue).GroupBy(r => r.Id).Any(g => g.Count() > 1))
            throw new SourceBrowseException("DuplicateMappingId", "A saved mapping was included more than once. Reload the mappings.");
        foreach (var rule in snapshot)
        {
            if (!allowEmptySource && string.IsNullOrWhiteSpace(rule.Source)) throw new SourceBrowseException("SourceFieldRequired", "Choose a source field for every saved mapping.");
            var target = FindTarget(schema, rule)
                ?? throw new SourceBrowseException("UnknownTarget", "Choose each target from this destination's supported fields.");
            if (rule.DefaultValue is { Length: > 0 } value && ValueError(value, target) is { } error)
                throw new SourceBrowseException("InvalidDefault", $"Default for {target.Name}: {error}");
        }
        foreach (var target in schema.Targets.Where(t => t.Required))
            if (!snapshot.Any(r => r.Target == target.Name && r.TargetType == target.Kind))
                throw new SourceBrowseException("RequiredTarget", $"Include the required target field {target.Name}.");
        if (snapshot.Select(r => FindTarget(schema, r)!).GroupBy(t => t.Kind + ":" + (t.AliasOf ?? t.Name), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new SourceBrowseException("DuplicateTarget", "Two mappings address the same destination field through aliases. Keep one mapping for that field.");
        return snapshot;
    }

    public static SourceMappingTarget? FindTarget(SourceMappingSchema schema, SourceMappingRule rule) =>
        schema.Targets.SingleOrDefault(t => t.Name == rule.Target && t.Kind == rule.TargetType) ??
        (schema.AdditionalKind == rule.TargetType && !string.IsNullOrWhiteSpace(rule.Target) && rule.Target.Length <= 200 &&
         !rule.Target.StartsWith('_') && rule.Target.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_') &&
         !schema.Targets.Any(t => t.Name.Equals(rule.Target, StringComparison.OrdinalIgnoreCase))
            ? new(rule.Target, rule.TargetType) : null);

    public static string? ValueError(object? value, SourceMappingTarget target)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(text)) return target.Required ? "A value is required." : null;
        if (target.MaxLength.HasValue && text.Length > target.MaxLength.Value) return $"Exceeds {target.MaxLength} characters.";
        return target.DataType switch
        {
            "Guid" when !Guid.TryParse(text, out _) => "Expected a GUID.",
            "DateTime" when !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) => "Expected a date and time.",
            "AD timestamp" when !IsAdTimestamp(text) => "Expected a date/time or an AD FILETIME value.",
            "Integer" when !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) => "Expected an integer.",
            "Boolean" when !bool.TryParse(text, out _) && text is not ("0" or "1") => "Expected true, false, 0 or 1.",
            "Boolean or yes/no" when !bool.TryParse(text, out _) && text is not ("0" or "1") && !text.Equals("yes", StringComparison.OrdinalIgnoreCase) && !text.Equals("no", StringComparison.OrdinalIgnoreCase) => "Expected true, false, 0, 1, yes or no.",
            _ => null
        };
    }

    private static bool IsAdTimestamp(string value)
    {
        if (!long.TryParse(value, out var fileTime)) return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        if (fileTime <= 0 || fileTime == long.MaxValue) return true; // Runtime unset sentinels.
        try { _ = DateTime.FromFileTimeUtc(fileTime); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    public static void RequireSavable(SourceMappingEditContext? edit, string revision)
    {
        if (edit?.CanSave != true) throw new SourceBrowseException("MappingSaveUnavailable", edit?.Reason ?? "Select a saved import step before saving mappings.");
        if (string.IsNullOrEmpty(revision) || revision != edit.Revision)
            throw new SourceBrowseException("MappingsChanged", "The project or mappings changed. Reload and review them before saving.");
    }
}
