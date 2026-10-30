using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Convert static method to extension" (Static2Extension): the first parameter becomes the
/// receiver, and every call `Type.M(receiver, args)` becomes `receiver.M(args)`.
/// </summary>
public interface IStaticToExtensionService
{
    Task<object> StaticToExtension(
        string filePath,
        string methodName,
        string? containingType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class StaticToExtensionService : IStaticToExtensionService
{
    private readonly IRoslynWorkspace _workspace;

    public StaticToExtensionService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> StaticToExtension(
        string filePath,
        string methodName,
        string? containingType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(methodName)) return new { error = "A method name is required." };

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

        var diskText = await File.ReadAllTextAsync(path, cancellationToken);
        var modelledText = (await document.GetTextAsync(cancellationToken)).ToString();
        if (!string.Equals(diskText, modelledText, StringComparison.Ordinal))
            return new { error = $"{Path.GetFileName(path)} changed on disk after the solution was loaded; the in-memory model is stale. Call load_solution, then retry." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var candidates = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == methodName)
            .Where(method => containingType is null ||
                             string.Equals(model.GetDeclaredSymbol(method, cancellationToken)?.ContainingType?.Name,
                                           containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            return new { error = $"No method '{methodName}' in {Path.GetFileName(path)}." };
        if (candidates.Count > 1)
            return new { error = $"Several methods named '{methodName}' (overloads); address one declaration at a time." };

        var declaration = candidates[0];
        if (model.GetDeclaredSymbol(declaration, cancellationToken) is not IMethodSymbol method)
            return new { error = $"'{methodName}' did not resolve to a method." };

        var refusal = RefuseFor(method);
        if (refusal is not null) return new { error = refusal };

        var containing = method.ContainingType;
        var extensionNamespace = containing?.ContainingNamespace;
        if (containing is null || extensionNamespace is null)
            return new { error = $"'{methodName}' has no containing type or namespace." };

        // ── Every call site must be rewritable: no named arguments, at least the receiver ──
        var references = await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken);
        var affected = new Dictionary<DocumentId, Document> { [document.Id] = document };
        var unsupported = new List<string>();
        var callSites = 0;

        foreach (var reference in references)
        {
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                if (location.Document.Id == document.Id && declaration.Span.Contains(location.Location.SourceSpan)) continue;

                var referenceRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
                var node = referenceRoot?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                var invocation = node?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();

                if (invocation is null)
                {
                    unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:{Line(node)} (not a call — method group or nameof)");
                    continue;
                }

                if (invocation.ArgumentList.Arguments.Count < method.Parameters.Length)
                {
                    unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:{Line(invocation)} (fewer arguments than parameters)");
                    continue;
                }

                if (invocation.ArgumentList.Arguments.Any(argument => argument.NameColon is not null))
                {
                    unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:{Line(invocation)} (named argument)");
                    continue;
                }

                affected[location.Document.Id] = location.Document;
                callSites++;
            }
        }

        if (unsupported.Count > 0)
            return new
            {
                error = $"{unsupported.Count} call site(s) cannot be rewritten; nothing was written.",
                callSites = unsupported.Take(10).ToList(),
            };

        // ── Build every new text first ────────────────────────────────────────────────────
        var edits = new List<(Document Document, string NewText, int Rewrites, string Preview)>();
        var usingAdded = 0;

        foreach (var (documentId, target) in affected)
        {
            var targetRoot = await target.GetSyntaxRootAsync(cancellationToken);
            var targetModel = await target.GetSemanticModelAsync(cancellationToken);
            if (targetRoot is null || targetModel is null) continue;

            var isDeclarationDocument = documentId == document.Id;

            var invocations = targetRoot.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(invocation => SymbolEqualityComparer.Default.Equals(
                    targetModel.GetSymbolInfo(invocation, cancellationToken).Symbol, method))
                .ToList();

            var newRoot = targetRoot.ReplaceNodes(invocations, (original, _) => RotateReceiver(original, methodName));

            if (isDeclarationDocument)
            {
                var localDeclaration = newRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(candidate => candidate.Span == declaration.Span);
                if (localDeclaration is null) continue;

                newRoot = newRoot.ReplaceNode(localDeclaration, WithThisOnFirstParameter(localDeclaration));
            }

            if (!isDeclarationDocument && !NamespaceVisible(targetRoot, extensionNamespace))
            {
                newRoot = AddUsing(newRoot, extensionNamespace);
                usingAdded++;
            }

            var oldText = (await target.GetTextAsync(cancellationToken)).ToString();
            var newText = newRoot.ToFullString();
            if (string.Equals(oldText, newText, StringComparison.Ordinal)) continue;

            edits.Add((target, newText, invocations.Count, RefactoringFileWriter.Diff(oldText, newText)));
        }

        if (edits.Count == 0) return new { error = "Nothing to rewrite." };

        if (!dryRun)
        {
            foreach (var edit in edits)
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, edit.Document, edit.NewText, cancellationToken);
        }

        return new
        {
            @refactoring = "static_to_extension",
            method = method.ToDisplayString(),
            receiverType = method.Parameters[0].Type.ToDisplayString(),
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            callSitesRewritten = callSites,
            usingsAdded = usingAdded,
            filesChanged = edits.Count,
            files = edits.Select(edit => new
            {
                file = edit.Document.FilePath,
                rewrites = edit.Rewrites,
                preview = edit.Preview,
            }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    private static string? RefuseFor(IMethodSymbol method)
    {
        if (!method.IsStatic) return $"'{method.Name}' is an instance method; make_static runs first, then this.";
        if (method.IsExtensionMethod) return $"'{method.Name}' is already an extension method.";
        if (method.MethodKind != MethodKind.Ordinary) return $"'{method.Name}' is a {method.MethodKind}, not an ordinary method.";
        if (method.Parameters.Length == 0) return $"'{method.Name}' takes no parameters: there is no receiver to promote.";
        if (method.ContainingType is not { IsStatic: true }) return $"'{method.Name}' must live in a static class for extensions.";
        if (method.ContainingType.Arity > 0) return "A generic static class cannot hold extension methods.";
        if (method.ContainingType.TypeKind != TypeKind.Class) return "Extensions must live in a non-generic static class.";
        if (method.ContainingType.ContainingType is not null) return "A nested class cannot hold extension methods.";

        var first = method.Parameters[0];
        if (first.RefKind != RefKind.None) return $"The first parameter is passed by {first.RefKind}; a 'this' receiver cannot be.";
        if (first.IsParams) return "A 'params' parameter cannot become the receiver.";
        if (first.Type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer) return "A pointer receiver is not allowed.";

        if (method.ExplicitInterfaceImplementations.Length > 0) return $"'{method.Name}' implements an interface explicitly.";

        return null;
    }

    /// <summary>`Type.M(receiver, args)` and bare `M(receiver, args)` both become `receiver.M(args)`.</summary>
    private static InvocationExpressionSyntax RotateReceiver(InvocationExpressionSyntax invocation, string methodName)
    {
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count == 0) return invocation;

        var receiver = arguments[0].Expression;
        var remaining = arguments.RemoveAt(0);

        var expression = invocation.Expression switch
        {
            MemberAccessExpressionSyntax access => access.WithExpression(AsReceiver(receiver)),
            _ => MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, AsReceiver(receiver), IdentifierName(methodName)),
        };

        var rewritten = invocation
            .WithExpression(expression)
            .WithArgumentList(invocation.ArgumentList.WithArguments(remaining));

        return rewritten.WithLeadingTrivia(invocation.GetLeadingTrivia())
                        .WithTrailingTrivia(invocation.GetTrailingTrivia());
    }

    /// <summary>A lambda or assignment as receiver needs parentheses: `(x => x).M()`.</summary>
    private static ExpressionSyntax AsReceiver(ExpressionSyntax receiver)
    {
        var needsParentheses = receiver is LambdaExpressionSyntax
                                          or AnonymousMethodExpressionSyntax
                                          or AssignmentExpressionSyntax
                                          or ConditionalExpressionSyntax;

        return needsParentheses ? ParenthesizedExpression(receiver.WithoutTrivia()).WithTriviaFrom(receiver) : receiver;
    }

    private static MethodDeclarationSyntax WithThisOnFirstParameter(MethodDeclarationSyntax method)
    {
        var parameters = method.ParameterList.Parameters;
        if (parameters.Count == 0) return method;

        var first = parameters[0].WithModifiers(
            parameters[0].Modifiers.Add(Token(SyntaxKind.ThisKeyword).WithTrailingTrivia(Space)));

        return method.WithParameterList(method.ParameterList.WithParameters(parameters.Replace(parameters[0], first)));
    }

    private static bool NamespaceVisible(SyntaxNode root, INamespaceSymbol extensionNamespace)
    {
        var namespaceText = extensionNamespace.ToDisplayString();

        // file-scoped or block namespace already inside it? (a child namespace is NOT visible)
        foreach (var declaration in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
        {
            var enclosing = declaration.Name.ToString();
            if (string.Equals(enclosing, namespaceText, StringComparison.Ordinal) ||
                enclosing.StartsWith(namespaceText + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (root is CompilationUnitSyntax unit)
        {
            return unit.Usings.Any(usingDirective => usingDirective.Alias is null &&
                string.Equals(usingDirective.Name?.ToString(), namespaceText, StringComparison.Ordinal));
        }

        return false;
    }

    private static CompilationUnitSyntax AddUsing(SyntaxNode root, INamespaceSymbol extensionNamespace)
    {
        if (root is not CompilationUnitSyntax unit) return (CompilationUnitSyntax)root;

        var directive = UsingDirective(ParseName(extensionNamespace.ToDisplayString()))
            .WithUsingKeyword(Token(SyntaxKind.UsingKeyword).WithTrailingTrivia(Space))
            .WithTrailingTrivia(EndOfLine(Environment.NewLine));

        var usings = unit.Usings;
        if (usings.Count > 0)
            return unit.WithUsings(usings.Insert(usings.Count, directive));

        // No using yet: place it before the first declaration so it stays above the namespace.
        return unit.WithUsings(SingletonList(directive));
    }

    private static int Line(SyntaxNode? node) =>
        node is null ? 0 : node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
