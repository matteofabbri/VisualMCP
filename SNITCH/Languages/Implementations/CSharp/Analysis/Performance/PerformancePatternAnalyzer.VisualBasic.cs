using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.CSharp.Analysis.Performance;

/// <summary>
/// The three allocation anti-patterns of <see cref="PerformancePatternAnalyzer"/>, read from VB
/// syntax: string appending in a loop (<c>&amp;=</c> always concatenates; <c>+=</c> does when the left
/// side is a String), a LINQ materialiser inside a loop, and a collection or a constant-size array
/// allocated per iteration.
/// </summary>
public sealed partial class PerformancePatternAnalyzer
{
    private static PerformanceFinding? DescribeVisualBasic(SyntaxNode node, SemanticModel model, Document document)
    {
        var file = document.FilePath ?? document.Name;
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var member = MemberVisualBasic(node);

        switch (node)
        {
            // VB has one AssignmentStatementSyntax for every compound form; the kind says which.
            case AssignmentStatementSyntax assignment
                when assignment.IsKind(SyntaxKind.ConcatenateAssignmentStatement) ||
                     (assignment.IsKind(SyntaxKind.AddAssignmentStatement) &&
                      model.GetTypeInfo(assignment.Left).Type?.SpecialType == SpecialType.System_String):
                return new PerformanceFinding(
                    "PF0001", "warning", file, line, member,
                    $"'{assignment.Left}' is a string appended inside a loop: each iteration copies the whole value.",
                    Snippet(assignment));

            case InvocationExpressionSyntax invocation
                when invocation.Expression is MemberAccessExpressionSyntax access &&
                     Materializers.Contains(access.Name.Identifier.ValueText):
            {
                var receiver = model.GetTypeInfo(access.Expression).Type;
                if (receiver is null || receiver.SpecialType == SpecialType.System_String) return null;

                return new PerformanceFinding(
                    "PF0002", "info", file, line, member,
                    $"'{access.Name.Identifier.ValueText}()' inside a loop rebuilds the sequence on every iteration.",
                    Snippet(invocation));
            }

            case ArrayCreationExpressionSyntax array when HasConstantBounds(array):
                return new PerformanceFinding(
                    "PF0003", "info", file, line, member,
                    "An array with a constant size is allocated on every iteration of the loop.",
                    Snippet(array));

            case ObjectCreationExpressionSyntax creation when IsConstantAllocationVisualBasic(creation, model):
                return new PerformanceFinding(
                    "PF0003", "info", file, line, member,
                    "A collection is allocated on every iteration of the loop.",
                    Snippet(creation));

            default:
                return null;
        }
    }

    private static bool HasConstantBounds(ArrayCreationExpressionSyntax array) =>
        array.ArrayBounds is { Arguments.Count: 1 } bounds &&
        bounds.Arguments[0] is SimpleArgumentSyntax { Expression: LiteralExpressionSyntax };

    private static bool IsConstantAllocationVisualBasic(ObjectCreationExpressionSyntax creation, SemanticModel model)
    {
        var type = model.GetTypeInfo(creation).Type;
        if (type is null) return false;

        // The interface is matched by metadata name, not by a display string: VB spells a generic as
        // `ICollection(Of T)` and a display comparison would silently stop matching.
        var isCollection = type.AllInterfaces.Any(@interface =>
            @interface.OriginalDefinition.Name == "ICollection" &&
            @interface.OriginalDefinition.Arity == 1 &&
            @interface.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic");
        if (!isCollection) return false;

        return creation.ArgumentList is null || creation.ArgumentList.Arguments.Count == 0;
    }

    /// <summary>The containing member of a VB node: the nearest declaration of a body.</summary>
    private static string MemberVisualBasic(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case MethodBlockSyntax method:
                    return method.SubOrFunctionStatement.Identifier.ValueText;
                case ConstructorBlockSyntax:
                    return "New";
                case OperatorBlockSyntax @operator:
                    return "operator " + @operator.OperatorStatement.OperatorToken.Text;
                case AccessorBlockSyntax accessor:
                    return accessor.AccessorStatement.AccessorKeyword.Text;
                case PropertyBlockSyntax property:
                    return property.PropertyStatement.Identifier.ValueText;
                case TypeBlockSyntax type:
                    return type.BlockStatement.Identifier.ValueText;
            }
        }

        return "(file)";
    }
}
