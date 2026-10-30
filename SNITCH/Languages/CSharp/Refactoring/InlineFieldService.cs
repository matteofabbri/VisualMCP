using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Inline a field at every use and remove it (Rider's "Inline field"). Accepted only when the field
/// has an initializer, is declared in one file and is never written outside its declaration: with
/// those conditions the edit is a substitution. The initializer is re-evaluated at each use
/// afterwards, which is what the note says.
/// </summary>
public interface IInlineFieldService
{
    Task<object> InlineField(string filePath, string fieldName, string? containingType, bool dryRun,
        CancellationToken cancellationToken);
}

public sealed class InlineFieldService : IInlineFieldService
{
    private readonly IRoslynWorkspace _workspace;
    public InlineFieldService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> InlineField(string filePath, string fieldName, string? containingType = null,
        bool dryRun = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(fieldName)) return new { error = "A field name is required." };

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

        var declarations = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(field => field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == fieldName))
            .ToList();

        if (declarations.Count == 0) return new { error = $"No field '{fieldName}' in {Path.GetFileName(path)}." };
        if (declarations.Count > 1) return new { error = $"Several fields named '{fieldName}' in the file; address one type at a time." };

        var declaration = declarations[0];
        if (declaration.Declaration.Variables.Count > 1)
            return new { error = "The declaration holds several variables; split it before inlining." };

        var variable = declaration.Declaration.Variables[0];
        if (variable.Initializer is null)
            return new { error = $"'{fieldName}' has no initializer: inlining it would change what each use evaluates." };

        var symbol = model.GetDeclaredSymbol(variable, cancellationToken);
        if (symbol is not IFieldSymbol fieldSymbol) return new { error = $"'{fieldName}' did not resolve to a field." };

        if (containingType is not null &&
            !string.Equals(fieldSymbol.ContainingType?.Name, containingType, StringComparison.OrdinalIgnoreCase))
            return new { error = $"'{fieldName}' is declared on '{fieldSymbol.ContainingType?.Name}', not '{containingType}'." };

        // ── Every use must be inside this file, and none may write the field ─────────────
        var references = await SymbolFinder.FindReferencesAsync(fieldSymbol, solution, cancellationToken);
        var identifierSpans = new List<TextSpan>();
        var writes = new List<string>();

        foreach (var reference in references)
        {
            foreach (var location in reference.Locations.Where(location => location.Location.IsInSource))
            {
                if (!string.Equals(location.Document.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase))
                    return new
                    {
                        error = $"'{fieldName}' is used in {Path.GetFileName(location.Document.FilePath)} as well: " +
                                "inlining a field across files would paste the initializer where it cannot see its context.",
                        file = location.Document.FilePath,
                    };

                var referenceRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
                var node = referenceRoot?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                if (node is null) continue;

                var written = node.Parent switch
                {
                    AssignmentExpressionSyntax assignment when assignment.Left == node => true,
                    PrefixUnaryExpressionSyntax prefix when prefix.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression => true,
                    PostfixUnaryExpressionSyntax => true,
                    ArgumentSyntax argument => argument.RefKindKeyword.Kind() is not SyntaxKind.None,
                    _ => false,
                };

                if (written)
                {
                    writes.Add($"{Path.GetFileName(location.Document.FilePath)}:{location.Location.GetLineSpan().StartLinePosition.Line + 1}");
                    continue;
                }

                if (node is IdentifierNameSyntax identifier) identifierSpans.Add(identifier.Span);
            }
        }

        if (writes.Count > 0)
            return new
            {
                error = $"'{fieldName}' is written outside its declaration ({string.Join(", ", writes)}): inlining would lose the assignment.",
            };

        // ── Build the text ───────────────────────────────────────────────────────────────
        var annotation = new SyntaxAnnotation("inline_field_declaration");
        var annotated = root.ReplaceNode(declaration, declaration.WithAdditionalAnnotations(annotation));

        var identifiers = annotated.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(identifier => identifierSpans.Any(span => span == identifier.Span))
            .ToList();

        if (identifiers.Count == 0)
            return new { error = $"'{fieldName}' has no read outside its declaration: nothing to inline (use safe_delete for that)." };

        var initializer = variable.Initializer.Value.WithoutTrivia();
        var replaced = annotated.ReplaceNodes(identifiers, (original, _) =>
            SyntaxFactory.ParenthesizedExpression(initializer).WithTriviaFrom(original));

        var target = replaced.GetAnnotatedNodes(annotation).OfType<FieldDeclarationSyntax>().Single();
        var newRoot = replaced.RemoveNode(target, SyntaxRemoveOptions.KeepNoTrivia)!;

        var newText = newRoot.ToFullString();
        var oldText = (await document.GetTextAsync(cancellationToken)).ToString();

        var projected = solution.WithDocumentText(document.Id, SourceText.From(newText));
        var gate = await RefactoringCompilerGate.CompareAsync(solution, projected, [document.Project.Id], cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, $"inlining '{fieldName}'");

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "inline_field",
            field = fieldSymbol.ToDisplayString(),
            uses = identifiers.Count,
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = gate.Before, after = gate.After },
            preview = RefactoringFileWriter.Diff(oldText, newText),
            note = dryRun
                ? "Preview only: pass dryRun=false to write the file."
                : "Field removed; the initializer is now evaluated at each use.",
        };
    }
}
