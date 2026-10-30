using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Move to namespace": the types declared in a file get a new namespace, and every file that
/// referenced them gets the <c>using</c> it now needs. The result is compiler-gated, so a move that
/// leaves a reference unresolvable is refused instead of written.
/// </summary>
public interface IMoveToNamespaceService
{
    Task<object> MoveToNamespace(
        string filePath,
        string targetNamespace,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class MoveToNamespaceService : IMoveToNamespaceService
{
    private readonly IRoslynWorkspace _workspace;

    public MoveToNamespaceService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> MoveToNamespace(
        string filePath,
        string targetNamespace,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(targetNamespace)) return new { error = "The target namespace is required." };
        if (!targetNamespace.Split('.').All(part => part.Length > 0 && SyntaxFacts.IsValidIdentifier(part)))
            return new { error = $"'{targetNamespace}' is not a valid namespace name." };

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

        var declarations = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().ToList();
        if (declarations.Count == 0)
            return new { error = $"{Path.GetFileName(path)} has no namespace declaration; add one (or use move_type) first." };
        if (declarations.Count > 1)
            return new
            {
                error = $"{Path.GetFileName(path)} declares {declarations.Count} namespaces; this tool moves a file's single namespace.",
                namespaces = declarations.Select(declaration => declaration.Name.ToString()).ToList(),
            };

        var declaration = declarations[0];
        var types = declaration.Members.OfType<BaseTypeDeclarationSyntax>().ToList();
        if (types.Count == 0)
            return new { error = $"{Path.GetFileName(path)} declares no types directly in its namespace." };

        var typeSymbols = types
            .Select(type => model.GetDeclaredSymbol(type, cancellationToken))
            .Where(symbol => symbol is not null)
            .Select(symbol => symbol!)
            .ToList();
        if (typeSymbols.Count == 0) return new { error = "The types in the file did not resolve." };

        var currentNamespace = declaration.Name.ToString();
        if (string.Equals(currentNamespace, targetNamespace, StringComparison.Ordinal))
            return new { error = $"The file already declares '{targetNamespace}'." };

        // When the moved types are the last ones in the old namespace, `using Old;` becomes an error
        // (CS0246) in every file that still has it, so those directives are removed with the move.
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        var namespaceRemains = false;
        if (compilation is not null)
        {
            var oldNamespace = Flatten(compilation.GlobalNamespace)
                .FirstOrDefault(candidate => candidate.ToDisplayString() == currentNamespace);

            namespaceRemains = oldNamespace is not null &&
                               (oldNamespace.GetNamespaceMembers().Any() ||
                                oldNamespace.GetTypeMembers().Any(type => type.Locations.Any(location =>
                                    location.SourceTree is not null && location.SourceTree != tree)));
        }

        var moved = declaration.WithName(ParseName(targetNamespace));
        var newDocumentText = root.ReplaceNode(declaration, moved).ToFullString();

        // ── Every other file that references a moved type needs the new using ─────────────
        var referencing = new Dictionary<DocumentId, Document>();
        foreach (var typeSymbol in typeSymbols)
        {
            foreach (var reference in await SymbolFinder.FindReferencesAsync(typeSymbol, solution, cancellationToken))
            {
                foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
                {
                    if (location.Document.Id == document.Id) continue;
                    referencing[location.Document.Id] = location.Document;
                }
            }
        }

        var edits = new List<(Document Document, string NewText, string Preview)>
        {
            (document, newDocumentText, RefactoringFileWriter.Diff(root.ToFullString(), newDocumentText)),
        };

        var usingsAdded = 0;
        var usingsRemoved = 0;
        foreach (var (_, target) in referencing)
        {
            var targetRoot = await target.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
            if (targetRoot is null) continue;

            var updated = targetRoot;

            if (!namespaceRemains)
            {
                var stale = updated.Usings
                    .Where(usingDirective => usingDirective.Alias is null &&
                                             string.Equals(usingDirective.Name?.ToString(), currentNamespace, StringComparison.Ordinal))
                    .ToList();

                foreach (var directive in stale)
                {
                    updated = updated.WithUsings(updated.Usings.Remove(directive));
                    usingsRemoved++;
                }
            }

            if (!AlreadyImports(updated, targetNamespace))
            {
                updated = updated.WithUsings(updated.Usings.Add(BuildUsing(updated, targetNamespace)));
                usingsAdded++;
            }

            var before = (await target.GetTextAsync(cancellationToken)).ToString();
            var after = updated.ToFullString();
            if (string.Equals(before, after, StringComparison.Ordinal)) continue;

            edits.Add((target, after, RefactoringFileWriter.Diff(before, after)));
        }

        var projected = solution;
        foreach (var edit in edits) projected = projected.WithDocumentText(edit.Document.Id, SourceText.From(edit.NewText));

        var gate = await RefactoringCompilerGate.CompareAsync(
            solution, projected, edits.Select(edit => edit.Document.Project.Id), cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "the move");

        if (!dryRun)
            foreach (var edit in edits)
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, edit.Document, edit.NewText, cancellationToken);

        return new
        {
            @refactoring = "move_to_namespace",
            from = currentNamespace,
            to = targetNamespace,
            types = typeSymbols.Select(symbol => symbol.ToDisplayString()).ToList(),
            filesChanged = edits.Count,
            usingsAdded,
            usingsRemoved,
            compilerErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            files = edits.Select(edit => new
            {
                file = edit.Document.FilePath,
                preview = edit.Preview,
            }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    private static bool AlreadyImports(CompilationUnitSyntax root, string targetNamespace) =>
        root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Any(usingDirective => usingDirective.Alias is null &&
                                   string.Equals(usingDirective.Name?.ToString(), targetNamespace, StringComparison.Ordinal));

    private static UsingDirectiveSyntax BuildUsing(CompilationUnitSyntax root, string targetNamespace)
    {
        var directive = UsingDirective(ParseName(targetNamespace))
            .WithUsingKeyword(Token(SyntaxKind.UsingKeyword).WithTrailingTrivia(Space));

        if (root.Usings.Count > 0)
        {
            var trailing = root.Usings.Last().GetTrailingTrivia();
            return directive.WithTrailingTrivia(trailing.Count > 0
                ? trailing
                : TriviaList(ElasticCarriageReturnLineFeed));
        }

        // No using at all: keep a blank line between the new using and the namespace declaration.
        return directive.WithTrailingTrivia(TriviaList(ElasticCarriageReturnLineFeed, ElasticCarriageReturnLineFeed));
    }

    private static IEnumerable<INamespaceSymbol> Flatten(INamespaceSymbol root)
    {
        yield return root;
        foreach (var child in root.GetNamespaceMembers())
            foreach (var descendant in Flatten(child))
                yield return descendant;
    }
}
