using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Style;

/// <summary>Shared checks for the rules that only touch <c>System.Linq.Enumerable</c>.</summary>
internal static class Linq
{
    /// <summary>Whether the call is the Enumerable extension, not Queryable and not a user type.</summary>
    public static bool IsEnumerable(InvocationExpressionSyntax invocation, SemanticModel model, CancellationToken cancellationToken) =>
        model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol { ContainingType: { } type } &&
        type.Name == "Enumerable" &&
        type.ContainingNamespace is { Name: "Linq" } linq &&
        linq.ContainingNamespace is { Name: "System" };
}

/// <summary>
/// IDE0120: <c>xs.Where(p).Any()</c> → <c>xs.Any(p)</c>, and the same for the operators that take a
/// predicate (<c>First</c>, <c>Count</c>, <c>Single</c>, …). Only the Enumerable forms: Queryable
/// builds an expression tree, which is a different thing.
/// </summary>
public sealed class LinqWhereChainRewrite : CSharpStyleRewrite
{
    private static readonly HashSet<string> PredicateOverloads = new(StringComparer.Ordinal)
    {
        "Any", "First", "FirstOrDefault", "Last", "LastOrDefault",
        "Single", "SingleOrDefault", "Count", "LongCount",
    };

    public override StyleRule Rule { get; } = new(
        "IDE0120", "Simplify LINQ expression",
        "values.Where(v => v > 0).Any() → values.Any(v => v > 0)");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not InvocationExpressionSyntax outer) return null;
        if (outer.ArgumentList.Arguments.Count != 0) return null;
        if (outer.Expression is not MemberAccessExpressionSyntax access) return null;
        if (!PredicateOverloads.Contains(access.Name.Identifier.ValueText)) return null;

        if (access.Expression is not InvocationExpressionSyntax where) return null;
        if (where.Expression is not MemberAccessExpressionSyntax whereAccess) return null;
        if (whereAccess.Name.Identifier.ValueText != "Where") return null;
        if (where.ArgumentList.Arguments.Count != 1) return null;

        if (!Linq.IsEnumerable(outer, model, cancellationToken)) return null;
        if (!Linq.IsEnumerable(where, model, cancellationToken)) return null;

        var predicate = where.ArgumentList.Arguments[0].Expression;
        return Match(node,
            ParseExpression($"{whereAccess.Expression}.{access.Name.Identifier.ValueText}({predicate})")
                .WithTriviaFrom(node));
    }
}

/// <summary>
/// IDE0121: <c>xs.Where(o =&gt; o is T).Cast&lt;T&gt;()</c> → <c>xs.OfType&lt;T&gt;()</c>. The lambda must
/// test its own parameter and nothing else, and the cast must name the same type.
/// </summary>
public sealed class LinqTypeCastRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0121", "Simplify LINQ expression",
        "values.Where(o => o is string).Cast<string>() → values.OfType<string>()");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not InvocationExpressionSyntax cast) return null;
        if (cast.ArgumentList.Arguments.Count != 0) return null;
        if (cast.Expression is not MemberAccessExpressionSyntax castAccess) return null;
        if (castAccess.Name is not GenericNameSyntax
            { Identifier.ValueText: "Cast", TypeArgumentList.Arguments.Count: 1 } castName) return null;

        if (castAccess.Expression is not InvocationExpressionSyntax where) return null;
        if (where.Expression is not MemberAccessExpressionSyntax whereAccess) return null;
        if (whereAccess.Name.Identifier.ValueText != "Where") return null;
        if (where.ArgumentList.Arguments.Count != 1) return null;

        var predicate = where.ArgumentList.Arguments[0].Expression;
        var body = predicate switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Body,
            ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } parenthesized => parenthesized.Body,
            _ => null,
        };

        if (body is not BinaryExpressionSyntax isCheck || !isCheck.IsKind(SyntaxKind.IsExpression)) return null;
        if (isCheck.Left is not IdentifierNameSyntax subject) return null;
        if (isCheck.Right is not TypeSyntax type) return null;

        var parameter = predicate switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText,
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters[0].Identifier.ValueText,
            _ => null,
        };

        if (subject.Identifier.ValueText != parameter) return null;
        if (castName.TypeArgumentList.Arguments[0].ToString() != type.ToString()) return null;

        if (!Linq.IsEnumerable(where, model, cancellationToken)) return null;
        if (!Linq.IsEnumerable(cast, model, cancellationToken)) return null;

        return Match(node, ParseExpression($"{whereAccess.Expression}.OfType<{type}>()").WithTriviaFrom(node));
    }
}

/// <summary>
/// IDE0220: a <c>foreach</c> whose variable has a different type from the collection's element type
/// hides a cast, and the repository can ask for it in source. When the preference is the default
/// <c>when_strongly_typed</c> only a generic collection is rewritten, never a legacy one.
/// </summary>
public sealed class ForeachExplicitCastRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0220", "Add explicit cast in foreach loop",
        "foreach (string item in objects) → foreach (string item in System.Linq.Enumerable.Cast<string>(objects))");

    public override string? RequiredOption => "dotnet_style_prefer_foreach_explicit_cast_in_source";

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        var preference = context.Options.ForeachExplicitCast;
        if (preference == ForeachCastPreference.None) return null;
        if (node is not ForEachStatementSyntax statement) return null;

        if (context.Model.GetDeclaredSymbol(statement, context.CancellationToken) is not ILocalSymbol variable)
            return null;

        var info = context.Model.GetForEachStatementInfo(statement);
        if (info.ElementType is null) return null;
        if (SymbolEqualityComparer.Default.Equals(info.ElementType, variable.Type)) return null;
        if (!info.ElementConversion.Exists) return null;

        if (preference == ForeachCastPreference.WhenStronglyTyped &&
            !IsStronglyTyped(context.Model, statement.Expression, context.CancellationToken))
        {
            return null;
        }

        var type = variable.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var cast = ParseExpression($"System.Linq.Enumerable.Cast<{type}>({statement.Expression})")
            .WithTriviaFrom(statement.Expression);

        return Match(node, statement.WithExpression(cast));
    }

    /// <summary>A generic collection: its element type is known through <c>IEnumerable&lt;T&gt;</c>.</summary>
    private static bool IsStronglyTyped(SemanticModel model, ExpressionSyntax collection, CancellationToken cancellationToken)
    {
        var type = model.GetTypeInfo(collection, cancellationToken).Type;
        if (type is not INamedTypeSymbol named) return false;

        var enumerable = $"System.Collections.Generic.IEnumerable<T>";
        return named.OriginalDefinition.ToDisplayString() == enumerable ||
               named.AllInterfaces.Any(face => face.OriginalDefinition.ToDisplayString() == enumerable);
    }
}
