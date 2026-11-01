using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// IDE0390 / IDE0391: an <c>async</c> method that never awaits runs synchronously but still pays for the
/// state machine. The fix removes <c>async</c> and makes the returns carry the Task themselves —
/// <c>return Task.FromResult(value)</c>, or <c>Task.CompletedTask</c> for a plain <c>Task</c>. The two
/// ids differ only in what is being rewritten: an ordinary member (IDE0390) or an override / interface
/// implementation (IDE0391), which a team may want to leave alone because callers await it through the
/// base contract.
/// </summary>
public sealed class MakeMethodSynchronousRewrite : CSharpStyleRewrite
{
    private const string Task = "System.Threading.Tasks.Task";

    private static readonly StyleRule Regular = new(
        "IDE0390", "Make method synchronous",
        "async Task<int> M() { return 42; } → Task<int> M() { return Task.FromResult(42); }");

    private static readonly StyleRule Contract = new(
        "IDE0391", "Make method synchronous",
        "the same fix for an override or an interface implementation");

    public override StyleRule Rule => Regular;

    public override IReadOnlyList<StyleRule> Rules => [Regular, Contract];

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not MethodDeclarationSyntax method || method.Body is null) return null;
        if (!method.Modifiers.Any(SyntaxKind.AsyncKeyword)) return null;

        // Any await at all keeps the method asynchronous — including one inside a nested lambda, which
        // we do not try to reason about.
        if (method.Body.DescendantNodes().OfType<AwaitExpressionSyntax>().Any()) return null;

        if (model.GetDeclaredSymbol(method, cancellationToken) is not IMethodSymbol symbol) return null;

        var rewritten = WithoutAsync(method, symbol, cancellationToken);
        if (rewritten is null) return null;

        return Match(IsContractMember(symbol) ? Contract : Regular, node, rewritten);
    }

    /// <summary>The method with <c>async</c> gone and every return carrying the task.</summary>
    private static MethodDeclarationSyntax? WithoutAsync(
        MethodDeclarationSyntax method, IMethodSymbol symbol, CancellationToken cancellationToken)
    {
        var name = symbol.ReturnType.ToDisplayString();

        if (symbol.ReturnsVoid)
        {
            // `async void M()` → `void M()`: the body is already valid unchanged.
            return method.WithModifiers(WithoutAsync(method.Modifiers));
        }

        var isPlainTask = name == Task;
        var isGenericTask = name.StartsWith($"{Task}<", StringComparison.Ordinal);
        if (!isPlainTask && !isGenericTask) return null;

        var returns = method.Body!
            .DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .Where(statement => BelongsToTheMethod(statement, method))
            .ToList();

        // A plain Task has nothing to wrap: every `return;` becomes CompletedTask, and a body that can
        // fall off its end gets one appended.
        if (isPlainTask && returns.Any(statement => statement.Expression is not null)) return null;
        if (isGenericTask && returns.Any(statement => statement.Expression is null)) return null;

        var body = method.Body!.ReplaceNodes(returns, (original, _) => isPlainTask
            ? ReturnStatement(ParseExpression($"{Task}.CompletedTask")).WithTriviaFrom(original)
            : ReturnStatement(ParseExpression($"{Task}.FromResult({original.Expression})")).WithTriviaFrom(original));

        if (isPlainTask && !FallsOffTheEnd(body))
        {
            // Nothing to do: the last statement already returns.
        }
        else if (isPlainTask)
        {
            body = body.WithStatements(body.Statements.Add(
                ReturnStatement(ParseExpression($"{Task}.CompletedTask"))));
        }

        return method
            .WithModifiers(WithoutAsync(method.Modifiers))
            .WithBody(body);
    }

    private static SyntaxTokenList WithoutAsync(SyntaxTokenList modifiers) =>
        TokenList(modifiers.Where(token => !token.IsKind(SyntaxKind.AsyncKeyword)));

    /// <summary>A return inside a nested lambda or local function returns from that, not from the method.</summary>
    private static bool BelongsToTheMethod(ReturnStatementSyntax statement, MethodDeclarationSyntax method) =>
        !statement.Ancestors()
            .TakeWhile(node => node != method)
            .Any(node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or BaseMethodDeclarationSyntax);

    /// <summary>Whether control can reach the closing brace, which a non-async Task method must not allow.</summary>
    private static bool FallsOffTheEnd(BlockSyntax body) => body.Statements.Count == 0 ||
        body.Statements[^1] is not (ReturnStatementSyntax or ThrowStatementSyntax);

    /// <summary>An override or an interface implementation: the caller may await it through the contract.</summary>
    private static bool IsContractMember(IMethodSymbol symbol)
    {
        if (symbol.IsOverride || symbol.ExplicitInterfaceImplementations.Length > 0) return true;
        if (symbol.ContainingType is not { } type) return false;

        return type.AllInterfaces
            .SelectMany(@interface => @interface.GetMembers(symbol.Name))
            .OfType<IMethodSymbol>()
            .Any(candidate => SymbolEqualityComparer.Default.Equals(
                type.FindImplementationForInterfaceMember(candidate), symbol));
    }
}
