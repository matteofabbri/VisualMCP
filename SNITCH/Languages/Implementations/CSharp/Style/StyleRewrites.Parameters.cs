using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// IDE0060: a parameter nobody reads is removed, together with the argument every call site passes.
/// This is the first rule whose suggestion spans several files: the declaration is one span, each call
/// site another, and the engine applies them as one suggestion — gated on the compiler, written or
/// refused together.
/// <para>
/// Deliberately narrow: only a private method (so no external caller can exist), one parameter per
/// pass, no <c>ref</c>/<c>out</c>/<c>params</c>, no attributes, and every reference must be a plain
/// invocation that passes the arguments positionally. A method group, a <c>nameof</c>, or a named
/// argument makes the mapping unsafe, and the rule then does nothing.
/// </para>
/// </summary>
public sealed class UnusedParameterRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0060", "Remove unused parameter",
        "void M(int unused) → void M(), and the argument at every call site");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (context.Solution is null) return null;

        // The call sites are read from the solution, so the tree must still be the solution's.
        if (!context.IsPristine) return null;

        if (node is not MethodDeclarationSyntax method) return null;
        if (method.Body is null && method.ExpressionBody is null) return null;
        if (method.AttributeLists.Count > 0) return null;

        if (context.Model.GetDeclaredSymbol(method, context.CancellationToken) is not IMethodSymbol symbol)
            return null;

        if (symbol.DeclaredAccessibility != Accessibility.Private) return null;
        if (symbol.IsOverride || symbol.IsVirtual || symbol.IsAbstract || symbol.IsExtern) return null;
        if (symbol.PartialDefinitionPart is not null || symbol.PartialImplementationPart is not null) return null;
        if (symbol.ExplicitInterfaceImplementations.Length > 0) return null;
        if (method.ParameterList.Parameters.Count != symbol.Parameters.Length) return null;

        var index = -1;
        for (var position = 0; position < symbol.Parameters.Length; position++)
        {
            var parameter = symbol.Parameters[position];
            if (parameter.RefKind != RefKind.None || parameter.IsParams) return null;
            if (method.ParameterList.Parameters[position].AttributeLists.Count > 0) return null;
            if (IsUsed(parameter, method, context)) continue;

            // One per pass: two removals in one suggestion would make the spans overlap.
            if (index >= 0) return null;
            index = position;
        }

        if (index < 0) return null;

        var declarationPath = method.SyntaxTree.FilePath;
        if (string.IsNullOrEmpty(declarationPath)) return null;

        var callSites = CallSites(symbol, context);
        if (callSites is null) return null;

        var edits = new List<SpanEdit>();
        AddSpan(method.ParameterList.Parameters, index, declarationPath, edits);

        foreach (var call in callSites)
        {
            var path = call.SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(path)) return null;

            AddSpan(call.ArgumentList.Arguments, index, path, edits);
        }

        var name = symbol.Parameters[index].Name;
        return Shape(Rule, method, edits, name, string.Empty);
    }

    /// <summary>
    /// The span of one element of a comma-separated list, comma included: the element itself when it is
    /// the only one, otherwise the separator and the element that follows it.
    /// </summary>
    private static void AddSpan<TNode>(SeparatedSyntaxList<TNode> list, int index, string path, List<SpanEdit> edits)
        where TNode : SyntaxNode
    {
        if (list.Count == 1)
        {
            edits.Add(new SpanEdit(path, list[0].Span.Start, list[0].Span.Length));
            return;
        }

        if (index == 0)
        {
            // `(a, b)` removing a: from a's start to b's start.
            edits.Add(new SpanEdit(path, list[0].Span.Start, list[1].Span.Start - list[0].Span.Start));
            return;
        }

        // `(a, b)` removing b: from the end of a to the end of b.
        edits.Add(new SpanEdit(path, list[index - 1].Span.End, list[index].Span.End - list[index - 1].Span.End));
    }

    private static bool IsUsed(IParameterSymbol parameter, MethodDeclarationSyntax method, StyleContext context)
    {
        var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (body is null) return true;

        return body.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Any(identifier => SameSymbol(context.Model.GetSymbolInfo(identifier, context.CancellationToken).Symbol, parameter));
    }

    /// <summary>Every call, or null when one reference cannot be rewritten as an argument list.</summary>
    private static List<InvocationExpressionSyntax>? CallSites(IMethodSymbol symbol, StyleContext context)
    {
        var references = SymbolFinder.FindReferencesAsync(symbol, context.Solution!, context.CancellationToken)
            .GetAwaiter().GetResult();

        var calls = new List<InvocationExpressionSyntax>();

        foreach (var location in references.SelectMany(reference => reference.Locations))
        {
            if (location.IsImplicit) continue;

            var root = location.Document.GetSyntaxRootAsync(context.CancellationToken).GetAwaiter().GetResult();
            if (root is null) return null;

            var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
            var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            if (invocation is null) return null;          // a method group or a nameof: no argument list

            if (invocation.ArgumentList.Arguments.Count != symbol.Parameters.Length) return null;
            if (invocation.ArgumentList.Arguments.Any(argument => argument.NameColon is not null)) return null;

            calls.Add(invocation);
        }

        return calls;
    }
}
