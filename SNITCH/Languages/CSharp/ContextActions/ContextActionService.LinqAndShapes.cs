using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Snitch.Languages.CSharp.ContextActions;

/// <summary>
/// The last of the ReSharper context-action families the engine can apply deterministically: the two
/// directions of LINQ syntax, a string accumulated in a loop rewritten to a <c>StringBuilder</c>, a
/// chain of same-receiver <c>is</c> checks merged into one pattern, and a static class of extension
/// methods rewritten into a C# 14 extension block.
/// </summary>
public sealed partial class ContextActionService
{
    // ── patterns ───────────────────────────────────────────────────────────────

    /// <summary>'x is A || x is B' → 'x is A or B'; the same for '&amp;&amp;' and 'and'.</summary>
    private static Outcome MergePatterns(Target target)
    {
        var logical = target.Root.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Where(node => node.IsKind(SyntaxKind.LogicalOrExpression) || node.IsKind(SyntaxKind.LogicalAndExpression))
            .Where(node => LineOf(node) <= target.Line && EndLine(node) >= target.Line)
            .OrderByDescending(node => node.Span.Length)
            .FirstOrDefault();

        if (logical is null) return Outcome.Fail("No '||' or '&&' expression on that line.");

        var operands = new List<ExpressionSyntax>();
        Flatten(logical, logical.IsKind(SyntaxKind.LogicalOrExpression), operands);

        if (operands.Count < 2)
            return Outcome.Fail("The condition does not chain at least two checks.");

        var receiver = default(ExpressionSyntax);
        var patterns = new List<string>();

        foreach (var operand in operands)
        {
            // 'x is T' parses as a type test, 'x is 5 or 6' as a pattern; both are the same check here.
            var (tested, pattern) = operand switch
            {
                IsPatternExpressionSyntax isPattern => (isPattern.Expression, isPattern.Pattern),
                BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.IsExpression)
                    => (binary.Left, binary.Right),
                _ => (default(ExpressionSyntax), default(SyntaxNode)),
            };

            if (tested is null || pattern is null)
                return Outcome.Fail("Every operand of the chain must be an 'is' pattern check.");
            if (pattern is VarPatternSyntax or DiscardPatternSyntax)
                return Outcome.Fail("A 'var' or discard pattern only type-tests; merging would change what it binds.");

            if (receiver is null) receiver = tested;
            else if (receiver.ToString() != tested.ToString())
                return Outcome.Fail("The checks test different expressions, so one pattern cannot express them.");

            patterns.Add(pattern.ToString());
        }

        var keyword = logical.IsKind(SyntaxKind.LogicalOrExpression) ? "or" : "and";
        var merged = $"{receiver} is {string.Join($" {keyword} ", patterns)}";

        return new Outcome(
            ReplaceSpan(target, logical.Span, merged),
            [$"merged {operands.Count} checks into one '{keyword}' pattern"]);
    }

    private static void Flatten(ExpressionSyntax expression, bool or, List<ExpressionSyntax> operands)
    {
        if (expression is BinaryExpressionSyntax binary &&
            binary.IsKind(or ? SyntaxKind.LogicalOrExpression : SyntaxKind.LogicalAndExpression))
        {
            Flatten(binary.Left, or, operands);
            Flatten(binary.Right, or, operands);
            return;
        }

        // A parenthesised group of the *same* operator is part of the same merge; a group of the other
        // operator is a different subtree and must not be folded into this one.
        if (expression is ParenthesizedExpressionSyntax parenthesized &&
            parenthesized.Expression is BinaryExpressionSyntax nested &&
            nested.IsKind(or ? SyntaxKind.LogicalOrExpression : SyntaxKind.LogicalAndExpression))
        {
            Flatten(parenthesized.Expression, or, operands);
            return;
        }

        operands.Add(expression);
    }

    // ── LINQ ───────────────────────────────────────────────────────────────────

    /// <summary>'from x in source where c orderby k select s' → 'source.Where(x => c).OrderBy(x => k).Select(x => s)'.</summary>
    private static Outcome LinqQueryToMethod(Target target)
    {
        var query = target.Root.DescendantNodes().OfType<QueryExpressionSyntax>()
            .Where(node => LineOf(node) <= target.Line && EndLine(node) >= target.Line)
            .OrderBy(node => node.Span.Length)
            .FirstOrDefault();

        if (query is null) return Outcome.Fail("No query expression ('from … in …') on that line.");

        var body = query.Body;
        if (body.SelectOrGroup is not SelectClauseSyntax select)
            return Outcome.Fail("The query ends with 'group … by …': a grouping has no single method-syntax equivalent here.");

        foreach (var clause in body.Clauses)
        {
            var unsupported = clause switch
            {
                JoinClauseSyntax => "join",
                LetClauseSyntax => "let",
                FromClauseSyntax => "a second 'from'",
                _ => null,
            };

            if (unsupported is not null)
                return Outcome.Fail(
                    $"The query uses '{unsupported}'. The translation covers 'from', 'where', 'orderby' and 'select'; " +
                    "the other clause shapes need a generated type, and a wrong translation would be worse than none.");
        }

        if (body.Clauses.OfType<JoinClauseSyntax>().Any() || body.Continuation is not null)
            return Outcome.Fail("The query continues with 'into' or 'join', which this action does not translate.");

        var range = query.FromClause.Identifier.ValueText;
        var chain = new System.Text.StringBuilder(Render(query.FromClause.Expression));
        var calls = 1; // the Select at the end

        foreach (var clause in body.Clauses)
        {
            switch (clause)
            {
                case WhereClauseSyntax where:
                    chain.Append($".Where({range} => {where.Condition})");
                    calls++;
                    break;

                case OrderByClauseSyntax order:
                    var first = true;
                    foreach (var ordering in order.Orderings)
                    {
                        var name = first
                            ? (ordering.IsKind(SyntaxKind.DescendingOrdering) ? "OrderByDescending" : "OrderBy")
                            : (ordering.IsKind(SyntaxKind.DescendingOrdering) ? "ThenByDescending" : "ThenBy");
                        chain.Append($".{name}({range} => {ordering.Expression})");
                        first = false;
                        calls++;
                    }

                    break;
            }
        }

        chain.Append($".Select({range} => {select.Expression})");

        return new Outcome(
            ReplaceSpan(target, query.Span, chain.ToString()),
            [$"translated the query into {calls} method calls"]);
    }

    /// <summary>The inverse: a Where/OrderBy/ThenBy/Select chain over one source becomes a query expression.</summary>
    private static Outcome LinqMethodToQuery(Target target)
    {
        var invocation = target.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(node => LineOf(node) <= target.Line && EndLine(node) >= target.Line)
            .OrderByDescending(node => node.Span.End)
            .FirstOrDefault(node => TryUnwind(node, out _));

        if (invocation is null) return Outcome.Fail("No Where/OrderBy/Select chain on that line.");

        TryUnwind(invocation, out var steps);
        var select = steps.LastOrDefault(step => step.Name == "Select");
        if (select.Name is null)
            return Outcome.Fail("The chain has no 'Select': a query expression needs one.");

        var where = steps.Where(step => step.Name == "Where").ToList();
        var ordering = steps.Where(step => step.Name is "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending").ToList();
        var others = steps.Count - where.Count - ordering.Count;
        if (others != 1)
            return Outcome.Fail("The chain contains calls that are not Where/OrderBy/ThenBy/Select over a single source.");

        var source = steps[0].Source;
        var newLine = NewLineOf(target.Text.ToString());
        var indentation = IndentationAt(target.Text, LineOf(invocation));
        var parameter = select.Parameter ?? "item";

        var lines = new List<string> { $"from {parameter} in {Render(source)}" };
        lines.AddRange(where.Select(clause => $"where {clause.Body}"));
        if (ordering.Count > 0)
        {
            var order = string.Join(", ", ordering.Select(step =>
                step.Name is "OrderByDescending" or "ThenByDescending"
                    ? $"{step.Body} descending"
                    : step.Body));
            lines.Add($"orderby {order}");
        }

        lines.Add($"select {select.Body}");

        var replacement = string.Join(newLine, lines.Select(line => indentation + line));
        return new Outcome(
            ReplaceSpan(target, invocation.Span, replacement),
            [$"translated {steps.Count} method calls into a query expression"]);
    }

    private readonly record struct ChainStep(string Name, string? Parameter, string Body, ExpressionSyntax Source);

    /// <summary>
    /// Unwinds <c>source.Where(…).Select(…)</c> from the outermost call down to the source: each step is
    /// accepted only when it is one of the four methods and its argument is a single-parameter lambda
    /// whose body is an expression.
    /// </summary>
    private static bool TryUnwind(InvocationExpressionSyntax invocation, out List<ChainStep> steps)
    {
        steps = [];

        while (true)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: var name } access)
                return false;
            if (name is not ("Where" or "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending" or "Select"))
                return false;
            if (invocation.ArgumentList.Arguments.Count != 1)
                return false;

            var argument = invocation.ArgumentList.Arguments[0].Expression;
            var (parameter, body) = argument switch
            {
                SimpleLambdaExpressionSyntax simple when simple.Body is ExpressionSyntax expression
                    => (simple.Parameter.Identifier.ValueText, expression.ToString()),
                ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } parenthesized
                    when parenthesized.Body is ExpressionSyntax expression
                    => (parenthesized.ParameterList.Parameters[0].Identifier.ValueText, expression.ToString()),
                _ => (null, null),
            };

            if (parameter is null || body is null) return false;
            steps.Add(new ChainStep(name, parameter, body, access.Expression));

            if (access.Expression is InvocationExpressionSyntax next)
            {
                invocation = next;
                continue;
            }

            break;
        }

        if (steps.Count == 0) return false;

        // The step list is outermost-first; the query expression writes them source-first.
        steps.Reverse();

        // One parameter name for the whole query, which is what a query expression has.
        var names = steps.Select(step => step.Parameter).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count != 1)
            return false;

        return true;
    }

    private static string Render(ExpressionSyntax expression) =>
        expression is BinaryExpressionSyntax or ConditionalExpressionSyntax or AssignmentExpressionSyntax
            or LambdaExpressionSyntax
            ? "(" + expression + ")"
            : expression.ToString();

    // ── string concatenation in a loop ─────────────────────────────────────────

    /// <summary>
    /// A string local that is built with <c>+=</c> becomes a <c>StringBuilder</c>: the declaration, every
    /// append and every remaining read of the variable in the same block are rewritten together, so the
    /// edit never leaves half of the change behind.
    /// </summary>
    private static Outcome ConcatToStringBuilder(Target target)
    {
        var declaration = target.Root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
            .Where(node => LineOf(node) == target.Line)
            .OrderBy(node => node.SpanStart)
            .FirstOrDefault();

        if (declaration is null) return Outcome.Fail("No local declaration on that line.");
        if (declaration.Declaration.Variables.Count != 1)
            return Outcome.Fail("The declaration has several declarators; split it first (split_declaration).");

        var declarator = declaration.Declaration.Variables[0];
        if (declarator.Initializer?.Value is not LiteralExpressionSyntax literal ||
            !literal.IsKind(SyntaxKind.StringLiteralExpression))
            return Outcome.Fail("The variable must start from a string literal: that is what says it is a string being built.");

        var model = target.Model;
        if (model.GetDeclaredSymbol(declarator) is not ILocalSymbol local)
            return Outcome.Fail("The declaration could not be resolved.");

        if (local.Type.SpecialType != SpecialType.System_String)
            return Outcome.Fail($"'{local.Name}' is a {local.Type.Name}, not a string.");

        var block = declaration.FirstAncestorOrSelf<BlockSyntax>();
        if (block is null) return Outcome.Fail("The declaration is not inside a block.");

        var appends = block.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(node => node.IsKind(SyntaxKind.AddAssignmentExpression) &&
                           node.Parent is ExpressionStatementSyntax &&
                           SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(node.Left).Symbol, local))
            .ToList();

        if (appends.Count == 0)
            return Outcome.Fail($"'{local.Name}' is never appended to with '+=' in its block: there is nothing to convert.");

        var edits = new List<(TextSpan Span, string Text)>
        {
            (declaration.Span, $"var {local.Name} = new System.Text.StringBuilder();"),
        };

        foreach (var append in appends)
            edits.Add((append.Parent!.Span, $"{local.Name}.Append({append.Right});"));

        var appendSpans = appends.Select(append => append.Parent!.Span).ToList();
        foreach (var identifier in block.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Identifier.ValueText != local.Name) continue;
            if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, local)) continue;
            if (appendSpans.Any(span => span.Contains(identifier.Span))) continue;
            if (declaration.Span.Contains(identifier.Span)) continue;

            edits.Add((identifier.Span, $"{local.Name}.ToString()"));
        }

        var text = target.Text.ToString();
        var builder = new System.Text.StringBuilder(text);
        var applied = 0;
        foreach (var (span, replacement) in edits.OrderByDescending(edit => edit.Span.Start))
        {
            builder.Remove(span.Start, span.Length);
            builder.Insert(span.Start, replacement);
            applied++;
        }

        return new Outcome(
            builder.ToString(),
            [$"replaced the declaration and {appends.Count} append(s) ({applied} edit(s) in the block)"]);
    }

    // ── static class of extension methods → C# 14 extension block ──────────────

    /// <summary>
    /// '<c>static class E { public static int M(this string s) … }</c>' becomes an
    /// <c>extension(string s) { public int M() … }</c> block: same members, same call sites, the receiver
    /// declared once instead of on every signature.
    /// </summary>
    private static Outcome ConvertToExtensionBlock(Target target)
    {
        var declaration = target.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(node => LineOf(node) <= target.Line && EndLine(node) >= target.Line)
            .OrderBy(node => node.Span.Length)
            .FirstOrDefault();

        if (declaration is null) return Outcome.Fail("No class declaration contains that line.");
        if (!declaration.Modifiers.Any(SyntaxKind.StaticKeyword))
            return Outcome.Fail("The class is not static: an extension block must live in a static class.");

        var model = target.Model;
        var convertible = new List<MethodDeclarationSyntax>();
        foreach (var method in declaration.Members.OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(SyntaxKind.StaticKeyword)) continue;
            if (method.ParameterList.Parameters.Count == 0) continue;
            if (!method.ParameterList.Parameters[0].Modifiers.Any(SyntaxKind.ThisKeyword)) continue;
            if (method.TypeParameterList is not null) continue; // a generic receiver needs the block's type parameters
            convertible.Add(method);
        }

        if (convertible.Count == 0)
            return Outcome.Fail(
                "The static class declares no extension method with a 'this' parameter (a generic one is refused: " +
                "its type parameters belong on the block).");

        var newLine = NewLineOf(target.Text.ToString());
        var indentation = IndentationAt(target.Text, LineOf(declaration));
        var memberIndent = indentation + "    ";

        // One block per receiver: the type *and* the parameter name, because the name is what the block
        // declares and what every body inside it refers to.
        var groups = convertible
            .GroupBy(method => (
                Type: method.ParameterList.Parameters[0].Type!.ToString(),
                Name: method.ParameterList.Parameters[0].Identifier.ValueText))
            .ToList();

        var kept = declaration.Members
            .Where(member => member is not MethodDeclarationSyntax method || !convertible.Contains(method))
            .Select(member => member.Span)
            .ToList();

        var source = target.Text.ToString();
        var body = new System.Text.StringBuilder();

        foreach (var span in kept)
            body.Append(memberIndent).Append(Indented(source[span.Start..span.End], memberIndent, newLine)).Append(newLine).Append(newLine);

        foreach (var group in groups)
        {
            body.Append(memberIndent).Append($"extension({group.Key.Type} {group.Key.Name})").Append(newLine);
            body.Append(memberIndent).Append('{').Append(newLine);

            foreach (var method in group)
            {
                var stripped = method
                    .WithModifiers(SyntaxFactory.TokenList(
                        method.Modifiers.Where(token => !token.IsKind(SyntaxKind.StaticKeyword))))
                    .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
                        method.ParameterList.Parameters.Skip(1).Select(parameter =>
                            parameter.WithModifiers(SyntaxFactory.TokenList(
                                parameter.Modifiers.Where(token => !token.IsKind(SyntaxKind.ThisKeyword))))))))
                    .WithoutTrivia();

                body.Append(memberIndent).Append("    ")
                    .Append(Indented(stripped.NormalizeWhitespace().ToFullString(), memberIndent + "    ", newLine))
                    .Append(newLine).Append(newLine);
            }

            body.Append(memberIndent).Append('}').Append(newLine).Append(newLine);
        }

        var modifiers = string.Join(" ", declaration.Modifiers.Select(token => token.Text));
        var header = $"{(modifiers.Length > 0 ? modifiers + " " : string.Empty)}class {declaration.Identifier.Text}"
                     + (declaration.TypeParameterList is { } parameters ? parameters.ToString() : string.Empty);

        var replacement = new System.Text.StringBuilder()
            .Append(header).Append(newLine)
            .Append(indentation).Append('{').Append(newLine)
            .Append(body.ToString().TrimEnd()).Append(newLine)
            .Append(indentation).Append('}')
            .ToString();

        return new Outcome(
            ReplaceSpan(target, declaration.Span, replacement),
            [$"rewrote {convertible.Count} extension method(s) into {groups.Count} extension block(s)"]);
    }

    /// <summary>Re-indents a member taken from the source so it sits at the given indentation.</summary>
    private static string Indented(string text, string indentation, string newLine)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var common = lines
            .Where(line => line.Trim().Length > 0)
            .Select(line => line.Length - line.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();

        return string.Join(newLine, lines.Select(line =>
            line.Trim().Length == 0 ? string.Empty : indentation + line[Math.Min(common, line.Length)..]));
    }
}
