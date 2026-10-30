using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Snitch.Languages.Gate;

/// <summary>
/// Compares two operation trees. A style rule that claims to preserve meaning must produce an
/// identical tree — the compiler gate only proves that the result compiles, and a rewrite whose error
/// compiles is exactly what the gate cannot see.
/// <para>
/// The comparison is normalised: implicit operations (the conversions the binder inserts) are skipped,
/// because they legitimately appear and disappear with syntax sugar, and symbols are compared by name
/// rather than by instance, because the two trees come from two different models.
/// </para>
/// </summary>
public static class SemanticEquivalence
{
    /// <summary>Every structural difference, at most <paramref name="max"/> of them, as readable paths.</summary>
    public static IReadOnlyList<string> Differences(IOperation? before, IOperation? after, int max = 4)
    {
        var differences = new List<string>();
        Walk(before, after, "op", differences, max);
        return differences;
    }

    private static void Walk(IOperation? before, IOperation? after, string path, List<string> differences, int max)
    {
        if (differences.Count >= max) return;

        if (before is null && after is null) return;
        if (before is null || after is null)
        {
            differences.Add($"{path}: {Describe(before)} vs {Describe(after)}");
            return;
        }

        if (before.Kind != after.Kind)
        {
            differences.Add($"{path}: {before.Kind} vs {after.Kind}");
            return;
        }

        if (before.ConstantValue.HasValue != after.ConstantValue.HasValue)
        {
            differences.Add($"{path}.Constant: {Describe(before.ConstantValue)} vs {Describe(after.ConstantValue)}");
        }
        else if (before.ConstantValue.HasValue && !Equals(before.ConstantValue.Value, after.ConstantValue.Value))
        {
            differences.Add($"{path}.Constant: {Describe(before.ConstantValue)} vs {Describe(after.ConstantValue)}");
        }

        var beforeOperator = Operator(before);
        var afterOperator = Operator(after);
        if (beforeOperator != afterOperator)
            differences.Add($"{path}.Operator: {beforeOperator} vs {afterOperator}");

        var beforeSymbol = Referenced(before);
        var afterSymbol = Referenced(after);
        if (beforeSymbol != afterSymbol)
            differences.Add($"{path}.Symbol: {beforeSymbol} vs {afterSymbol}");

        var beforeChildren = Children(before);
        var afterChildren = Children(after);
        if (beforeChildren.Count != afterChildren.Count)
        {
            differences.Add($"{path}.Children: {beforeChildren.Count} vs {afterChildren.Count}");
            return;
        }

        for (var index = 0; index < beforeChildren.Count; index++)
            Walk(beforeChildren[index], afterChildren[index], $"{path}.{beforeChildren[index].Kind}", differences, max);
    }

    /// <summary>
    /// The operations that are actually written: the binder's implicit conversions are not, and neither
    /// is the receiver of an unqualified access — <c>this.M</c> and <c>M</c> differ in syntax, not in
    /// what they do.
    /// </summary>
    private static List<IOperation> Children(IOperation operation) =>
        operation.ChildOperations
            .Where(child => !child.IsImplicit && child is not IInstanceReferenceOperation)
            .ToList();

    private static string? Operator(IOperation operation) => operation switch
    {
        IBinaryOperation binary => binary.OperatorKind.ToString(),
        IUnaryOperation unary => unary.OperatorKind.ToString(),
        ICompoundAssignmentOperation compound => compound.OperatorKind.ToString(),
        IIncrementOrDecrementOperation increment => (increment.Kind.ToString(), increment.IsPostfix).ToString(),
        _ => null,
    };

    private static string? Referenced(IOperation operation) => operation switch
    {
        IMemberReferenceOperation member => member.Member?.Name,
        IInvocationOperation invocation => invocation.TargetMethod?.Name,
        IObjectCreationOperation creation => creation.Constructor?.ContainingType?.Name,
        ITypeOfOperation typeOf => typeOf.TypeOperand?.Name,
        IDefaultValueOperation defaultValue => defaultValue.Type?.Name,
        _ => null,
    };

    private static string Describe(IOperation? operation) => operation is null ? "none" : operation.Kind.ToString();

    private static string Describe(Optional<object?> constant) =>
        !constant.HasValue ? "no constant" : constant.Value?.ToString() ?? "null";
}
