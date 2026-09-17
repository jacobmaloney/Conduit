using Conduit.Mapping;

namespace Conduit.Sync.Orchestration;

/// <summary>
/// Compatibility entry point for saved Conduit mappings. The shared library owns
/// expression execution and missing-source semantics; the orchestrator owns I/O.
/// </summary>
public static class AttributeTransformer
{
    public static object? Apply(string expression, object? input) =>
        AttributeTransformExpression.Apply(expression, input);

    public static bool ProducesValueWhenSourceMissing(string expression) =>
        AttributeTransformExpression.ProducesValueWhenSourceMissing(expression);
}
