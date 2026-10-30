using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Copy type": duplicate a type under a new name, renaming its constructors so the copy
/// compiles. References inside the copy keep pointing at the original. Gated on the compiler.
/// </summary>
public interface ICopyTypeService
{
    Task<object> CopyType(
        string filePath,
        string typeName,
        string newName,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class CopyTypeService : ICopyTypeService
{
    private readonly IRoslynWorkspace _workspace;

    public CopyTypeService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> CopyType(
        string filePath,
        string typeName,
        string newName,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(newName))
            return new { error = "The current name and the new name are both required." };
        if (!SyntaxFacts.IsValidIdentifier(newName)) return new { error = $"'{newName}' is not a valid identifier." };
        if (string.Equals(typeName, newName, StringComparison.Ordinal)) return new { error = "The names are identical." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var declaration = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == typeName);
        if (declaration is null) return new { error = $"No type '{typeName}' in {Path.GetFileName(path)}." };

        if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol symbol)
            return new { error = $"'{typeName}' did not resolve to a type." };

        var scope = (INamespaceOrTypeSymbol?)symbol.ContainingType ?? symbol.ContainingNamespace;
        if (scope is not null && scope.GetMembers(newName).Length > 0)
            return new { error = $"'{newName}' already exists in the same scope." };

        var copy = (TypeDeclarationSyntax)declaration
            .WithIdentifier(Identifier(newName).WithTriviaFrom(declaration.Identifier));

        // Constructors and the destructor must carry the new name.
        copy = copy.ReplaceNodes(
            copy.Members.Where(member => member is ConstructorDeclarationSyntax or DestructorDeclarationSyntax),
            (original, _) => original switch
            {
                ConstructorDeclarationSyntax constructor => constructor.WithIdentifier(
                    Identifier(newName).WithTriviaFrom(constructor.Identifier)),
                DestructorDeclarationSyntax destructor => destructor.WithIdentifier(
                    Identifier(newName).WithTriviaFrom(destructor.Identifier)),
                _ => original,
            });

        copy = copy
            .WithLeadingTrivia(new SyntaxTriviaList(ElasticCarriageReturnLineFeed, ElasticCarriageReturnLineFeed)
                .AddRange(declaration.GetLeadingTrivia()))
            .WithTrailingTrivia(ElasticCarriageReturnLineFeed);

        var newRoot = InsertAfter(root, declaration, copy);

        // ── Gate: the project must still compile ──────────────────────────────────────────
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        if (compilation is null || tree is null) return new { error = "The project produced no compilation." };

        var before = compilation.GetDiagnostics(cancellationToken)
            .Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var changedTree = CSharpSyntaxTree.Create((CSharpSyntaxNode)newRoot, (CSharpParseOptions)tree.Options,
            document.FilePath, encoding: null);
        var afterDiagnostics = compilation.ReplaceSyntaxTree(tree, changedTree)
            .GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();

        var added = afterDiagnostics.Count - before;
        if (added > 0)
            return new
            {
                error = $"Refused: copying the type would add {added} compiler error(s) ({before} → {afterDiagnostics.Count}).",
                errors = afterDiagnostics.Select(diagnostic => $"{diagnostic.Id} {diagnostic.GetMessage()}")
                    .Distinct().Take(8).ToList(),
            };

        var oldText = (await document.GetTextAsync(cancellationToken)).ToString();
        var newText = newRoot.ToFullString();

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "copy_type",
            from = symbol.ToDisplayString(),
            to = newName,
            file = document.FilePath,
            compilerErrors = new { before, after = afterDiagnostics.Count },
            dryRun,
            applied = !dryRun,
            preview = RefactoringFileWriter.Diff(oldText, newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    /// <summary>Inserts the copy right after the original, inside whatever container holds it.</summary>
    private static CompilationUnitSyntax InsertAfter(
        CompilationUnitSyntax root, TypeDeclarationSyntax declaration, MemberDeclarationSyntax insert)
    {
        switch (declaration.Parent)
        {
            case TypeDeclarationSyntax outer:
                var outerIndex = outer.Members.IndexOf(declaration);
                return root.ReplaceNode(outer, outer.WithMembers(outer.Members.Insert(outerIndex + 1, insert)));

            case FileScopedNamespaceDeclarationSyntax fileScoped:
                var fileIndex = fileScoped.Members.IndexOf(declaration);
                return root.ReplaceNode(fileScoped,
                    fileScoped.WithMembers(fileScoped.Members.Insert(fileIndex + 1, insert)));

            case NamespaceDeclarationSyntax block:
                var blockIndex = block.Members.IndexOf(declaration);
                return root.ReplaceNode(block, block.WithMembers(block.Members.Insert(blockIndex + 1, insert)));

            default:
                var unitIndex = root.Members.IndexOf(declaration);
                return root.WithMembers(root.Members.Insert(unitIndex + 1, insert));
        }
    }
}
