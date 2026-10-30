using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Change type", gated on the compiler: replace a declaration's type and keep the change
/// only when the project still compiles. That turns a risky edit into a checked one — the errors the
/// new type causes are reported instead of being written to disk.
/// </summary>
public interface IChangeTypeService
{
    Task<object> ChangeType(
        string filePath,
        int line,
        string newType,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class ChangeTypeService : IChangeTypeService
{
    private readonly IRoslynWorkspace _workspace;

    public ChangeTypeService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ChangeType(
        string filePath,
        int line,
        string newType,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (line < 1) return new { error = "Line must be 1-based." };
        if (string.IsNullOrWhiteSpace(newType)) return new { error = "The new type is required." };

        var parsed = ParseTypeName(newType.Trim());
        if (parsed.ContainsDiagnostics)
            return new { error = $"'{newType}' is not a valid type name." };

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
        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (root is null || model is null || tree is null || compilation is null)
            return new { error = $"Could not read the syntax or semantic model of {Path.GetFileName(path)}." };

        var target = FindTypeOnLine(root, line);
        if (target is null)
            return new { error = $"No field, property, local, parameter or return type starts on line {line}." };

        var current = target.ToString();
        if (string.Equals(current, newType.Trim(), StringComparison.Ordinal))
            return new { error = $"The declaration on line {line} already has type '{current}'." };

        var newRoot = root.ReplaceNode(target, parsed.WithTriviaFrom(target));

        // ── Gate: the project must still compile ──────────────────────────────────────────
        var before = compilation.GetDiagnostics(cancellationToken)
            .Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var changedTree = CSharpSyntaxTree.Create(
            (CSharpSyntaxNode)newRoot, (CSharpParseOptions)tree.Options, document.FilePath, encoding: null);
        var afterDiagnostics = compilation.ReplaceSyntaxTree(tree, changedTree)
            .GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();

        var added = afterDiagnostics.Count - before;
        if (added > 0)
            return new
            {
                error = $"Refused: '{current}' → '{newType}' would add {added} compiler error(s) " +
                        $"({before} → {afterDiagnostics.Count}). Fix the usages first, or change the type text directly.",
                errors = afterDiagnostics
                    .Select(diagnostic => $"{diagnostic.Id} {diagnostic.GetMessage()}")
                    .Distinct()
                    .Take(8)
                    .ToList(),
            };

        var oldText = (await document.GetTextAsync(cancellationToken)).ToString();
        var newText = newRoot.ToFullString();

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "change_type",
            from = current,
            to = newType.Trim(),
            file = document.FilePath,
            line,
            compilerErrors = new { before, after = afterDiagnostics.Count },
            dryRun,
            applied = !dryRun,
            preview = RefactoringFileWriter.Diff(oldText, newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    /// <summary>The type syntax of the first field/property/local/parameter/return declaration starting on that line.</summary>
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
