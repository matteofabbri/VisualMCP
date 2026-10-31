using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// The inverse of "Add params modifier": remove <c>params</c> from a method's last array parameter.
/// Call sites that pass the elements individually stop compiling, so the compiler is the gate —
/// removing the modifier from a method with such a call site is refused with the errors.
/// </summary>
public interface IRemoveParamsModifierService
{
    Task<object> RemoveParams(string filePath, string methodName, string? containingType, bool dryRun,
        CancellationToken cancellationToken);
}

public sealed partial class RemoveParamsModifierService : IRemoveParamsModifierService
{
    private readonly IRoslynWorkspace _workspace;
    public RemoveParamsModifierService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> RemoveParams(string filePath, string methodName, string? containingType = null,
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

        if (document.Project.Language == LanguageNames.VisualBasic)
            return await TryVisualBasicAsync(_workspace, document, methodName, containingType, dryRun, cancellationToken)
                   ?? new { error = "The Visual Basic rewrite did not produce a result." };

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
        if (candidates.Count > 1) return new { error = $"Several methods named '{methodName}'; address one overload at a time." };

        var declaration = candidates[0];
        var last = declaration.ParameterList.Parameters.LastOrDefault();
        if (last is null) return new { error = $"'{methodName}' has no parameters." };
        if (!last.Modifiers.Any(SyntaxKind.ParamsKeyword))
            return new { error = "The last parameter does not carry 'params'." };

        var newModifiers = last.Modifiers.Where(token => !token.IsKind(SyntaxKind.ParamsKeyword));
        var updated = last.WithModifiers(SyntaxFactory.TokenList(newModifiers))
            .WithLeadingTrivia(last.GetLeadingTrivia());

        var newDeclaration = declaration.WithParameterList(
            declaration.ParameterList.WithParameters(
                declaration.ParameterList.Parameters.Replace(last, updated)));
        var newRoot = root.ReplaceNode(declaration, newDeclaration);

        var newText = newRoot.ToFullString();
        var oldText = (await document.GetTextAsync(cancellationToken)).ToString();
        if (string.Equals(oldText, newText, StringComparison.Ordinal))
            return new { error = "'params' was already absent after the rewrite." };

        // ── Gate: every call site must still compile ─────────────────────────────────────
        var projected = solution.WithDocumentText(document.Id, SourceText.From(newText));
        var gate = await RefactoringCompilerGate.CompareAsync(solution, projected, [document.Project.Id], cancellationToken);
        if (!gate.Passed)
            return RefactoringCompilerGate.Refusal(gate,
                $"removing 'params' from '{methodName}' (call sites that pass the elements individually would break)");

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "remove_params",
            method = methodName,
            parameter = $"{last.Type} {last.Identifier.ValueText}",
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = gate.Before, after = gate.After },
            preview = RefactoringFileWriter.Diff(oldText, newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }
}
