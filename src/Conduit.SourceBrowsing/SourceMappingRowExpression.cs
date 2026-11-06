using Conduit.Mapping;

namespace Conduit.SourceBrowsing;

/// <summary>
/// MAP-EDITOR-SOURCE-01. A source-browser mapping row as the one value expression the shared mapping editor shows.
/// A chain host (<see cref="SourceMappingContext.Expression"/>) is exactly <see cref="MappingRowExpression"/>:
/// <c>{source} | step | step</c>, where a default is its <c>default:X</c> step. An operation host reads one source, at
/// most one of its operations and an optional <c>default:X</c>, written where that host applies it: first when
/// <see cref="SourceMappingContext.DefaultBeforeTransform"/> (an empty value takes the default, then the operation runs
/// on it), otherwise last (the default is written unchanged). So the order on screen is the order that runs.
/// </summary>
public static class SourceMappingRowExpression
{
    private const string DefaultStage = "default:";

    /// <summary>The expression for a saved row.</summary>
    public static string Format(SourceMappingContext context, string? source, string? transformation, string? defaultValue)
    {
        if (context.Expression) return MappingRowExpression.Format(source, transformation);
        var operation = transformation?.Trim() ?? string.Empty;
        if (operation.Equals("Direct", StringComparison.OrdinalIgnoreCase)) operation = string.Empty;
        var stages = new List<string>(2);
        if (defaultValue != null && context.DefaultBeforeTransform) stages.Add(DefaultStage + defaultValue);
        if (operation.Length > 0) stages.Add(operation);
        if (defaultValue != null && !context.DefaultBeforeTransform) stages.Add(DefaultStage + defaultValue);
        return MappingRowExpression.Format(source, string.Join(" | ", stages));
    }

    /// <summary>The row an expression saves as. An operation is written in the host's own spelling.</summary>
    public static (string Source, string Transformation, string? DefaultValue) Parse(SourceMappingContext context, string? expression)
    {
        var (source, chain) = MappingRowExpression.Parse(expression);
        if (context.Expression) return (source, chain, null);
        string? defaultValue = null;
        var operation = string.Empty;
        foreach (var stage in Stages(chain))
        {
            if (stage.StartsWith(DefaultStage, StringComparison.OrdinalIgnoreCase)) defaultValue = stage[DefaultStage.Length..];
            else operation = context.Transformations.FirstOrDefault(t => t.Equals(stage, StringComparison.OrdinalIgnoreCase)) ?? stage;
        }
        return (source, operation, defaultValue);
    }

    /// <summary>Why this host cannot run the expression, in the operator's words, or null when it can.</summary>
    public static string? Problem(SourceMappingContext context, string? expression)
    {
        var text = expression?.Trim() ?? string.Empty;
        if (text.Length == 0) return null;
        if (context.Expression) return MappingRowExpression.Problem(text);
        if (MappingRowExpression.ShapeProblem(text) is { } shape) return shape;
        var (source, chain) = MappingRowExpression.Parse(text);
        if (source.Length == 0) return "Start with the source field in { }, for example {mail}.";
        var stages = Stages(chain);
        var operations = stages.Where(s => !IsDefault(s)).ToArray();
        var defaults = stages.Count(IsDefault);
        if (operations.Length > 1) return "Use one operation per field here: " + Operations(context) + ".";
        if (operations.Length == 1 && !context.Transformations.Contains(operations[0], StringComparer.OrdinalIgnoreCase))
            return $"{operations[0]} is not an operation here. Use one of: {Operations(context)}.";
        if (defaults > 0 && !context.Defaults) return "This host has no default value; remove default:.";
        if (defaults > 1) return "Write default: once.";
        if (defaults == 1 && operations.Length == 1)
        {
            var defaultFirst = IsDefault(stages[0]);
            if (context.DefaultBeforeTransform && !defaultFirst)
                return $"Here an empty value takes the default first, then {operations[0]} runs on it. Write default: before {operations[0]}.";
            if (!context.DefaultBeforeTransform && defaultFirst)
                return $"Here the default is written unchanged, without {operations[0]}. Write default: last.";
        }
        return null;
    }

    /// <summary>The grammar in one line, for the editor's hint.</summary>
    public static string Hint(SourceMappingContext context) => context.Expression
        ? MappingRowExpression.Hint
        : "Write the source field in { }, then at most one operation after |: " + Operations(context) + "."
          + (!context.Defaults ? string.Empty
              : context.DefaultBeforeTransform ? " default:X before the operation fills an empty value, which the operation then changes."
              : " default:X last is written unchanged when the field is missing.");

    /// <summary>An example for an empty value input.</summary>
    public static string Placeholder(SourceMappingContext context) => context.Expression
        ? "{sAMAccountName} | trim | lower"
        : context.Transformations.FirstOrDefault(t => !t.Equals("Direct", StringComparison.OrdinalIgnoreCase)) is { } operation ? "{mail} | " + operation : "{mail}";

    private static bool IsDefault(string stage) => stage.StartsWith(DefaultStage, StringComparison.OrdinalIgnoreCase);

    private static string Operations(SourceMappingContext context) => string.Join(", ", context.Transformations);

    // Every | starts a step; a value with | of its own stays as saved until the row is edited.
    private static List<string> Stages(string chain) =>
        chain.Trim().Length == 0 ? new() : chain.Split('|').Select(s => s.Trim()).ToList();
}
