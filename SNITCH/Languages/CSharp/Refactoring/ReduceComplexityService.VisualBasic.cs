using Microsoft.CodeAnalysis;
using Snitch.Languages.CSharp.Analysis.Complexity;
using Snitch.Languages.VB;
using Sy = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using VbFactory = Microsoft.CodeAnalysis.VisualBasic.SyntaxFactory;
using VbKind = Microsoft.CodeAnalysis.VisualBasic.SyntaxKind;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// The two moves the reducer applies to Visual Basic: a long boolean becomes a private
/// <c>Function … As Boolean</c>, and a tail <c>If</c> of a <c>Sub</c> becomes a guard clause.
/// <para>
/// Both are text splices on the file's own text — the condition is replaced by the call, the helper is
/// inserted after the host method, the guard replaces the tail <c>If</c> — and the result is re-parsed
/// before it is handed back, so a splice that does not parse is refused instead of written. The
/// compiler gate that follows in the caller is what proves the semantics.
/// </para>
/// </summary>
public sealed partial class ReduceComplexityService
{
    // RS1039 claims GetDeclaredSymbol on a VB type or method block always returns null; it does not
    // (probed against a real VB compilation). The symbol is what names the helper and types it.
#pragma warning disable RS1039
    private static (SyntaxNode? Root, string? Error) ApplyVisualBasicConditionExtraction(
        SyntaxNode root, SemanticModel model, SyntaxNode unit, int startLine, CancellationToken cancellationToken)
    {
        if (unit is not Sy.MethodBlockSyntax host) return (null, "the enclosing unit is not a method with a body");

        var owner = host.Ancestors().OfType<Sy.TypeBlockSyntax>().FirstOrDefault();
        if (owner is null) return (null, "the method is not inside a type declaration");
        if (model.GetDeclaredSymbol(owner, cancellationToken) is not INamedTypeSymbol ownerSymbol)
            return (null, "the containing type did not resolve");

        var condition = VisualBasicConditionAt(host, startLine);
        if (condition is null) return (null, $"no condition starts on line {startLine}");

        var parameters = new List<(string Type, string Name)>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var identifier in condition.DescendantNodesAndSelf().OfType<Sy.IdentifierNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(identifier, cancellationToken).Symbol;
            if (symbol is not ILocalSymbol and not IParameterSymbol) continue;
            if (!seen.Add(symbol)) continue;

            switch (symbol)
            {
                case IParameterSymbol parameter when parameter.RefKind != RefKind.None:
                    return (null, $"a parameter of '{host.SubOrFunctionStatement.Identifier.ValueText}' is ByRef and cannot be forwarded");
                case IParameterSymbol parameter:
                    parameters.Add((parameter.Type.ToMinimalDisplayString(model, condition.SpanStart), parameter.Name));
                    break;
                case ILocalSymbol local when local.Type.TypeKind == TypeKind.Error:
                    return (null, $"local '{local.Name}' has no resolvable type");
                case ILocalSymbol local:
                    parameters.Add((local.Type.ToMinimalDisplayString(model, condition.SpanStart), local.Name));
                    break;
            }
        }

        var helperName = UniqueMemberName(
            ownerSymbol, "Is" + Pascal(host.SubOrFunctionStatement.Identifier.ValueText) + "Condition");

        var call = $"{helperName}({string.Join(", ", parameters.Select(parameter => parameter.Name))})";
        var parameterList = string.Join(", ", parameters.Select(parameter => $"{parameter.Name} As {parameter.Type}"));
        var source = root.SyntaxTree.GetText().ToString();
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var indentation = IndentationOf(host);

        var helper = string.Join(newline,
            $"{indentation}Private Function {helperName}({parameterList}) As Boolean",
            $"{indentation}    Return {OneLine(condition.ToString())}",
            $"{indentation}End Function");

        // The helper goes after the host's End Sub/End Function, so the condition's offsets stay valid.
        var insertAt = EndOfLine(source, host.Span.End);
        var withHelper = source[..insertAt] + newline + helper + source[insertAt..];
        var rewritten = withHelper[..condition.SpanStart] + call + withHelper[condition.Span.End..];

        var newRoot = ParseVisualBasic(rewritten, root.SyntaxTree.Options, out var parseError);
        return newRoot is null ? (null, parseError ?? "the rewrite did not parse") : (newRoot, null);
    }

    private static (SyntaxNode? Root, string? Error) ApplyVisualBasicGuardClause(
        SemanticModel model, SyntaxNode unit, int startLine)
    {
        if (unit is not Sy.MethodBlockSyntax host) return (null, "the enclosing unit is not a method with a body");
        if (!host.SubOrFunctionStatement.SubOrFunctionKeyword.IsKind(VbKind.SubKeyword))
            return (null, "the method returns a value, so losing the 'If' changes what it returns");
        if (host.Statements.Count < 2) return (null, "the method has nothing to flatten");

        if (host.Statements[^1] is not Sy.MultiLineIfBlockSyntax tail || StartLine(tail) != startLine)
            return (null, $"no 'If' is the last statement on line {startLine}");
        if (tail.ElseBlock is not null || tail.ElseIfBlocks.Count > 0)
            return (null, "the 'If' has an else branch");

        if (model.GetDeclaredSymbol(host) is not IMethodSymbol { ReturnsVoid: true })
            return (null, "the method returns a value, so losing the 'If' changes what it returns");

        var source = unit.SyntaxTree.GetText().ToString();
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var ifIndentation = IndentationOf(tail);

        var bodyText = string.Join(newline, tail.Statements.Select(statement => statement.ToFullString()));
        var branchIndentation = tail.Statements.Count == 0 ? ifIndentation + "    " : IndentationOf(tail.Statements[0]);
        var delta = Math.Max(0, branchIndentation.Length - ifIndentation.Length);
        var dedented = Dedent(bodyText, delta).TrimEnd('\r', '\n');

        var guard = string.Join(newline,
            $"{ifIndentation}If Not ({OneLine(tail.IfStatement.Condition.ToString())}) Then",
            $"{ifIndentation}    Return",
            $"{ifIndentation}End If");

        var replacement = tail.Statements.Count == 0 ? guard : guard + newline + dedented;
        var rewritten = source[..tail.SpanStart] + replacement + source[tail.Span.End..];

        var newRoot = ParseVisualBasic(rewritten, unit.SyntaxTree.Options, out var parseError);
        return newRoot is null ? (null, parseError ?? "the rewrite did not parse") : (newRoot, null);
    }
#pragma warning restore RS1039

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Sy.ExpressionSyntax? VisualBasicConditionAt(Sy.MethodBlockSyntax host, int startLine)
    {
        foreach (var node in VbSyntaxScope.OwnNodes(host))
        {
            var condition = node switch
            {
                Sy.MultiLineIfBlockSyntax @if => @if.IfStatement.Condition,
                Sy.SingleLineIfStatementSyntax @if => @if.Condition,
                Sy.WhileBlockSyntax @while => @while.WhileStatement.Condition,
                Sy.DoLoopBlockSyntax @do => @do.DoStatement.WhileOrUntilClause?.Condition
                    ?? @do.LoopStatement.WhileOrUntilClause?.Condition,
                _ => null,
            };

            if (condition is not null && StartLine(condition) == startLine) return condition;
        }

        return null;
    }

    private static SyntaxNode? ParseVisualBasic(string text, ParseOptions options, out string? error)
    {
        var unit = VbFactory.ParseCompilationUnit(text, options: options as Microsoft.CodeAnalysis.VisualBasic.VisualBasicParseOptions);
        var errors = unit.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Take(3)
            .Select(diagnostic => diagnostic.GetMessage())
            .ToList();

        error = errors.Count == 0 ? null : string.Join("; ", errors);
        return errors.Count == 0 ? unit : null;
    }

    /// <summary>The offset just past the line that ends at <paramref name="position"/>.</summary>
    private static int EndOfLine(string text, int position)
    {
        var newline = text.IndexOf('\n', Math.Min(position, text.Length - 1));
        return newline < 0 ? text.Length : newline + 1;
    }

    /// <summary>A multi-line condition collapses onto one line: the helper's <c>Return</c> reads it once.</summary>
    private static string OneLine(string text) =>
        string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
}
