using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools;

[McpServerToolType]
public static class IntroduceVariableTool
{
    [McpServerTool, Description(
        "Extract an expression at a given file + line into a new local variable, replacing every occurrence " +
        "of the same expression in the enclosing method. " +
        "Equivalent to ReSharper 'Introduce Variable'. Requires LoadSolution first.")]
    public static async Task<object> IntroduceVariable(
        [Description("Absolute path to the source file")] string filePath,
        [Description("Line number (1-based) where the expression appears")] int line,
        [Description("The exact text of the expression to extract (used to locate and replace all occurrences)")] string expressionText,
        [Description("Name for the new local variable")] string variableName,
        [Description("Dry run — show the change without writing to disk (default: false)")] bool dryRun = false)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        var document = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));

        if (document is null)
            return new { error = $"File '{filePath}' is not part of the loaded solution." };

        var sourceText = await document.GetTextAsync();
        if (line < 1 || line > sourceText.Lines.Count)
            return new { error = $"Line {line} is out of range." };

        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();
        if (root is null || model is null)
            return new { error = "Could not get semantic model." };

        // Find the expression on the specified line
        var textLine = sourceText.Lines[line - 1];
        var lineText = textLine.ToString();
        var col = lineText.IndexOf(expressionText, StringComparison.Ordinal);
        if (col < 0)
            return new { error = $"Expression '{expressionText}' not found on line {line}." };

        var position = textLine.Start + col;
        var targetExpr = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(position, expressionText.Length), findInsideTrivia: false, getInnermostNodeForTie: true)
            as ExpressionSyntax
            ?? root.DescendantNodes()
                   .OfType<ExpressionSyntax>()
                   .FirstOrDefault(e => e.ToString() == expressionText &&
                                        e.SpanStart >= textLine.Start &&
                                        e.SpanStart <= textLine.End);

        if (targetExpr is null)
            return new { error = $"Could not locate expression '{expressionText}' as a syntax node on line {line}." };

        // Determine the type for 'var' / explicit
        var typeInfo = model.GetTypeInfo(targetExpr);
        var typeStr = typeInfo.Type is not null
            ? typeInfo.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            : "var";

        // Find the enclosing statement (the insertion point)
        var enclosingStatement = targetExpr.FirstAncestorOrSelf<StatementSyntax>();
        if (enclosingStatement is null)
            return new { error = "The expression is not inside a statement — cannot introduce a variable here." };

        // Find all identical expression nodes within the enclosing method body
        var enclosingMethod = enclosingStatement.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>()
            ?? enclosingStatement.FirstAncestorOrSelf<AccessorDeclarationSyntax>() as SyntaxNode
            ?? enclosingStatement.Parent;

        if (enclosingMethod is null)
            return new { error = "Could not find the enclosing method." };

        var allOccurrences = enclosingMethod
            .DescendantNodes()
            .OfType<ExpressionSyntax>()
            .Where(e => e.ToString() == expressionText)
            .OrderByDescending(e => e.SpanStart)
            .ToList();

        // Build the variable declaration line
        var indent = GetIndent(enclosingStatement);
        var varDecl = $"{indent}var {variableName} = {expressionText};";

        // Work on raw text: replace all occurrences end-to-start, then insert declaration
        var text = sourceText.ToString();

        foreach (var occ in allOccurrences)
        {
            text = text[..occ.SpanStart] + variableName + text[(occ.SpanStart + occ.Span.Length)..];
        }

        // Insert variable declaration before the (now-modified) enclosing statement
        // The enclosing statement position is still valid (we only replaced inside it, not before)
        var stmtStart = enclosingStatement.FullSpan.Start;
        text = text[..stmtStart] + varDecl + "\n" + text[stmtStart..];

        if (dryRun)
            return new
            {
                dryRun = true,
                filePath,
                line,
                expressionText,
                variableName,
                inferredType = typeStr,
                occurrencesReplaced = allOccurrences.Count,
            };

        await File.WriteAllTextAsync(document.FilePath!, text, System.Text.Encoding.UTF8);

        return new
        {
            dryRun = false,
            filePath,
            variableName,
            inferredType = typeStr,
            occurrencesReplaced = allOccurrences.Count,
        };
    }

    private static string GetIndent(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia()
            .Where(t => t.IsKind(SyntaxKind.WhitespaceTrivia))
            .LastOrDefault();
        return trivia.ToString();
    }
}
