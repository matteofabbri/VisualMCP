using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// IDE0039: a lambda assigned to a local becomes a local function. The delegate allocation at the
/// assignment disappears — the function is converted back to a delegate only where it is used as one —
/// and the lambda stops being an expression buried in a statement.
/// <para>
/// The name comes from the variable, Pascal-cased, which is C#'s own convention for local functions. If
/// that name is already in scope the rule refuses instead of inventing a suffix: a name the user did not
/// choose is worse than no rewrite.
/// </para>
/// </summary>
public sealed class LambdaToLocalFunctionRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0039", "Use a local function",
        "Func<int, int> f = x => x + 1 → int F(int x) => x + 1; Func<int, int> f = F");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not LocalDeclarationStatementSyntax declaration) return null;
        if (!declaration.UsingKeyword.IsKind(SyntaxKind.None) || !declaration.AwaitKeyword.IsKind(SyntaxKind.None)) return null;
        if (declaration.Declaration.Variables.Count != 1) return null;

        var declarator = declaration.Declaration.Variables[0];
        if (declarator.Initializer?.Value is not LambdaExpressionSyntax lambda) return null;

        if (model.GetTypeInfo(declaration.Declaration.Type, cancellationToken).Type
            is not INamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateType) return null;
        if (delegateType.DelegateInvokeMethod is not { } invoke) return null;

        var name = LocalFunctionName(declarator.Identifier.ValueText);
        if (name is null) return null;

        // LookupSymbols applies the real scoping rules: a local, a parameter, a member or a type with
        // that name makes the function declaration fail to compile, so the rule stops here.
        if (model.LookupSymbols(declaration.SpanStart, name: name).Length > 0) return null;

        var parameters = lambda switch
        {
            SimpleLambdaExpressionSyntax simple => [simple.Parameter.Identifier.ValueText],
            ParenthesizedLambdaExpressionSyntax parenthesized =>
                parenthesized.ParameterList.Parameters.Select(parameter => parameter.Identifier.ValueText).ToList(),
            _ => null,
        };

        if (parameters is null || parameters.Count != invoke.Parameters.Length) return null;
        if (parameters.Any(string.IsNullOrEmpty)) return null;

        var position = declaration.SpanStart;
        var modifiers = lambda.Modifiers.ToString();
        var modifier = string.IsNullOrEmpty(modifiers) ? string.Empty : modifiers + " ";
        var parameterList = string.Join(", ", invoke.Parameters.Select((parameter, index) =>
            $"{TypeText(parameter.Type, model, position)} {parameters[index]}"));

        var header = $"{modifier}{TypeText(invoke.ReturnType, model, position)} {name}({parameterList})";
        var function = lambda.Block is { } block
            ? header + block.ToFullString().Trim()
            : header + " => " + lambda.ExpressionBody!.ToString().TrimEnd() + ";";

        var declarationText = $"{declaration.Declaration.Type} {declarator.Identifier.ValueText} = {name};";

        return MatchShape(Rule, declaration, declaration, function + "\n" + declarationText);
    }

    private static string? LocalFunctionName(string variable)
    {
        if (string.IsNullOrWhiteSpace(variable)) return null;
        return char.ToUpperInvariant(variable[0]) + variable[1..];
    }

    private static string TypeText(ITypeSymbol type, SemanticModel model, int position) =>
        type.ToMinimalDisplayString(model, position);
}
