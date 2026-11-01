using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// Rider's "Extract method" for Visual Basic, narrowed to the case that can be proven
/// behaviour-preserving: a contiguous statement block that reads (but never writes) the locals and
/// parameters around it, contains no jump, and optionally ends with a single <c>Return expression</c>.
/// The block keeps its own text and simply becomes the body of a new private <c>Sub</c> or
/// <c>Function</c>; the call that replaces it names the values it now takes.
/// </summary>
internal static class VbExtractMethod
{
    public static async Task<object> RunAsync(
        IRoslynWorkspace workspace,
        Document document,
        int fromLine,
        int toLine,
        string methodName,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (fromLine < 1 || toLine < fromLine) return new { error = "A valid 1-based line range is required." };
        if (!SyntaxFacts.IsValidIdentifier(methodName))
            return new { error = $"'{methodName}' is not a valid Visual Basic method name." };

        var text = await document.GetTextAsync(cancellationToken);
        if (toLine > text.Lines.Count) return new { error = $"Line {toLine} is out of range." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null)
            return new { error = $"Could not read the syntax tree of {Path.GetFileName(document.FilePath)}." };

        // ── the enclosing method and the selected statements ─────────────────────────────────
        var method = root.DescendantNodes().OfType<MethodBlockSyntax>()
            .Where(block => CoversLines(block, fromLine, toLine))
            .OrderBy(block => block.Span.Length)
            .FirstOrDefault();

        if (method is null) return new { error = $"No method body encloses lines {fromLine}-{toLine}." };

        var selected = method.Statements
            .Select((statement, index) => (statement, index))
            .Where(pair => StartLine(pair.statement) >= fromLine && EndLine(pair.statement) <= toLine)
            .ToList();

        if (selected.Count == 0)
            return new { error = $"No statements of '{method.SubOrFunctionStatement.Identifier.ValueText}' fall inside lines {fromLine}-{toLine}." };

        var firstIndex = selected[0].index;
        var lastIndex = selected[^1].index;
        if (lastIndex - firstIndex + 1 != selected.Count)
            return new { error = "The selection skips statements outside the line range; select a contiguous block." };

        var block = new List<StatementSyntax>();
        for (var index = firstIndex; index <= lastIndex; index++) block.Add(method.Statements[index]);

        var first = block[0];
        var last = block[^1];

        // ── shape: no jump, and the only Return is the last statement ────────────────────────
        foreach (var statement in block)
        {
            var jumps = statement.DescendantNodesAndSelf().Any(node =>
                node is ExitStatementSyntax or ContinueStatementSyntax or GoToStatementSyntax or
                YieldStatementSyntax or OnErrorGoToStatementSyntax or OnErrorResumeNextStatementSyntax or
                ResumeStatementSyntax);

            if (jumps)
                return new { error = "The selected block jumps out of itself (Exit, Continue, GoTo, On Error or Yield); move that statement out first." };

            if (statement is ReturnStatementSyntax && statement != last)
                return new { error = "The selected block returns in the middle; the extraction would change when the method exits." };
        }

        var returns = last is ReturnStatementSyntax { Expression: { } returned } ? returned : null;
        if (last is ReturnStatementSyntax && returns is null)
            return new { error = "The last statement is a Return with no expression; give the new method a value to return." };

        // ── what the block reads from around it ──────────────────────────────────────────────
        var watched = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var node in block.SelectMany(statement => statement.DescendantNodesAndSelf()).OfType<IdentifierNameSyntax>())
        {
            if (model.GetSymbolInfo(node, cancellationToken).Symbol is not { } symbol) continue;
            if (symbol is not (ILocalSymbol or IParameterSymbol)) continue;

            watched.Add(symbol);
        }

        // A local the block declares itself is not a parameter; it only disqualifies the extraction when
        // the code after the block still reads it.
        var parameters = new List<ISymbol>();
        var declaredInside = new List<ISymbol>();

        foreach (var symbol in watched)
        {
            var declaration = symbol.Locations.FirstOrDefault(location => location.IsInSource)?.SourceSpan;
            if (declaration is { } span && span.Start >= first.SpanStart && span.End <= last.Span.End)
                declaredInside.Add(symbol);
            else
                parameters.Add(symbol);
        }

        foreach (var local in declaredInside)
        {
            if (local is not ILocalSymbol declarationLocal) continue;

            if (declarationLocal.IsStatic)
                return new { error = $"'{declarationLocal.Name}' is a Static local; moving it into a method would give it a new lifetime." };

            if (await UsedAfterAsync(local, method, last, document, cancellationToken))
                return new { error = $"'{declarationLocal.Name}' is declared inside the block and read after it; select a wider range." };
        }

        foreach (var parameter in parameters)
        {
            if (IsWrittenInside(parameter, block, model, cancellationToken))
                return new
                {
                    error = $"The block writes '{parameter.Name}', which it only received; an extraction would lose the write.",
                };
        }

        // ── the new method and the call that replaces the block ──────────────────────────────
        var indent = IndentationOf(text, method.SpanStart);
        var newline = NewlineOf(text);
        var ordered = parameters
            .OrderBy(parameter => parameter.Locations.FirstOrDefault(location => location.IsInSource)?.SourceSpan.Start ?? int.MaxValue)
            .ToList();

        var parameterText = string.Join(", ", ordered.Select(parameter =>
            $"{parameter.Name} As {TypeOf(parameter, model, method.SpanStart)}"));

        var returnType = returns is null
            ? null
            : model.GetTypeInfo(returns, cancellationToken).Type?.ToMinimalDisplayString(
                model, method.SpanStart, SymbolDisplayFormat.MinimallyQualifiedFormat);

        if (returns is not null && returnType is null)
            return new { error = "The returned expression has no inferred type; cast it so the new method can declare one." };

        var signature = returnType is null
            ? $"Private Sub {methodName}({parameterText})"
            : $"Private Function {methodName}({parameterText}) As {returnType}";

        var body = text.ToString(TextSpan.FromBounds(first.SpanStart, last.Span.End));

        var replacement = new StringBuilder();
        replacement.Append(indent).Append(signature)
            .Append(newline)
            .Append(indent).Append("    ").Append(body)
            .Append(newline)
            .Append(indent).Append(returnType is null ? "End Sub" : "End Function");

        var call = $"{methodName}({string.Join(", ", ordered.Select(parameter => parameter.Name))})";
        var callText = returns is null ? call : $"Return {call}";

        // The new method goes right after the one it was extracted from, still inside the type.
        var changes = new List<TextChange>
        {
            new(TextSpan.FromBounds(first.SpanStart, last.Span.End), callText),
            new(new TextSpan(method.Span.End, 0), $"{newline}{newline}{replacement}"),
        };

        var newText = text.WithChanges(changes).ToString();
        var after = document.Project.Solution.WithDocumentText(document.Id, SourceText.From(newText, text.Encoding));
        var gate = await RefactoringCompilerGate.CompareAsync(
            document.Project.Solution, after, [document.Project.Id], cancellationToken);

        if (!gate.Passed)
            return new
            {
                error = $"Refused: the extraction would add {gate.Added} compiler error(s) " +
                        $"({gate.Before} -> {gate.After}).",
                errors = gate.NewErrors.Count > 0 ? gate.NewErrors : gate.Errors,
                preview = RefactoringFileWriter.Diff(text.ToString(), newText),
            };

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "extract_method",
            language = "Visual Basic",
            method = methodName,
            extractedFrom = method.SubOrFunctionStatement.Identifier.ValueText,
            lines = $"{fromLine}-{toLine}",
            parameters = ordered.Select(parameter => $"{parameter.Name} As {TypeOf(parameter, model, method.SpanStart)}").ToList(),
            returns = returnType,
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = gate.Before, after = gate.After },
            preview = RefactoringFileWriter.Diff(text.ToString(), newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    // Placeholder so the identifier walk above reads as one filter; the write test needs the block.
    private const bool IsWritten = false;

    private static string TypeOf(ISymbol symbol, SemanticModel model, int position) => symbol switch
    {
        ILocalSymbol local => local.Type.ToMinimalDisplayString(model, position, SymbolDisplayFormat.MinimallyQualifiedFormat),
        IParameterSymbol parameter => parameter.Type.ToMinimalDisplayString(model, position, SymbolDisplayFormat.MinimallyQualifiedFormat),
        IFieldSymbol field => field.Type.ToMinimalDisplayString(model, position, SymbolDisplayFormat.MinimallyQualifiedFormat),
        IPropertySymbol property => property.Type.ToMinimalDisplayString(model, position, SymbolDisplayFormat.MinimallyQualifiedFormat),
        _ => "Object",
    };

    private static bool IsWrittenInside(
        ISymbol symbol, IReadOnlyList<StatementSyntax> block, SemanticModel model, CancellationToken cancellationToken)
    {
        foreach (var statement in block)
        {
            foreach (var assignment in statement.DescendantNodesAndSelf().OfType<AssignmentStatementSyntax>())
            {
                if (model.GetSymbolInfo(assignment.Left, cancellationToken).Symbol is not { } written) continue;
                if (SymbolEqualityComparer.Default.Equals(written, symbol)) return true;
            }
        }

        return false;
    }

    private static async Task<bool> UsedAfterAsync(
        ISymbol symbol, MethodBlockSyntax method, StatementSyntax last, Document document, CancellationToken cancellationToken)
    {
        foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, document.Project.Solution, cancellationToken))
        {
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                if (location.Document.Id != document.Id) continue;
                if (location.Location.SourceSpan.Start > last.Span.End) return true;
            }
        }

        return false;
    }

    private static bool CoversLines(SyntaxNode node, int fromLine, int toLine)
    {
        var span = node.SyntaxTree.GetLineSpan(node.Span);
        return span.StartLinePosition.Line + 1 <= fromLine && span.EndLinePosition.Line + 1 >= toLine;
    }

    private static int StartLine(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int EndLine(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

    private static string IndentationOf(SourceText text, int position)
    {
        var line = text.Lines.GetLineFromPosition(position);
        var prefix = text.ToString(TextSpan.FromBounds(line.Start, position));
        return prefix.All(character => character is ' ' or '\t') ? prefix : string.Empty;
    }

    private static string NewlineOf(SourceText text) =>
        text.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}
