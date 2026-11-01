using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Use base type": replace a declaration's type with one of its base types, when the
/// compiler accepts it. A narrower, self-checking change_type — the base type is derived, not typed.
/// </summary>
public interface IUseBaseTypeService
{
    Task<object> UseBaseType(
        string filePath,
        int line,
        string? baseType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class UseBaseTypeService : IUseBaseTypeService
{
    private readonly IRoslynWorkspace _workspace;

    public UseBaseTypeService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> UseBaseType(
        string filePath,
        int line,
        string? baseType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (line < 1) return new { error = "Line must be 1-based." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };

        if (document.Project.Language == LanguageNames.VisualBasic)
            return await Snitch.Languages.VB.Refactoring.VbRefactoringSupport.UseBaseTypeAsync(
                _workspace, document, line, baseType, dryRun, cancellationToken);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (root is null || model is null || tree is null || compilation is null)
            return new { error = $"Could not read the syntax or semantic model of {Path.GetFileName(path)}." };

        var target = FindTypeOnLine(root, line);
        if (target is null)
            return new { error = $"No field, property, local, parameter or return type starts on line {line}." };

        if (model.GetTypeInfo(target, cancellationToken).Type is not { } current)
            return new { error = "The declaration's type did not resolve." };

        var candidates = BaseCandidates(current);
        if (candidates.Count == 0)
            return new { error = $"'{current.ToDisplayString()}' has no base type in source." };

        INamedTypeSymbol chosen;
        if (baseType is null)
        {
            if (candidates.Count > 1)
                return new
                {
                    error = "Several base types are available: pass baseType.",
                    candidates = candidates.Select(candidate => candidate.ToDisplayString()).ToList(),
                };

            chosen = candidates[0];
        }
        else
        {
            chosen = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, baseType, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.ToDisplayString(), baseType, StringComparison.OrdinalIgnoreCase))!;

            if (chosen is null)
                return new
                {
                    error = $"'{baseType}' is not a base type of '{current.ToDisplayString()}'.",
                    candidates = candidates.Select(candidate => candidate.ToDisplayString()).ToList(),
                };
        }

        var newType = ParseTypeName(chosen.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
            .WithTriviaFrom(target);
        var newRoot = root.ReplaceNode(target, newType);

        // ── Gate: the project must still compile ──────────────────────────────────────────
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
                error = $"Refused: '{current.ToDisplayString()}' → '{chosen.ToDisplayString()}' would add {added} compiler error(s) " +
                        $"({before} → {afterDiagnostics.Count}): the usages need the derived members.",
                errors = afterDiagnostics.Select(diagnostic => $"{diagnostic.Id} {diagnostic.GetMessage()}")
                    .Distinct().Take(8).ToList(),
            };

        var oldText = (await document.GetTextAsync(cancellationToken)).ToString();
        var newText = newRoot.ToFullString();

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "use_base_type",
            from = current.ToDisplayString(),
            to = chosen.ToDisplayString(),
            file = document.FilePath,
            line,
            compilerErrors = new { before, after = afterDiagnostics.Count },
            dryRun,
            applied = !dryRun,
            preview = RefactoringFileWriter.Diff(oldText, newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    /// <summary>The base class and the inherited interfaces, in source.</summary>
    private static List<INamedTypeSymbol> BaseCandidates(ITypeSymbol type)
    {
        var candidates = new List<INamedTypeSymbol>();
        if (type.BaseType is { SpecialType: not SpecialType.System_Object } baseType) candidates.Add(baseType);

        candidates.AddRange(type.AllInterfaces.OrderBy(candidate => candidate.Name, StringComparer.Ordinal));
        return candidates;
    }

    private static TypeSyntax? FindTypeOnLine(SyntaxNode root, int line) =>
        root.DescendantNodes()
            .Where(node => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line)
            .OrderBy(node => node.SpanStart)
            .Select(node => node switch
            {
                VariableDeclarationSyntax variable => variable.Type,
                PropertyDeclarationSyntax property => property.Type,
                ParameterSyntax parameter => parameter.Type,
                MethodDeclarationSyntax method => IsVoid(method.ReturnType) ? null : method.ReturnType,
                _ => null,
            })
            .FirstOrDefault(type => type is not null);

    private static bool IsVoid(TypeSyntax type) =>
        type is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);
}
