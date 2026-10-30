using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Turn an extension method back into a static one (the inverse of <c>static_to_extension</c>): the
/// <c>this</c> modifier goes away and every <c>x.M(a)</c> becomes <c>Type.M(x, a)</c>, across the
/// solution. A use that is not a plain call (a method group) is refused, because there is no
/// receiver to move into the argument list.
/// </summary>
public interface IExtensionToStaticService
{
    Task<object> Convert(string filePath, string methodName, string? containingType, bool dryRun,
        CancellationToken cancellationToken);
}

public sealed class ExtensionToStaticService : IExtensionToStaticService
{
    private readonly IRoslynWorkspace _workspace;
    public ExtensionToStaticService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> Convert(string filePath, string methodName, string? containingType = null,
        bool dryRun = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(methodName)) return new { error = "A method name is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax of {Path.GetFileName(path)}." };

        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == methodName)
            .Where(method => method.Modifiers.Any(SyntaxKind.StaticKeyword))
            .Where(method => method.ParameterList.Parameters.Count > 0 &&
                             method.ParameterList.Parameters[0].Modifiers.Any(SyntaxKind.ThisKeyword))
            .Where(method => containingType is null ||
                             string.Equals(model.GetDeclaredSymbol(method, cancellationToken)?.ContainingType?.Name,
                                 containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (methods.Count == 0)
            return new { error = $"No extension method '{methodName}' in {Path.GetFileName(path)} (it needs to be static with a 'this' first parameter)." };
        if (methods.Count > 1) return new { error = $"Several extension methods named '{methodName}'; address one overload at a time." };

        var declaration = methods[0];
        if (model.GetDeclaredSymbol(declaration, cancellationToken) is not IMethodSymbol symbol)
            return new { error = $"'{methodName}' did not resolve." };

        var owner = symbol.ContainingType?.Name;
        if (owner is null) return new { error = "The method has no containing type." };

        // ── Call sites: x.M(a) → Type.M(x, a) ────────────────────────────────────────────
        var edits = new Dictionary<DocumentId, List<(TextSpan Span, string Replacement)>>();
        var receiverless = new List<string>();

        foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken))
        {
            foreach (var location in reference.Locations.Where(location => location.Location.IsInSource))
            {
                var siteRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
                if (siteRoot is null) continue;

                var node = siteRoot.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();

                if (invocation?.Expression is not MemberAccessExpressionSyntax access)
                {
                    var line = location.Location.GetLineSpan().StartLinePosition.Line + 1;
                    receiverless.Add($"{Path.GetFileName(location.Document.FilePath)}:{line}");
                    continue;
                }

                var typeArguments = access.Name is GenericNameSyntax generic ? generic.TypeArgumentList.ToString() : string.Empty;
                var receiver = access.Expression.ToString();
                var arguments = invocation.ArgumentList.Arguments.Select(argument => argument.ToString()).ToList();
                var list = arguments.Count == 0 ? receiver : receiver + ", " + string.Join(", ", arguments);

                var replacement = $"{owner}.{methodName}{typeArguments}({list})";

                if (!edits.TryGetValue(location.Document.Id, out var list2))
                    edits[location.Document.Id] = list2 = new List<(TextSpan, string)>();
                list2.Add((invocation.Span, replacement));
            }
        }

        if (receiverless.Count > 0)
            return new
            {
                error = $"{receiverless.Count} use(s) are not a plain call (method group or unqualified call): " +
                        "there is no receiver to turn into the first argument.",
                uses = receiverless.Take(10).ToList(),
            };

        // ── Declaration: drop 'this' ─────────────────────────────────────────────────────
        var first = declaration.ParameterList.Parameters[0];
        var updatedFirst = first.WithModifiers(
                SyntaxFactory.TokenList(first.Modifiers.Where(token => !token.IsKind(SyntaxKind.ThisKeyword))))
            .WithLeadingTrivia(first.GetLeadingTrivia());
        var newDeclaration = declaration.WithParameterList(
            declaration.ParameterList.WithParameters(
                declaration.ParameterList.Parameters.Replace(first, updatedFirst)));

        var declarationText = declaration.SyntaxTree.GetText().ToString();
        var editedDeclaration = declarationText.Remove(declaration.SpanStart, declaration.Span.Length)
            .Insert(declaration.SpanStart, newDeclaration.ToFullString());

        // ── Apply per document, from the end, then write and gate ────────────────────────
        var writes = new Dictionary<DocumentId, string>
        {
            [document.Id] = editedDeclaration,
        };

        foreach (var (documentId, replacements) in edits)
        {
            if (documentId == document.Id) continue;

            var siteDocument = solution.GetDocument(documentId);
            var siteText = siteDocument is null ? null : await siteDocument.GetTextAsync(cancellationToken);
            if (siteText is null) continue;

            writes[documentId] = ApplyReplacements(siteText.ToString(), replacements);
        }

        // The declaration's own file can also hold call sites: apply them there too.
        if (edits.TryGetValue(document.Id, out var inFile))
            writes[document.Id] = ApplyReplacements(editedDeclaration, inFile);

        var projected = solution;
        foreach (var (documentId, text) in writes)
            projected = projected.WithDocumentText(documentId, SourceText.From(text));

        var projectIds = writes.Keys
            .Select(id => solution.GetDocument(id)?.Project.Id)
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct()
            .ToList();

        var gate = await RefactoringCompilerGate.CompareAsync(solution, projected, projectIds, cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, $"converting '{methodName}' to a static method");

        if (!dryRun)
        {
            foreach (var (documentId, text) in writes)
            {
                var target = solution.GetDocument(documentId);
                if (target is not null) await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, target, text, cancellationToken);
            }
        }

        var oldDeclaration = await document.GetTextAsync(cancellationToken);

        return new
        {
            @refactoring = "extension_to_static",
            method = symbol.ToDisplayString(),
            owner,
            callSites = edits.Values.Sum(list => list.Count),
            filesModified = writes.Count,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = gate.Before, after = gate.After },
            preview = RefactoringFileWriter.Diff(oldDeclaration.ToString(), editedDeclaration),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    private static string ApplyReplacements(string text, List<(TextSpan Span, string Replacement)> replacements)
    {
        foreach (var (span, replacement) in replacements.OrderByDescending(entry => entry.Span.Start))
            text = text.Remove(span.Start, span.Length).Insert(span.Start, replacement);

        return text;
    }
}
