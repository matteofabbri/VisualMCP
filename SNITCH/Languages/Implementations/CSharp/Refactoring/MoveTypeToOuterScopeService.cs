using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Move type to outer scope": a nested type becomes a sibling of its container, and every
/// <c>Outer.Inner</c> reference becomes <c>Inner</c>. Gated on the compiler like change_type.
/// </summary>
public interface IMoveTypeToOuterScopeService
{
    Task<object> MoveTypeToOuterScope(
        string filePath,
        string typeName,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class MoveTypeToOuterScopeService : IMoveTypeToOuterScopeService
{
    private readonly IRoslynWorkspace _workspace;

    public MoveTypeToOuterScopeService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> MoveTypeToOuterScope(
        string filePath,
        string typeName,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(typeName)) return new { error = "The nested type name is required." };

        if (filePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
            return await Snitch.Languages.VB.Refactoring.VbRefactoringTail.MoveTypeToOuterScopeAsync(
                _workspace, filePath, typeName, dryRun, cancellationToken);

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

        var declarationRoot = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var declarationModel = await document.GetSemanticModelAsync(cancellationToken);
        if (declarationRoot is null || declarationModel is null)
            return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var nested = declarationRoot.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == typeName &&
                                         candidate.Parent is TypeDeclarationSyntax);
        if (nested is null) return new { error = $"No nested type '{typeName}' in {Path.GetFileName(path)}." };

        var outerDeclaration = (TypeDeclarationSyntax)nested.Parent!;
        if (declarationModel.GetDeclaredSymbol(nested, cancellationToken) is not INamedTypeSymbol nestedSymbol)
            return new { error = $"'{typeName}' did not resolve to a type." };

        var extensionNamespace = nestedSymbol.ContainingNamespace;

        // ── Per document: rewrite `Outer.Inner` → `Inner` (symbols come from the original tree) ──
        var documents = solution.Projects.SelectMany(project => project.Documents).ToList();
        var edits = new List<(Document Document, string NewText, int References)>();
        var usingsAdded = 0;
        var moved = false;

        foreach (var target in documents)
        {
            var targetRoot = await target.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
            var targetModel = await target.GetSemanticModelAsync(cancellationToken);
            if (targetRoot is null || targetModel is null) continue;

            var references = targetRoot.DescendantNodes()
                .Where(node => node is QualifiedNameSyntax or MemberAccessExpressionSyntax)
                .Where(node => SymbolEqualityComparer.Default.Equals(
                    targetModel.GetSymbolInfo(node, cancellationToken).Symbol, nestedSymbol))
                .ToList();

            var newRoot = references.Count == 0
                ? targetRoot
                : targetRoot.ReplaceNodes(references,
                    (original, _) => IdentifierName(typeName).WithTriviaFrom(original));

            if (target.Id == document.Id)
            {
                var movedRoot = MoveOut(newRoot, typeName, outerDeclaration.Identifier.ValueText, out var failure);
                if (movedRoot is null) return new { error = failure };
                newRoot = movedRoot;
                moved = true;
            }
            else if (references.Count > 0 && !NamespaceVisible(newRoot, extensionNamespace))
            {
                newRoot = AddUsing(newRoot, extensionNamespace);
                usingsAdded++;
            }

            var oldText = (await target.GetTextAsync(cancellationToken)).ToString();
            var newText = newRoot.ToFullString();
            if (string.Equals(oldText, newText, StringComparison.Ordinal)) continue;

            edits.Add((target, newText, references.Count));
        }

        if (!moved) return new { error = $"The declaration of '{typeName}' could not be moved." };

        // ── Gate: the project must still compile ──────────────────────────────────────────
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (compilation is null) return new { error = "The project produced no compilation." };

        var before = compilation.GetDiagnostics(cancellationToken)
            .Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var projected = document.Project;
        foreach (var edit in edits)
            projected = projected.Solution.WithDocumentText(edit.Document.Id, SourceText.From(edit.NewText))
                .GetProject(document.Project.Id)!;

        var afterDiagnostics = (await projected.GetCompilationAsync(cancellationToken))!
            .GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();

        var added = afterDiagnostics.Count - before;
        if (added > 0)
            return new
            {
                error = $"Refused: moving '{typeName}' out would add {added} compiler error(s) " +
                        $"({before} → {afterDiagnostics.Count}).",
                errors = afterDiagnostics.Select(diagnostic => $"{diagnostic.Id} {diagnostic.GetMessage()}")
                    .Distinct().Take(8).ToList(),
            };

        if (!dryRun)
        {
            foreach (var edit in edits)
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, edit.Document, edit.NewText, cancellationToken);
        }

        return new
        {
            @refactoring = "move_type_to_outer_scope",
            type = nestedSymbol.ToDisplayString(),
            movedOutOf = outerDeclaration.Identifier.ValueText,
            file = document.FilePath,
            referencesRewritten = edits.Sum(edit => edit.References),
            usingsAdded,
            filesChanged = edits.Count,
            compilerErrors = new { before, after = afterDiagnostics.Count },
            dryRun,
            applied = !dryRun,
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    /// <summary>
    /// Re-finds the nested type in the rewritten tree (references were replaced there) and hoists it
    /// one level. Everything works on the same tree, so no node crosses trees.
    /// </summary>
    private static CompilationUnitSyntax? MoveOut(
        CompilationUnitSyntax root, string typeName, string outerName, out string? failure)
    {
        failure = null;

        var nested = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == typeName &&
                                         candidate.Parent is TypeDeclarationSyntax parent &&
                                         parent.Identifier.ValueText == outerName);
        if (nested is null || nested.Parent is not TypeDeclarationSyntax outer)
        {
            failure = $"Lost the declaration of '{typeName}' while rewriting.";
            return null;
        }

        var indentation = IndentationOf(outer);
        var moved = ParseMemberDeclaration(Reindent(nested.NormalizeWhitespace().ToFullString(), indentation));
        if (moved is null)
        {
            failure = $"'{typeName}' could not be re-parsed after normalising it.";
            return null;
        }

        moved = moved
            .WithLeadingTrivia(ElasticCarriageReturnLineFeed, ElasticCarriageReturnLineFeed, Whitespace(indentation))
            .WithTrailingTrivia(ElasticCarriageReturnLineFeed);

        var newOuter = outer.WithMembers(outer.Members.Remove(nested));

        switch (outer.Parent)
        {
            case CompilationUnitSyntax unit:
                return root.WithMembers(ReplaceAndInsert(unit.Members, outer, newOuter, moved));
            case FileScopedNamespaceDeclarationSyntax fileScoped:
                return root.ReplaceNode(fileScoped,
                    fileScoped.WithMembers(ReplaceAndInsert(fileScoped.Members, outer, newOuter, moved)));
            case NamespaceDeclarationSyntax block:
                return root.ReplaceNode(block,
                    block.WithMembers(ReplaceAndInsert(block.Members, outer, newOuter, moved)));
            default:
                failure = $"'{outerName}' is not directly inside a namespace.";
                return null;
        }
    }

    private static SyntaxList<MemberDeclarationSyntax> ReplaceAndInsert(
        SyntaxList<MemberDeclarationSyntax> members,
        MemberDeclarationSyntax original,
        MemberDeclarationSyntax replacement,
        MemberDeclarationSyntax extra)
    {
        var index = members.IndexOf(original);
        return members.Replace(original, replacement).Insert(index + 1, extra);
    }

    private static bool NamespaceVisible(SyntaxNode root, INamespaceSymbol extensionNamespace)
    {
        var namespaceText = extensionNamespace.ToDisplayString();

        foreach (var declaration in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
        {
            var enclosing = declaration.Name.ToString();
            if (string.Equals(enclosing, namespaceText, StringComparison.Ordinal) ||
                enclosing.StartsWith(namespaceText + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return root is CompilationUnitSyntax unit && unit.Usings.Any(directive =>
            directive.Alias is null && string.Equals(directive.Name?.ToString(), namespaceText, StringComparison.Ordinal));
    }

    private static CompilationUnitSyntax AddUsing(SyntaxNode root, INamespaceSymbol extensionNamespace)
    {
        if (root is not CompilationUnitSyntax unit) return (CompilationUnitSyntax)root;

        var directive = UsingDirective(ParseName(extensionNamespace.ToDisplayString()))
            .WithUsingKeyword(Token(SyntaxKind.UsingKeyword).WithTrailingTrivia(Space))
            .WithTrailingTrivia(EndOfLine(Environment.NewLine));

        return unit.WithUsings(unit.Usings.Add(directive));
    }

    private static string IndentationOf(SyntaxNode node)
    {
        var leading = node.GetLeadingTrivia().ToString().Replace("\r", string.Empty);
        var lastBreak = leading.LastIndexOf('\n');
        return lastBreak < 0 ? string.Empty : leading[(lastBreak + 1)..];
    }

    private static string Reindent(string text, string indentation)
    {
        if (indentation.Length == 0) return text;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Select((line, index) =>
            index == 0 || line.Length == 0 ? line : indentation + line));
    }
}
