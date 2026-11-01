using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Invert a boolean member (Rider's "Invert Boolean"): every <c>return</c> or the expression body is
/// negated, and — when a new name is given — the member is renamed across the solution. Refused when
/// the body returns without a value or yields, because there is no boolean to invert there.
/// </summary>
public interface IInvertBooleanService
{
    Task<object> Invert(string filePath, string memberName, string? containingType, string? newName, bool dryRun,
        CancellationToken cancellationToken);
}

public sealed class InvertBooleanService : IInvertBooleanService
{
    private readonly IRoslynWorkspace _workspace;
    public InvertBooleanService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> Invert(string filePath, string memberName, string? containingType = null,
        string? newName = null, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(memberName)) return new { error = "A member name is required." };

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
            return await Snitch.Languages.VB.Refactoring.VbRefactoringTail.InvertBooleanAsync(
                _workspace, filePath, memberName, containingType, newName, dryRun, cancellationToken);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax of {Path.GetFileName(path)}." };

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(candidate => candidate.Identifier.ValueText == memberName)
            .Where(candidate => candidate.ReturnType is PredefinedTypeSyntax predefined &&
                                predefined.Keyword.IsKind(SyntaxKind.BoolKeyword))
            .Where(candidate => containingType is null ||
                                string.Equals(model.GetDeclaredSymbol(candidate, cancellationToken)?.ContainingType?.Name,
                                    containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (method.Count == 0)
            return new { error = $"No bool-returning method '{memberName}' in {Path.GetFileName(path)}." };
        if (method.Count > 1) return new { error = $"Several methods named '{memberName}'; address one overload at a time." };

        var declaration = method[0];
        if (model.GetDeclaredSymbol(declaration, cancellationToken) is not IMethodSymbol symbol)
            return new { error = $"'{memberName}' did not resolve." };

        var returnType = declaration.ReturnType;
        if (!returnType.ToString().Contains("bool", StringComparison.Ordinal))
            return new { error = $"'{memberName}' does not return bool." };

        // ── Negate ──────────────────────────────────────────────────────────────────────
        bool negatedSomething = false;
        var negationTargets = new List<ExpressionSyntax>();

        if (declaration.ExpressionBody is { } expressionBody)
        {
            negationTargets.Add(expressionBody.Expression);
        }
        else if (declaration.Body is { } block)
        {
            foreach (var statement in block.DescendantNodes().OfType<ReturnStatementSyntax>())
            {
                if (statement.Expression is null)
                    return new { error = $"'{memberName}' has a 'return;' without a value: it cannot be inverted." };
                negationTargets.Add(statement.Expression);
            }

            if (block.DescendantNodes().Any(node => node.IsKind(SyntaxKind.YieldReturnStatement)))
                return new { error = "The method yields: an iterator has no boolean result to invert." };
        }
        else
        {
            return new { error = "The method has neither a body nor an expression body." };
        }

        if (negationTargets.Count == 0)
            return new { error = $"'{memberName}' has no return statement to invert." };

        var spans = negationTargets.Select(target => target.Span).ToHashSet();
        var expressions = declaration.DescendantNodes()
            .OfType<ExpressionSyntax>()
            .Where(node => spans.Contains(node.Span))
            .ToList();

        var annotated = declaration.ReplaceNodes(expressions, (original, _) => Negate(original));

        var newText = root.ReplaceNode(declaration, annotated).ToFullString();
        var originalText = (await document.GetTextAsync(cancellationToken)).ToString();
        negatedSomething = !string.Equals(originalText, newText, StringComparison.Ordinal);

        if (!negatedSomething) return new { error = "The negation produced no change." };

        // ── Optional rename, on top of the inverted text ─────────────────────────────────
        var projected = solution.WithDocumentText(document.Id, SourceText.From(newText));
        var renamed = false;

        if (!string.IsNullOrWhiteSpace(newName))
        {
            if (!SyntaxFacts.IsValidIdentifier(newName))
                return new { error = $"'{newName}' is not a valid identifier." };

            var project = projected.GetProject(document.Project.Id)!;
            var compilation = await project.GetCompilationAsync(cancellationToken);
            var owner = symbol.ContainingType is null
                ? null
                : compilation?.GetTypeByMetadataName(symbol.ContainingType.ToDisplayString());

            var target = owner?.GetMembers(memberName)
                .OfType<IMethodSymbol>()
                .FirstOrDefault(candidate => candidate.Parameters.Length == symbol.Parameters.Length);

            if (target is null) return new { error = $"Could not resolve '{memberName}' in the projected compilation to rename it." };

            projected = await Renamer.RenameSymbolAsync(projected, target, new SymbolRenameOptions(), newName, cancellationToken);
            renamed = true;
        }

        // ── Gate and write every changed document ────────────────────────────────────────
        var changed = new List<(Document Document, string Text)>();
        foreach (var projectId in solution.ProjectIds)
        {
            var before = solution.GetProject(projectId);
            var after = projected.GetProject(projectId);
            if (before is null || after is null) continue;

            foreach (var documentId in before.DocumentIds)
            {
                var oldDocument = before.GetDocument(documentId);
                var newDocument = after.GetDocument(documentId);
                if (oldDocument?.FilePath is null || newDocument is null) continue;

                var oldText = await oldDocument.GetTextAsync(cancellationToken);
                var updatedText = await newDocument.GetTextAsync(cancellationToken);
                if (oldText.ContentEquals(updatedText)) continue;

                changed.Add((oldDocument, updatedText.ToString()));
            }
        }

        var gate = await RefactoringCompilerGate.CompareAsync(solution, projected,
            changed.Select(entry => entry.Document.Project.Id).Distinct().ToList(), cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, $"inverting '{memberName}'");

        if (!dryRun)
        {
            foreach (var (target, text) in changed)
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, target, text, cancellationToken);
        }

        return new
        {
            @refactoring = "invert_boolean",
            member = symbol.ToDisplayString(),
            invertedReturns = negationTargets.Count,
            renamedTo = renamed ? newName : null,
            filesModified = changed.Count,
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = gate.Before, after = gate.After },
            preview = changed.Count > 0
                ? RefactoringFileWriter.Diff(originalText, changed[0].Text)
                : null,
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    /// <summary><c>true</c>/<c>false</c> flip; anything else is parenthesised behind a <c>!</c>.</summary>
    private static ExpressionSyntax Negate(ExpressionSyntax expression)
    {
        if (expression.IsKind(SyntaxKind.TrueLiteralExpression))
            return SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)
                .WithTriviaFrom(expression);

        if (expression.IsKind(SyntaxKind.FalseLiteralExpression))
            return SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)
                .WithTriviaFrom(expression);

        if (expression is PrefixUnaryExpressionSyntax prefix && prefix.IsKind(SyntaxKind.LogicalNotExpression))
            return prefix.Operand.WithTriviaFrom(expression);

        return SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                SyntaxFactory.ParenthesizedExpression(expression.WithoutTrivia()))
            .WithTriviaFrom(expression);
    }
}
