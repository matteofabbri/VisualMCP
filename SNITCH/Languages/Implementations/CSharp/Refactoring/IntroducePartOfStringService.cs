using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Introduce part of string": lifts a substring of a string literal into a named constant and
/// leaves a concatenation (or the constant alone when the whole literal is selected) behind.
/// </summary>
public interface IIntroducePartOfStringService
{
    Task<object> IntroducePartOfString(
        string filePath,
        int line,
        string part,
        string constantName = "Extracted",
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class IntroducePartOfStringService : IIntroducePartOfStringService
{
    private readonly IRoslynWorkspace _workspace;

    public IntroducePartOfStringService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> IntroducePartOfString(
        string filePath,
        int line,
        string part,
        string constantName = "Extracted",
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (line <= 0) return new { error = "A 1-based line number is required." };
        if (part is null || part.Length == 0) return new { error = "The substring to extract is required." };
        if (!SyntaxFacts.IsValidIdentifier(constantName))
            return new { error = $"'{constantName}' is not a valid constant name." };

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

        var literals = root.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
            .Where(literal =>
            {
                var span = literal.GetLocation().GetLineSpan();
                return line >= span.StartLinePosition.Line + 1 && line <= span.EndLinePosition.Line + 1;
            })
            .Where(literal => literal.Token.ValueText.Contains(part, StringComparison.Ordinal))
            .ToList();

        if (literals.Count == 0)
            return new
            {
                error = $"No string literal containing \"{part}\" on line {line} of {Path.GetFileName(path)}.",
                literalsOnLine = root.DescendantNodes().OfType<LiteralExpressionSyntax>()
                    .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
                    .Where(literal =>
                    {
                        var span = literal.GetLocation().GetLineSpan();
                        return line >= span.StartLinePosition.Line + 1 && line <= span.EndLinePosition.Line + 1;
                    })
                    .Select(literal => literal.Token.ValueText)
                    .ToList(),
            };

        if (literals.Count > 1)
            return new { error = $"{literals.Count} string literals on line {line} contain \"{part}\"; select a line with one of them." };

        var literalNode = literals[0];
        var value = literalNode.Token.ValueText;
        var index = value.IndexOf(part, StringComparison.Ordinal);
        if (value.IndexOf(part, index + 1, StringComparison.Ordinal) >= 0)
            return new { error = $"\"{part}\" occurs more than once in \"{value}\"; the extraction would be ambiguous." };

        var typeDeclaration = literalNode.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDeclaration is null) return new { error = "The literal is not inside a type declaration." };

        if (model.GetDeclaredSymbol(typeDeclaration, cancellationToken) is { } typeSymbol &&
            typeSymbol.GetMembers(constantName).Length > 0)
            return new { error = $"'{typeSymbol.ToDisplayString()}' already has a member named '{constantName}'." };

        var prefix = value[..index];
        var suffix = value[(index + part.Length)..];

        var pieces = new List<string>();
        if (prefix.Length > 0) pieces.Add(SymbolDisplay.FormatLiteral(prefix, quote: true));
        pieces.Add(constantName);
        if (suffix.Length > 0) pieces.Add(SymbolDisplay.FormatLiteral(suffix, quote: true));

        var replacement = ParseExpression(string.Join(" + ", pieces)).WithTriviaFrom(literalNode);

        // Replace the literal FIRST (its node is the one the semantic model produced), then find the
        // type by name for the constant: after a replacement the old node instances are not reliable keys.
        var replaced = root.ReplaceNode(literalNode, replacement);

        var target = replaced.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == typeDeclaration.Identifier.ValueText);
        if (target is null) return new { error = "The containing type was lost while the edits were applied." };

        var constant = ParseMemberDeclaration(
            $"private const string {constantName} = {SymbolDisplay.FormatLiteral(part, quote: true)};")!;

        var indentation = IndentationOf(target) + "    ";
        var withConstant = target.WithMembers(target.Members.Insert(0, constant
            .WithLeadingTrivia(Whitespace(indentation))
            .WithTrailingTrivia(ElasticCarriageReturnLineFeed)));

        var newText = replaced.ReplaceNode(target, withConstant).ToFullString();

        var projected = document.Project.Solution.WithDocumentText(document.Id, SourceText.From(newText));
        var gate = await RefactoringCompilerGate.CompareAsync(
            document.Project.Solution, projected, new[] { document.Project.Id }, cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "the extraction");

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "introduce_part_of_string",
            constant = constantName,
            value = part,
            file = document.FilePath,
            line,
            compilerErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            preview = RefactoringFileWriter.Diff(root.ToFullString(), newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    private static string IndentationOf(SyntaxNode node)
    {
        var leading = node.GetLeadingTrivia().ToString().Replace("\r", string.Empty);
        var lastBreak = leading.LastIndexOf('\n');
        return lastBreak < 0 ? string.Empty : leading[(lastBreak + 1)..];
    }
}
