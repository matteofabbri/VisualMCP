using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Extract method", narrowed to the case that can be proven behaviour-preserving: a
/// contiguous statement block that reads (but never writes) the locals declared before it, contains
/// no jumps, and optionally ends with a single <c>return expression;</c>. A block that awaits is
/// extracted as <c>async Task(…)</c> and called with <c>await</c>, which requires the enclosing
/// method to be async already.
/// </summary>
public interface IExtractMethodService
{
    Task<object> ExtractMethod(
        string filePath,
        int fromLine,
        int toLine,
        string methodName,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class ExtractMethodService : IExtractMethodService
{
    private readonly IRoslynWorkspace _workspace;

    public ExtractMethodService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ExtractMethod(
        string filePath,
        int fromLine,
        int toLine,
        string methodName,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (fromLine < 1 || toLine < fromLine) return new { error = "A valid 1-based line range is required." };
        if (string.IsNullOrWhiteSpace(methodName) || !SyntaxFacts.IsValidIdentifier(methodName))
            return new { error = $"'{methodName}' is not a valid method name." };

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
        if (toLine > sourceText.Lines.Count) return new { error = $"Line {toLine} is out of range." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        // ── Enclosing method and the selected statements ───────────────────────────────────
        var containingMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(candidate => candidate.Body is not null && CoversLines(candidate, fromLine, toLine))
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (containingMethod?.Body is null)
            return new { error = $"No method body encloses lines {fromLine}-{toLine}." };

        var block = containingMethod.Body;
        var statements = block.Statements;

        var selectedIndexes = statements
            .Select((statement, index) => (statement, index))
            .Where(pair => StartLine(pair.statement) >= fromLine && EndLine(pair.statement) <= toLine)
            .Select(pair => pair.index)
            .ToList();

        if (selectedIndexes.Count == 0)
            return new { error = $"No statements of '{containingMethod.Identifier.ValueText}' fall inside lines {fromLine}-{toLine}." };

        var firstIndex = selectedIndexes[0];
        var lastIndex = selectedIndexes[^1];
        if (lastIndex - firstIndex + 1 != selectedIndexes.Count)
            return new { error = "The selection skips statements outside the line range; select a contiguous block." };

        var selected = statements.Skip(firstIndex).Take(lastIndex - firstIndex + 1).ToList();

        var owner = containingMethod.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (owner is null) return new { error = "The method is not inside a type declaration." };
        if (model.GetDeclaredSymbol(owner, cancellationToken) is not INamedTypeSymbol ownerSymbol)
            return new { error = "The containing type did not resolve." };

        var extraction = BlockExtraction.TryCreate(
            root, model, containingMethod, owner, ownerSymbol, selected, methodName, cancellationToken, out var refusal);

        if (extraction is null) return new { error = refusal };

        var indentation = BlockExtraction.IndentationOf(containingMethod);
        var newRoot = BlockExtraction.BuildRoot(
            root, owner, containingMethod, block, firstIndex, lastIndex, extraction, indentation);

        var oldText = sourceText.ToString();
        var newText = newRoot.ToFullString();

        // The static checks above cover the shape of the selection; the compiler covers what they
        // cannot see (a missing using for Task, a name that now resolves differently).
        var before = (await document.Project.GetCompilationAsync(cancellationToken))?
            .GetDiagnostics(cancellationToken)
            .Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) ?? 0;

        var projected = document.Project.Solution
            .WithDocumentText(document.Id, SourceText.From(newText))
            .GetProject(document.Project.Id)!;
        var afterDiagnostics = (await projected.GetCompilationAsync(cancellationToken))!
            .GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (afterDiagnostics.Count > before)
            return new
            {
                error = $"Refused: the extraction would add {afterDiagnostics.Count - before} compiler error(s) " +
                        $"({before} → {afterDiagnostics.Count}).",
                errors = afterDiagnostics.Select(diagnostic => $"{diagnostic.Id} {diagnostic.GetMessage()}")
                    .Distinct().Take(8).ToList(),
            };

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "extract_method",
            method = methodName,
            extractedFrom = containingMethod.Identifier.ValueText,
            lines = $"{fromLine}-{toLine}",
            parameters = extraction.Parameters.Select(parameter => $"{parameter.Type} {parameter.Name}").ToList(),
            returns = extraction.ReturnType,
            async = extraction.IsAsync,
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before, after = afterDiagnostics.Count },
            preview = RefactoringFileWriter.Diff(oldText, newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    private static bool CoversLines(SyntaxNode node, int fromLine, int toLine)
    {        var span = node.SyntaxTree.GetLineSpan(node.Span);
        return span.StartLinePosition.Line + 1 <= fromLine && span.EndLinePosition.Line + 1 >= toLine;
    }

    private static int StartLine(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int EndLine(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
}
