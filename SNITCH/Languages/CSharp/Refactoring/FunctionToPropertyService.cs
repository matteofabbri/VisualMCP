using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>Rider's "Method to property" (Function2Property): a parameterless getter becomes a property, call sites included.</summary>
public interface IFunctionToPropertyService
{
    Task<object> FunctionToProperty(
        string filePath,
        string methodName,
        string? containingType = null,
        string? propertyName = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class FunctionToPropertyService : IFunctionToPropertyService
{
    private readonly IRoslynWorkspace _workspace;

    public FunctionToPropertyService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> FunctionToProperty(
        string filePath,
        string methodName,
        string? containingType = null,
        string? propertyName = null,
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

        // VB has its own MethodBlock, its own two call spellings (with and without parentheses) and its
        // own ReadOnly Property vocabulary, so it is a separate projection.
        if (document.Project.Language == LanguageNames.VisualBasic)
            return await Snitch.Languages.VB.Refactoring.VbFunctionToProperty.RunAsync(
                _workspace, solution, document, methodName, containingType, propertyName, dryRun, cancellationToken);

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
            return new { error = $"Several methods named '{methodName}' (overloads); converting one would collide with the property name." };

        var declaration = candidates[0];
        if (model.GetDeclaredSymbol(declaration, cancellationToken) is not IMethodSymbol method)
            return new { error = $"'{methodName}' did not resolve to a method." };

        var refusal = RefuseFor(method);
        if (refusal is not null) return new { error = refusal };

        var owner = method.ContainingType;
        if (owner is null) return new { error = $"'{methodName}' has no containing type." };

        var targetName = propertyName ?? DefaultPropertyName(methodName);
        if (!SyntaxFacts.IsValidIdentifier(targetName))
            return new { error = $"'{targetName}' is not a valid identifier; pass propertyName explicitly." };
        if (owner.GetMembers(targetName).Length > 0)
            return new { error = $"'{owner.ToDisplayString()}' already has a member named '{targetName}'; pass another propertyName." };

        // ── Every use must be a plain invocation: a method group or nameof would change meaning ──
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
                if (node is null) continue;

                if (node.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault() is not null)
                {
                    affected[location.Document.Id] = location.Document;
                    callSites++;
                    continue;
                }

                unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1} " +
                                $"(not an invocation — method group or nameof)");
            }
        }

        if (unsupported.Count > 0)
            return new
            {
                error = $"{unsupported.Count} use(s) are not plain calls, so converting would change their meaning; nothing was written.",
                uses = unsupported.Take(10).ToList(),
            };

        // ── Build every new text first ────────────────────────────────────────────────────
        var edits = new List<(Document Document, string NewText, int Rewrites, string Preview)>();

        foreach (var (documentId, target) in affected)
        {
            var targetRoot = await target.GetSyntaxRootAsync(cancellationToken);
            if (targetRoot is null) continue;

            var invocations = targetRoot.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(invocation => invocation.ArgumentList.Arguments.Count == 0)
                .Where(invocation => invocation.Expression is IdentifierNameSyntax name
                        ? name.Identifier.ValueText == methodName
                        : invocation.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == methodName)
                .ToList();

            var newRoot = targetRoot.ReplaceNodes(invocations, (original, _) => RewriteInvocation(original, targetName));

            if (documentId == document.Id)
            {
                var localDeclaration = newRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(candidate => candidate.Span == declaration.Span);
                if (localDeclaration is null) continue;

                newRoot = newRoot.ReplaceNode(localDeclaration, BuildProperty(localDeclaration, targetName));
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
            @refactoring = "function_to_property",
            method = method.ToDisplayString(),
            property = targetName,
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            callSitesRewritten = callSites,
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
        if (method.MethodKind != MethodKind.Ordinary) return $"'{method.Name}' is a {method.MethodKind}, not an ordinary method.";
        if (method.Parameters.Length > 0) return $"'{method.Name}' takes {method.Parameters.Length} parameter(s); a property cannot.";
        if (method.ReturnsVoid) return $"'{method.Name}' returns void; a property needs a value.";
        if (method.Arity > 0) return $"'{method.Name}' is generic; a property cannot be.";
        if (method.IsAsync) return $"'{method.Name}' is async; a property cannot be.";
        if (method.ReturnsByRef || method.ReturnsByRefReadonly) return $"'{method.Name}' returns by reference; not supported.";
        if (method.IsAbstract || method.IsVirtual || method.IsOverride) return $"'{method.Name}' is abstract/virtual/override: overrides would break.";
        if (method.IsExtern) return $"'{method.Name}' is extern.";
        if (method.ExplicitInterfaceImplementations.Length > 0) return $"'{method.Name}' implements an interface explicitly.";
        if (method.ContainingType?.TypeKind == TypeKind.Interface) return "Interface members cannot become properties this way.";

        if (method.ContainingType is { } owner && owner.AllInterfaces.Length > 0)
        {
            foreach (var interfaceMember in owner.AllInterfaces.SelectMany(i => i.GetMembers(method.Name)))
            {
                var implementation = owner.FindImplementationForInterfaceMember(interfaceMember);
                if (SymbolEqualityComparer.Default.Equals(implementation, method))
                    return $"'{method.Name}' implements '{interfaceMember.ContainingType.Name}.{interfaceMember.Name}'.";
            }
        }

        return null;
    }

    /// <summary>`GetCount` → `Count`; anything else keeps its name (capitalised).</summary>
    private static string DefaultPropertyName(string methodName)
    {
        var name = methodName.Length > 3 && methodName.StartsWith("Get", StringComparison.Ordinal)
            ? methodName[3..]
            : methodName;

        return name.Length == 0 ? methodName : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static ExpressionSyntax RewriteInvocation(InvocationExpressionSyntax invocation, string propertyName)
    {
        var rewritten = invocation.Expression switch
        {
            MemberAccessExpressionSyntax access =>
                access.WithName(IdentifierName(propertyName).WithTriviaFrom(access.Name)),
            IdentifierNameSyntax identifier =>
                identifier.WithIdentifier(Identifier(propertyName).WithTriviaFrom(identifier.Identifier)),
            _ => invocation.Expression,
        };

        // The whitespace after `()` is trailing trivia of the argument list, so the replacement
        // must carry it or `M() + x` becomes `M+ x`.
        return rewritten
            .WithLeadingTrivia(invocation.GetLeadingTrivia())
            .WithTrailingTrivia(invocation.GetTrailingTrivia());
    }

    private static PropertyDeclarationSyntax BuildProperty(MethodDeclarationSyntax method, string propertyName)
    {
        var getter = AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
            .WithKeyword(Token(SyntaxKind.GetKeyword).WithLeadingTrivia(Space).WithTrailingTrivia(Space));

        // Reusing the original body / arrow clause keeps its statements, comments and spacing.
        getter = method.ExpressionBody is not null
            ? getter.WithExpressionBody(method.ExpressionBody)
                    .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
            : getter.WithBody(method.Body!);

        var accessors = AccessorList(
            Token(SyntaxKind.OpenBraceToken).WithLeadingTrivia(Space),
            SingletonList(getter),
            Token(SyntaxKind.CloseBraceToken).WithLeadingTrivia(Space));

        return PropertyDeclaration(method.ReturnType, Identifier(propertyName).WithTriviaFrom(method.Identifier))
            .WithAttributeLists(method.AttributeLists)
            .WithModifiers(method.Modifiers)
            .WithAccessorList(accessors)
            .WithLeadingTrivia(method.GetLeadingTrivia())
            .WithTrailingTrivia(method.GetTrailingTrivia());
    }
}
