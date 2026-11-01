using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Inline parameter": removes a parameter and substitutes its value into the method body.
/// It is only accepted when the value is the same, compile-time-fixed expression at every call site
/// and still binds to the same symbol inside the method — otherwise inlining would silently change
/// what the body means, so the whole operation is refused instead.
/// </summary>
public interface IInlineParameterService
{
    Task<object> InlineParameter(
        string filePath,
        string methodName,
        string parameterName,
        string? containingType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class InlineParameterService : IInlineParameterService
{
    private readonly IRoslynWorkspace _workspace;

    public InlineParameterService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> InlineParameter(
        string filePath,
        string methodName,
        string parameterName,
        string? containingType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(methodName)) return new { error = "A method name is required." };
        if (string.IsNullOrWhiteSpace(parameterName)) return new { error = "A parameter name is required." };

        if (filePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
            return await Snitch.Languages.VB.Refactoring.VbRefactoringTail.InlineParameterAsync(
                _workspace, filePath, methodName, parameterName, containingType, dryRun, cancellationToken);

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var candidates = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == methodName)
            .Where(method => containingType is null ||
                             string.Equals(model.GetDeclaredSymbol(method, cancellationToken)?.ContainingType?.Name,
                                           containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0) return new { error = $"No method '{methodName}' in {Path.GetFileName(path)}." };
        if (candidates.Count > 1) return new { error = $"Several methods named '{methodName}'; pass containingType." };

        var declaration = candidates[0];
        if (model.GetDeclaredSymbol(declaration, cancellationToken) is not IMethodSymbol method)
            return new { error = $"'{methodName}' did not resolve to a method." };

        var index = -1;
        for (var i = 0; i < declaration.ParameterList.Parameters.Count; i++)
            if (declaration.ParameterList.Parameters[i].Identifier.ValueText == parameterName) { index = i; break; }

        if (index < 0)
            return new
            {
                error = $"'{methodName}' has no parameter '{parameterName}'.",
                parameters = declaration.ParameterList.Parameters.Select(parameter => parameter.Identifier.ValueText).ToList(),
            };

        var refusal = RefuseFor(method, method.Parameters[index]);
        if (refusal is not null) return new { error = refusal };

        var parameter = method.Parameters[index];

        foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
            if (SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(invocation, cancellationToken).Symbol, method))
                return new { error = $"'{methodName}' calls itself; the recursive call would keep the argument that is being removed." };

        // ── Every use must be a plain call, so the argument can be found at each site ───────
        var references = await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken);
        var callSites = new List<(Document Document, InvocationExpressionSyntax Invocation, ExpressionSyntax Argument)>();
        var unsupported = new List<string>();

        foreach (var reference in references)
        {
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                if (location.Document.Id == document.Id && declaration.Span.Contains(location.Location.SourceSpan)) continue;

                var referenceRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
                var node = referenceRoot?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                if (node is null) continue;

                var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (invocation is null || !invocation.Expression.DescendantNodesAndSelf().Contains(node))
                {
                    unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:" +
                                    $"{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1} (method group, nameof or delegate)");
                    continue;
                }

                if (invocation.ArgumentList.Arguments.Count <= index)
                {
                    unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:" +
                                    $"{invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1} (omits the argument)");
                    continue;
                }

                var argument = invocation.ArgumentList.Arguments[index];
                if (argument.NameColon is not null)
                {
                    unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:" +
                                    $"{invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1} (named argument)");
                    continue;
                }

                callSites.Add((location.Document, invocation, argument.Expression));
            }
        }

        if (unsupported.Count > 0)
            return new
            {
                error = $"{unsupported.Count} use(s) cannot be inlined (the argument is not where it would have to be); nothing was written.",
                uses = unsupported.Take(10).ToList(),
            };

        if (callSites.Count == 0) return new { error = $"No call sites of '{methodName}' were found; nothing to inline into." };

        // ── The value must be call-site independent and mean the same thing in the callee ──
        var callSiteModel = await callSites[0].Document.GetSemanticModelAsync(cancellationToken);
        if (callSiteModel is null) return new { error = "Could not read the semantic model of a call site." };

        var texts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (callDocument, invocation, argument) in callSites)
        {
            var argumentModel = await callDocument.GetSemanticModelAsync(cancellationToken);
            if (argumentModel is null) return new { error = $"Could not read the semantic model of {callDocument.FilePath}." };

            var dependence = CallSiteDependence(argument, argumentModel, cancellationToken);
            if (dependence is not null)
                return new
                {
                    error = $"The argument at {Path.GetFileName(callDocument.FilePath)}:" +
                            $"{invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1} {dependence}; " +
                            "inlining it would move the evaluation into the method, which changes what it means.",
                };

            var binding = BindingConsistency(argument, argumentModel, model, declaration, cancellationToken);
            if (binding is not null)
                return new
                {
                    error = $"At {Path.GetFileName(callDocument.FilePath)}:" +
                            $"{invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1} the argument {binding}; " +
                            "inside the method it would mean something else.",
                };

            texts.Add(Normalize(argument));
        }

        if (texts.Count > 1)
            return new
            {
                error = "The call sites pass different values, so removing the parameter would change their behaviour.",
                values = texts.OrderBy(text => text, StringComparer.Ordinal).ToList(),
            };

        var substitution = texts.Single();

        // ── Build every new text before writing anything ───────────────────────────────────
        var edits = new List<(Document Document, string NewText, string Preview)>();
        var perDocument = callSites
            .GroupBy(site => site.Document.Id)
            .ToDictionary(group => group.Key,
                group => group.Select(site => (site.Document, site.Invocation, site.Argument)).ToList());
        if (!perDocument.ContainsKey(document.Id)) perDocument[document.Id] = new();

        foreach (var (documentId, sites) in perDocument)
        {
            var target = documentId == document.Id ? document : sites[0].Document;
            var targetRoot = await target.GetSyntaxRootAsync(cancellationToken);
            if (targetRoot is null) continue;

            var replacements = new Dictionary<SyntaxNode, SyntaxNode>();

            foreach (var (_, invocation, _) in sites)
            {
                // The invocation nodes come from this document's tree.
                var current = targetRoot.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .FirstOrDefault(candidate => candidate.Span == invocation.Span);
                if (current is null) continue;

                replacements[current] = current.WithArgumentList(
                    current.ArgumentList.WithArguments(current.ArgumentList.Arguments.RemoveAt(index)));
            }

            if (documentId == document.Id)
            {
                var rewrittenBody = RewriteBody(declaration, model, parameter, substitution, cancellationToken);
                if (rewrittenBody is null) return new { error = $"Could not rewrite the body of '{methodName}'." };

                var withoutParameter = rewrittenBody.WithParameterList(
                    rewrittenBody.ParameterList.WithParameters(rewrittenBody.ParameterList.Parameters.RemoveAt(index)));

                replacements[declaration] = withoutParameter;
            }

            if (replacements.Count == 0) continue;

            var newRoot = targetRoot.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
            var oldText = (await target.GetTextAsync(cancellationToken)).ToString();
            var newText = newRoot.ToFullString();
            if (string.Equals(oldText, newText, StringComparison.Ordinal)) continue;

            edits.Add((target, newText, RefactoringFileWriter.Diff(oldText, newText)));
        }

        if (edits.Count == 0) return new { error = "Nothing to rewrite." };

        var projected = solution;
        foreach (var edit in edits) projected = projected.WithDocumentText(edit.Document.Id, SourceText.From(edit.NewText));

        var gate = await RefactoringCompilerGate.CompareAsync(
            solution, projected, edits.Select(edit => edit.Document.Project.Id), cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "inlining the parameter");

        if (!dryRun)
            foreach (var edit in edits)
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, edit.Document, edit.NewText, cancellationToken);

        return new
        {
            @refactoring = "inline_parameter",
            method = method.ToDisplayString(),
            parameter = parameterName,
            inlinedValue = substitution,
            callSites = callSites.Count,
            filesChanged = edits.Count,
            compilerErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            files = edits.Select(edit => new { file = edit.Document.FilePath, preview = edit.Preview }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    private static string? RefuseFor(IMethodSymbol method, IParameterSymbol parameter)
    {
        if (method.MethodKind != MethodKind.Ordinary) return $"'{method.Name}' is a {method.MethodKind}, not an ordinary method.";
        if (method.IsAbstract || method.IsVirtual || method.IsOverride)
            return $"'{method.Name}' is abstract/virtual/override: overrides and implementations would have to change too.";
        if (method.IsExtern) return $"'{method.Name}' is extern.";
        if (method.PartialDefinitionPart is not null || method.PartialImplementationPart is not null)
            return $"'{method.Name}' is partial; inlining one part would leave the other stale.";
        if (method.ExplicitInterfaceImplementations.Length > 0) return $"'{method.Name}' implements an interface explicitly.";
        if (method.ContainingType is { TypeKind: TypeKind.Interface }) return "An interface method cannot lose a parameter this way.";
        if (parameter.RefKind != RefKind.None) return $"'{parameter.Name}' is passed by reference (ref/out/in).";
        if (parameter.IsParams) return $"'{parameter.Name}' is a params array.";
        if (parameter.Type.IsRefLikeType) return $"'{parameter.Name}' is a ref struct; it cannot be moved into the body safely.";
        if (method.IsExtensionMethod) return "An extension method's `this` parameter is not inlined.";

        return null;
    }

    /// <summary>
    /// Null when the expression means the same at the call site and inside the method: same symbol
    /// when it binds to one, same type always. Speculative binding at the method body is what catches
    /// `Foo.Bar` resolving to a different `Foo` in the callee's namespace.
    /// </summary>
    private static string? BindingConsistency(
        ExpressionSyntax argument,
        SemanticModel callModel,
        SemanticModel calleeModel,
        MethodDeclarationSyntax declaration,
        CancellationToken cancellationToken)
    {
        var position = declaration.Body?.OpenBraceToken.SpanStart
                       ?? declaration.ExpressionBody?.SpanStart
                       ?? declaration.SpanStart;

        var parsed = ParseExpression(argument.WithoutTrivia().ToFullString());

        var callSymbol = callModel.GetSymbolInfo(argument, cancellationToken).Symbol;
        var calleeSymbol = calleeModel.GetSpeculativeSymbolInfo(position, parsed, SpeculativeBindingOption.BindAsExpression).Symbol;
        if (callSymbol is not null || calleeSymbol is not null)
        {
            if (!SymbolEqualityComparer.Default.Equals(callSymbol, calleeSymbol))
                return "resolves to a different symbol there";
        }

        var callType = callModel.GetTypeInfo(argument, cancellationToken).Type;
        var calleeType = calleeModel.GetSpeculativeTypeInfo(position, parsed, SpeculativeBindingOption.BindAsExpression).Type;
        if (!SymbolEqualityComparer.Default.Equals(callType, calleeType))
            return $"has type '{callType?.ToDisplayString()}' at the call site but '{calleeType?.ToDisplayString()}' inside the method";

        return null;
    }

    /// <summary>Null when the argument can be evaluated anywhere; otherwise why it cannot.</summary>
    private static string? CallSiteDependence(ExpressionSyntax argument, SemanticModel model, CancellationToken cancellationToken)
    {
        foreach (var node in argument.DescendantNodesAndSelf())
        {
            if (node is ThisExpressionSyntax or BaseExpressionSyntax)
                return "reads 'this'";

            if (node is not IdentifierNameSyntax identifier) continue;

            var symbol = model.GetSymbolInfo(identifier, cancellationToken).Symbol;
            switch (symbol)
            {
                case ILocalSymbol:
                case IParameterSymbol:
                    return $"reads the local/parameter '{identifier.Identifier.ValueText}'";
                case IFieldSymbol { IsStatic: false }:
                case IPropertySymbol { IsStatic: false }:
                case IEventSymbol { IsStatic: false }:
                    return $"reads the instance member '{identifier.Identifier.ValueText}'";
            }
        }

        return null;
    }

    private static MethodDeclarationSyntax? RewriteBody(
        MethodDeclarationSyntax declaration,
        SemanticModel model,
        IParameterSymbol parameter,
        string substitution,
        CancellationToken cancellationToken)
    {
        var replacements = declaration.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(identifier => SymbolEqualityComparer.Default.Equals(
                model.GetSymbolInfo(identifier, cancellationToken).Symbol, parameter))
            .ToList();

        var replacement = ParseExpression(substitution);
        var needsParentheses = replacement is not (LiteralExpressionSyntax or IdentifierNameSyntax or
            MemberAccessExpressionSyntax or InvocationExpressionSyntax or ElementAccessExpressionSyntax or
            ParenthesizedExpressionSyntax or DefaultExpressionSyntax or TypeOfExpressionSyntax or
            ThisExpressionSyntax or BaseExpressionSyntax);

        var node = needsParentheses ? ParenthesizedExpression(replacement) : replacement;

        if (declaration.Body is not null)
        {
            var inBody = replacements.Where(identifier => declaration.Body.Span.Contains(identifier.Span)).ToList();
            var body = declaration.Body.ReplaceNodes(inBody, (original, _) => node.WithTriviaFrom(original));
            return declaration.WithBody(body);
        }

        if (declaration.ExpressionBody is not null)
        {
            var expression = declaration.ExpressionBody.Expression.ReplaceNodes(
                replacements.Where(identifier => declaration.ExpressionBody.Expression.Span.Contains(identifier.Span)),
                (original, _) => node.WithTriviaFrom(original));

            return declaration.WithExpressionBody(declaration.ExpressionBody.WithExpression(expression));
        }

        return declaration;
    }

    private static string Normalize(ExpressionSyntax expression) =>
        expression.WithoutTrivia().NormalizeWhitespace().ToFullString();
}
