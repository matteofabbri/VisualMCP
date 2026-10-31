using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Add params modifier" and its reverse, for Visual Basic: the last array parameter starts
/// (or stops) collecting call sites, spelled <c>ParamArray</c>. <c>ParamArray</c> implies <c>ByVal</c>,
/// so the explicit modifier is dropped when one is present.
/// </summary>
public sealed partial class AddParamsModifierService
{
    private static async Task<object?> TryVisualBasicAsync(
        IRoslynWorkspace workspace, Document document, string methodName, string? containingType,
        bool dryRun, CancellationToken cancellationToken)
    {
        if (document.Project.Language != LanguageNames.VisualBasic) return null;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(document.FilePath)}." };

        var statements = VbMethodStatements(root)
            .Where(statement => statement.Identifier.ValueText == methodName)
            .Where(statement => containingType is null ||
                                string.Equals(model.GetDeclaredSymbol(VbOwnerOf(statement), cancellationToken)?.ContainingType?.Name,
                                    containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (statements.Count == 0) return new { error = $"No method '{methodName}' in {Path.GetFileName(document.FilePath)}." };
        if (statements.Count > 1) return new { error = $"Several methods named '{methodName}'; address one overload at a time." };

        var declaration = statements[0];
        if (model.GetDeclaredSymbol(VbOwnerOf(declaration), cancellationToken) is not IMethodSymbol method)
            return new { error = $"'{methodName}' did not resolve to a method." };

        if (declaration.Modifiers.Any(SyntaxKind.OverridesKeyword))
            return new { error = $"'{methodName}' overrides a base member: adding params would break the override." };
        if (declaration.ImplementsClause is not null)
            return new { error = $"'{methodName}' implements an interface member: the signature must stay identical." };

        var parameterList = declaration.ParameterList;
        var last = parameterList?.Parameters.LastOrDefault();
        if (parameterList is null || last is null) return new { error = $"'{methodName}' has no parameters." };
        if (last.Modifiers.Any(SyntaxKind.ParamArrayKeyword)) return new { error = "The last parameter is already 'ParamArray'." };
        if (!IsOneDimensionalArray(last, out var elementType))
            return new { error = "The last parameter is not a one-dimensional array; ParamArray needs T()." };

        var updated = last
            .WithModifiers(SyntaxFactory.TokenList(
                last.Modifiers
                    .Where(token => !token.IsKind(SyntaxKind.ByValKeyword))
                    .Prepend(SyntaxFactory.Token(SyntaxKind.ParamArrayKeyword).WithTrailingTrivia(SyntaxFactory.Space))))
            .WithLeadingTrivia(last.GetLeadingTrivia());

        var newDeclaration = declaration.WithParameterList(
            parameterList.WithParameters(parameterList.Parameters.Replace(last, updated)));

        var newRoot = root.ReplaceNode(declaration, newDeclaration);
        var oldText = (await document.GetTextAsync(cancellationToken)).ToString();
        var newText = newRoot.ToFullString();

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "add_params",
            method = method.ToDisplayString(),
            parameter = $"{elementType}() {last.Identifier.Identifier.ValueText}",
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            preview = RefactoringFileWriter.Diff(oldText, newText),
            note = dryRun
                ? "Preview only: pass dryRun=false to write the file."
                : "File written. Existing call sites keep compiling: they pass an array explicitly.",
        };
    }

    /// <summary>The declarations that can be a method: a block with a body, or a body-less statement.</summary>
    internal static IEnumerable<MethodStatementSyntax> VbMethodStatements(SyntaxNode root) =>
        root.DescendantNodes().Select(node => node switch
        {
            MethodBlockSyntax block => block.SubOrFunctionStatement,
            MethodStatementSyntax statement when statement.Parent is not MethodBlockSyntax => statement,
            _ => null,
        }).OfType<MethodStatementSyntax>();

    /// <summary>The node to ask the semantic model about: the block when there is one.</summary>
    internal static SyntaxNode VbOwnerOf(MethodStatementSyntax statement) =>
        statement.Parent is MethodBlockSyntax block ? block : statement;

    /// <summary>
    /// Whether the parameter is a one-dimensional array. VB writes the array on the name
    /// (<c>values() As String</c>): the parentheses live in the declarator's bounds, and the
    /// <c>As</c> clause names the element type.
    /// </summary>
    internal static bool IsOneDimensionalArray(ParameterSyntax parameter, out string elementType)
    {
        elementType = string.Empty;

        // `values()` carries one rank specifier of rank 1 on the declarator; `values(,)` has rank 2.
        if (parameter.Identifier.ArrayRankSpecifiers is not [{ Rank: 1 }]) return false;

        if (parameter.AsClause is not SimpleAsClauseSyntax simple) return false;

        elementType = simple.Type.ToString();
        return elementType.Length > 0;
    }
}

public sealed partial class RemoveParamsModifierService
{
    private static async Task<object?> TryVisualBasicAsync(
        IRoslynWorkspace workspace, Document document, string methodName, string? containingType,
        bool dryRun, CancellationToken cancellationToken)
    {
        if (document.Project.Language != LanguageNames.VisualBasic) return null;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(document.FilePath)}." };

        var statements = AddParamsModifierService.VbMethodStatements(root)
            .Where(statement => statement.Identifier.ValueText == methodName)
            .Where(statement => containingType is null ||
                                string.Equals(model.GetDeclaredSymbol(AddParamsModifierService.VbOwnerOf(statement), cancellationToken)
                                        ?.ContainingType?.Name,
                                    containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (statements.Count == 0) return new { error = $"No method '{methodName}' in {Path.GetFileName(document.FilePath)}." };
        if (statements.Count > 1) return new { error = $"Several methods named '{methodName}'; address one overload at a time." };

        var declaration = statements[0];
        var parameterList = declaration.ParameterList;
        var last = parameterList?.Parameters.LastOrDefault();
        if (parameterList is null || last is null) return new { error = $"'{methodName}' has no parameters." };
        if (!last.Modifiers.Any(SyntaxKind.ParamArrayKeyword))
            return new { error = "The last parameter does not carry 'ParamArray'." };

        var updated = last
            .WithModifiers(SyntaxFactory.TokenList(
                last.Modifiers.Where(token => !token.IsKind(SyntaxKind.ParamArrayKeyword))))
            .WithLeadingTrivia(last.GetLeadingTrivia());

        var newDeclaration = declaration.WithParameterList(
            parameterList.WithParameters(parameterList.Parameters.Replace(last, updated)));
        var newRoot = root.ReplaceNode(declaration, newDeclaration);

        var oldText = (await document.GetTextAsync(cancellationToken)).ToString();
        var newText = newRoot.ToFullString();
        if (string.Equals(oldText, newText, StringComparison.Ordinal))
            return new { error = "'ParamArray' was already absent after the rewrite." };

        // ── Gate: every call site must still compile ─────────────────────────────
        var current = await workspace.EnsureSolutionLoadedAsync(cancellationToken);
        var projected = current!.WithDocumentText(document.Id, Microsoft.CodeAnalysis.Text.SourceText.From(newText));
        var gate = await RefactoringCompilerGate.CompareAsync(current!, projected, [document.Project.Id], cancellationToken);
        if (!gate.Passed)
            return RefactoringCompilerGate.Refusal(gate,
                $"removing 'ParamArray' from '{methodName}' (call sites that pass the elements individually would break)");

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "remove_params",
            method = methodName,
            parameter = last.Identifier.Identifier.ValueText,
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = gate.Before, after = gate.After },
            preview = RefactoringFileWriter.Diff(oldText, newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }
}
