using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>Rider's "Invert if": swap the branches and negate the condition, when there is an else.</summary>
public interface IInvertIfService
{
    Task<object> InvertIf(string filePath, int line, bool dryRun = false, CancellationToken cancellationToken = default);
}

public sealed class InvertIfService : IInvertIfService
{
    private readonly IRoslynWorkspace _workspace;

    public InvertIfService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> InvertIf(string filePath, int line, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (line < 1) return new { error = "Line must be 1-based." };

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

        var sourceText = await document.GetTextAsync(cancellationToken);
        if (line > sourceText.Lines.Count) return new { error = $"Line {line} is out of range." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var statement = root.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(candidate => candidate.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line)
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (statement is null) return new { error = $"No if statement starts on line {line}." };
        if (statement.Else is null) return new { error = "The if has no else branch: inverting it would change control flow." };
        if (statement.Else.Statement is IfStatementSyntax)
            return new { error = "An 'else if' chain is not supported; convert it to a block first." };

        var invertedCondition = Negate(statement.Condition);

        // Swapping branches moves nodes that carry their own trivia, which produces `else        {`.
        // Normalise the inverted statement, re-indent it to its original column and re-parse it. The
        // indentation comes from the source line: the first statement of a block has no leading
        // trivia of its own (the newline belongs to the `{` before it).
        var lineText = sourceText.Lines[line - 1].ToString();
        var indentation = new string(lineText.TakeWhile(char.IsWhiteSpace).ToArray());

        var normalized = statement
            .WithCondition(invertedCondition)
            .WithStatement(statement.Else.Statement.WithoutTrivia())
            .WithElse(ElseClause(statement.Statement.WithoutTrivia()))
            .NormalizeWhitespace()
            .ToFullString();

        var reparsed = ParseStatement(Reindent(normalized, indentation))
            .WithLeadingTrivia(statement.GetLeadingTrivia())
            .WithTrailingTrivia(statement.GetTrailingTrivia());

        var newRoot = root.ReplaceNode(statement, reparsed);
        var oldText = sourceText.ToString();
        var newText = newRoot.ToFullString();

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "invert_if",
            condition = invertedCondition.ToString(),
            file = document.FilePath,
            line,
            dryRun,
            applied = !dryRun,
            preview = RefactoringFileWriter.Diff(oldText, newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    /// <summary>`!x`, `a &amp;&amp; b` → `!(a &amp;&amp; b)`, `!(...)` → the operand.</summary>
    private static ExpressionSyntax Negate(ExpressionSyntax condition)
    {
        if (condition is PrefixUnaryExpressionSyntax negation && negation.IsKind(SyntaxKind.LogicalNotExpression))
            return negation.Operand.WithTriviaFrom(condition);

        var atomic = condition is IdentifierNameSyntax
                              or MemberAccessExpressionSyntax
                              or InvocationExpressionSyntax
                              or ElementAccessExpressionSyntax
                              or LiteralExpressionSyntax
                              or ObjectCreationExpressionSyntax
                              or ThisExpressionSyntax;

        var operand = atomic
            ? (ExpressionSyntax)condition.WithoutTrivia()
            : ParenthesizedExpression(condition.WithoutTrivia());

        return PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, operand).WithTriviaFrom(condition);
    }

    /// <summary>The whitespace that precedes this node on its own line.</summary>
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
