using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace Snitch.Languages.CSharp.ContextActions;

/// <summary>
/// The proprietary tail of the context-action catalogue, implemented where Roslyn can decide it: the
/// static-member import family (import, qualify, unqualify), the quote and split/join string family, the
/// span index guard, and the two body-shape conversions.
/// </summary>
public sealed partial class ContextActionService
{
    // ── Import actions ───────────────────────────────────────────────────────────────────────────

    /// <summary>Adds <c>using static &lt;type&gt;</c> for the type named in <c>text</c>.</summary>
    private Outcome ImportStaticMember(Target target, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Outcome.Fail("import_static_member needs the type in text, e.g. 'System.Math'.");

        var type = ResolveType(target.Compilation, text.Trim());
        if (type is null) return Outcome.Fail($"'{text.Trim()}' does not resolve to a type in this compilation.");

        var full = FullName(type);
        var updated = InsertUsingStatic(target.Text.ToString(), full, out var added);
        if (!added) return Outcome.Fail($"'using static {full};' is already imported.");

        return new Outcome(updated, [$"adds 'using static {full};'"]);
    }

    /// <summary>Qualifies the first unqualified static member on the line with its containing type.</summary>
    private Outcome QualifyMember(Target target)
    {
        foreach (var identifier in target.Root.DescendantNodes()
                     .OfType<IdentifierNameSyntax>()
                     .Where(node => OnSelectedLine(target, node)))
        {
            if (identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier) continue;

            // A method-group identifier resolves on the invocation, so the candidate is the answer here.
            var info = target.Model.GetSymbolInfo(identifier);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (symbol is not (IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol)) continue;
            if (symbol.ContainingType is null || !symbol.IsStatic) continue;

            // The source is unqualified here (that is the condition), so qualifying is the edit. The full
            // name always binds: a 'using static' imports the members, not the type name.
            var qualifier = FullName(symbol.ContainingType);
            var text = target.Text.ToString();
            var updated = text[..identifier.SpanStart] + qualifier + "." + identifier.Identifier.Text + text[identifier.Span.End..];

            return new Outcome(updated, [$"qualifies '{identifier.Identifier.Text}' as '{qualifier}.{identifier.Identifier.Text}'"]);
        }

        return Outcome.Fail("No unqualified static member on this line could be qualified.");
    }

    /// <summary>Drops the type qualifier from a static member access and imports it with <c>using static</c>.</summary>
    private Outcome UnqualifyMember(Target target)
    {
        var access = target.Root.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .FirstOrDefault(node =>
                node.IsKind(SyntaxKind.SimpleMemberAccessExpression)
                && OnSelectedLine(target, node)
                && target.Model.GetSymbolInfo(node.Expression).Symbol is INamedTypeSymbol);

        if (access is null)
            return Outcome.Fail("No 'Type.Member' access on this line whose receiver is a type.");

        var type = (INamedTypeSymbol)target.Model.GetSymbolInfo(access.Expression).Symbol!;
        var full = FullName(type);

        var text = target.Text.ToString();
        var updated = text[..access.SpanStart] + access.Name + text[access.Span.End..];
        updated = InsertUsingStatic(updated, full, out _);

        return new Outcome(updated, [$"unqualifies '{access}' and adds 'using static {full};'"]);
    }

    // ── Literal/string actions ──────────────────────────────────────────────────────────────────

    /// <summary>Turns the selected lines into one escaped C# string literal.</summary>
    private Outcome WrapInQuotes(Target target)
    {
        var text = target.Text.ToString();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var from = target.Line - 1;
        var to = Math.Min(target.ToLine - 1, lines.Length - 1);

        var selected = string.Join("\n", lines[from..(to + 1)]);
        if (selected.Length == 0) return Outcome.Fail("The selected lines are empty.");

        var trimmedSelection = selected.Trim();
        if (trimmedSelection.Length > 1 && trimmedSelection.StartsWith('"') && trimmedSelection.EndsWith('"'))
            return Outcome.Fail("The selected text is already a string literal.");

        var literal = SymbolDisplay.FormatLiteral(selected, quote: true);
        var start = LineStart(text, target.Line);
        var end = LineEnd(text, target.ToLine);
        var updated = text[..start] + literal + text[end..];

        return new Outcome(updated, ["the selected lines become one escaped string literal"]);
    }

    /// <summary>Splits the string literal on the line into a concatenation across lines.</summary>
    private Outcome SplitString(Target target, string? text)
    {
        var (literal, value) = StringLiteralOnLine(target);
        if (literal is null || value is null) return Outcome.Fail("No string literal on this line.");

        var separator = string.IsNullOrEmpty(text) ? " " : text;
        var parts = value.Split(separator, StringSplitOptions.None);
        if (parts.Length < 2)
            return Outcome.Fail($"The literal does not contain '{separator}': there is nothing to split it at.");

        // The separator belongs to the value, so every part but the last carries it back.
        var pieces = new List<string>();
        for (var index = 0; index < parts.Length; index++)
        {
            var piece = index == parts.Length - 1 ? parts[index] : parts[index] + separator;
            pieces.Add(SymbolDisplay.FormatLiteral(piece, quote: true));
        }

        var indentation = IndentationAt(target.Text, LineOf(literal));
        var newLine = NewLineOf(target.Text.ToString());
        var replacement = string.Join($" +{newLine}{indentation}", pieces);

        var fileText = target.Text.ToString();
        var updated = fileText[..literal.SpanStart] + replacement + fileText[literal.Span.End..];

        return new Outcome(updated, [$"splits the literal into {pieces.Count} parts at '{separator}'"]);
    }

    /// <summary>Joins a concatenation of string literals on the line into one literal.</summary>
    private Outcome JoinString(Target target)
    {
        var concatenation = target.Root.DescendantNodes()
            .OfType<BinaryExpressionSyntax>()
            .FirstOrDefault(node => node.IsKind(SyntaxKind.AddExpression) && OnSelectedLine(target, node));

        if (concatenation is null) return Outcome.Fail("No '+' concatenation on this line.");

        var operands = new List<ExpressionSyntax>();
        Flatten(concatenation, operands);

        if (operands.Count < 2 || operands.Any(operand =>
                operand is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression)))
        {
            return Outcome.Fail("The concatenation is not made only of string literals.");
        }

        var value = string.Concat(operands.Cast<LiteralExpressionSyntax>().Select(literal => literal.Token.ValueText));
        var replacement = SymbolDisplay.FormatLiteral(value, quote: true);
        var text = target.Text.ToString();
        var updated = text[..concatenation.SpanStart] + replacement + text[concatenation.Span.End..];

        return new Outcome(updated, [$"joins {operands.Count} literals into one"]);
    }

    // ── Guard actions ──────────────────────────────────────────────────────────────────────────

    /// <summary>Inserts a bounds guard before the statement that indexes a span or an array on the line.</summary>
    private Outcome CheckSpanIndex(Target target, string? text)
    {
        var elementAccess = target.Root.DescendantNodes()
            .OfType<ElementAccessExpressionSyntax>()
            .FirstOrDefault(node => OnSelectedLine(target, node));

        if (elementAccess is null) return Outcome.Fail("No element access ('span[index]') on this line.");
        if (elementAccess.ArgumentList.Arguments.Count != 1)
            return Outcome.Fail("The element access does not take exactly one index.");

        var statement = elementAccess.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (statement is null) return Outcome.Fail("The element access is not inside a statement.");
        var statementLineStart = statement.SyntaxTree.GetText().Lines
            .GetLineFromPosition(statement.SpanStart).Start;
        if (target.Text.ToString()[statementLineStart..statement.SpanStart].Trim().Length > 0)
            return Outcome.Fail("The statement shares its line with other code; put it on its own line first.");

        var receiverType = target.Model.GetTypeInfo(elementAccess.Expression).Type;
        if (receiverType is null) return Outcome.Fail("The receiver of the element access has no type (it does not compile).");

        var receiver = elementAccess.Expression.ToString();
        var lengthProperty = receiverType.Name == "List" ? "Count" : "Length";

        var index = string.IsNullOrWhiteSpace(text)
            ? elementAccess.ArgumentList.Arguments[0].Expression.ToString()
            : text.Trim();

        var indentation = IndentationAt(target.Text, LineOf(statement));
        var newLine = NewLineOf(target.Text.ToString());
        var indexName = index.All(character => char.IsLetterOrDigit(character) || character is '_' or '.')
            ? $"nameof({index})"
            : "null";

        var guard =
            $"{indentation}if ({index} < 0 || {index} >= {receiver}.{lengthProperty}){newLine}" +
            $"{indentation}{{{newLine}" +
            $"{indentation}    throw new System.ArgumentOutOfRangeException({indexName}, {index}, " +
            $"\"The index is outside {receiver}.\");{newLine}" +
            $"{indentation}}}{newLine}";

        var text0 = target.Text.ToString();
        var insertAt = LineStart(text0, LineOf(statement));
        var updated = text0[..insertAt] + guard + text0[insertAt..];

        return new Outcome(updated,
            [$"guards '{index}' against 0 and {receiver}.{lengthProperty} before {Truncate(statement.ToString())}"]);
    }

    // ── Statement ↔ expression ─────────────────────────────────────────────────────────────────

    /// <summary>A method whose body is one return becomes an expression-bodied method.</summary>
    private Outcome BlockBodyToExpression(Target target)
    {
        var method = target.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Body is not null && OverlapsSelection(target, candidate));

        if (method?.Body is null) return Outcome.Fail("No block-bodied method on this line.");

        var expression = OnlyExpression(method.Body);
        if (expression is null)
            return Outcome.Fail("The body must hold exactly one return to become an expression body.");

        var updated = method
            .WithBody(null)
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(expression.WithoutTrivia()))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithAdditionalAnnotations(Formatter.Annotation);

        var root = Formatter.Format(target.Root.ReplaceNode(method, updated), target.Document.Project.Solution.Workspace);

        return new Outcome(root.ToFullString(), ["the block body becomes an expression body"]);
    }

    /// <summary>An expression-bodied method becomes a block.</summary>
    private Outcome ExpressionBodyToBlock(Target target)
    {
        var method = target.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.ExpressionBody is not null && OverlapsSelection(target, candidate));

        if (method?.ExpressionBody is null) return Outcome.Fail("No expression-bodied method on this line.");

        var returnsValue = !method.ReturnType.ToString().Equals("void", StringComparison.Ordinal);
        StatementSyntax statement = returnsValue
            ? SyntaxFactory.ReturnStatement(method.ExpressionBody.Expression.WithoutTrivia())
            : SyntaxFactory.ExpressionStatement(method.ExpressionBody.Expression.WithoutTrivia());

        var updated = method
            .WithExpressionBody(null)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None))
            .WithBody(SyntaxFactory.Block(statement))
            .WithAdditionalAnnotations(Formatter.Annotation);

        var root = Formatter.Format(target.Root.ReplaceNode(method, updated), target.Document.Project.Solution.Workspace);

        return new Outcome(root.ToFullString(), ["the expression body becomes a block"]);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private static ExpressionSyntax? OnlyExpression(BlockSyntax body)
    {
        if (body.Statements.Count != 1) return null;

        return body.Statements[0] switch
        {
            ReturnStatementSyntax { Expression: { } returned } => returned,
            ExpressionStatementSyntax { Expression: { } expression } => expression,
            _ => null,
        };
    }

    private static void Flatten(ExpressionSyntax expression, List<ExpressionSyntax> operands)
    {
        if (expression is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression))
        {
            Flatten(binary.Left, operands);
            Flatten(binary.Right, operands);
            return;
        }

        operands.Add(expression);
    }

    private static (LiteralExpressionSyntax? Literal, string? Value) StringLiteralOnLine(Target target)
    {
        var literal = target.Root.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .FirstOrDefault(node => node.IsKind(SyntaxKind.StringLiteralExpression) && OnSelectedLine(target, node));

        return (literal, literal?.Token.ValueText);
    }

    /// <summary>Inserts <c>using static …;</c> after the last using directive, when it is not there already.</summary>
    private static string InsertUsingStatic(string text, string fullName, out bool added)
    {
        var declaration = $"using static {fullName};";
        if (text.Contains(declaration, StringComparison.Ordinal))
        {
            added = false;
            return text;
        }

        var newLine = NewLineOf(text);
        var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
        var lastUsing = root.Usings.LastOrDefault();

        added = true;
        if (lastUsing is not null)
        {
            var end = lastUsing.FullSpan.End;
            return text[..end] + newLine + declaration + text[end..];
        }

        // No using yet: put it at the top, after a byte-order mark when the file has one.
        var bom = text.StartsWith('\uFEFF') ? "\uFEFF" : string.Empty;
        var body = bom.Length > 0 ? text[1..] : text;
        return bom + declaration + newLine + newLine + body;
    }

    private static INamedTypeSymbol? ResolveType(Compilation compilation, string name)
    {
        var direct = compilation.GetTypeByMetadataName(name);
        if (direct is not null) return direct;

        var simple = name.Split('.').Last();
        return compilation.GetSymbolsWithName(simple, SymbolFilter.Type)
            .OfType<INamedTypeSymbol>()
            .FirstOrDefault();
    }

    private static string FullName(INamedTypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty, StringComparison.Ordinal);

    /// <summary>Whether the declaration's line range contains any of the selected lines.</summary>
    private static bool OverlapsSelection(Target target, SyntaxNode node) =>
        LineOf(node) <= target.ToLine && EndLine(node) >= target.Line;

    /// <summary>The index of the first character of a 1-based line.</summary>
    private static int LineStart(string text, int line)
    {
        var current = 0;
        for (var index = 0; index < line - 1; index++)
        {
            var newline = text.IndexOf('\n', current);
            if (newline < 0) return text.Length;
            current = newline + 1;
        }

        return current;
    }

    /// <summary>The index just past the newline that ends a 1-based line.</summary>
    private static int LineEnd(string text, int line)
    {
        var start = LineStart(text, line);
        var newline = text.IndexOf('\n', start);
        return newline < 0 ? text.Length : newline + 1;
    }

    private static string Truncate(string value, int limit = 60) =>
        value.Length <= limit ? value : value[..limit] + "…";
}
