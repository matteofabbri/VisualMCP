using Microsoft.CodeAnalysis;
using Snitch.Languages.VB;
using Sy = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using VbKind = Microsoft.CodeAnalysis.VisualBasic.SyntaxKind;

namespace Snitch.Languages.CSharp.Analysis.Complexity;

/// <summary>
/// The Visual Basic worklist: the moves that would lower a VB method's score. Naming a long boolean in
/// its own <c>Function</c> and flattening a tail <c>If</c> into a guard clause are the two the reducer
/// can apply on its own; extracting a block into a method is reported with the reason it is manual —
/// VB's <c>Sub</c>/<c>Function</c> body has no standalone block to move, so the move is a hand edit.
/// </summary>
public static partial class ComplexityPlanner
{
    private static IReadOnlyList<ComplexitySuggestion> SuggestVisualBasic(
        SyntaxNode unit, SemanticModel model, CancellationToken cancellationToken)
    {
        var suggestions = new List<ComplexitySuggestion>();
        if (unit is not Sy.MethodBlockSyntax method) return suggestions;

        var owner = method.Ancestors().OfType<Sy.TypeBlockSyntax>().FirstOrDefault();
        var hostName = method.SubOrFunctionStatement.Identifier.ValueText;

        AddVisualBasicExtraction(method, suggestions);
        AddVisualBasicConditionExtraction(method, owner, suggestions);
        AddVisualBasicGuardClause(method, hostName, suggestions);
        AddVisualBasicLadder(method, suggestions);

        return suggestions
            .OrderByDescending(suggestion => suggestion.AutoApplicable)
            .ThenByDescending(suggestion => suggestion.CyclomaticReduction + suggestion.CognitiveReduction)
            .ThenBy(suggestion => suggestion.Line)
            .ToList();
    }

    /// <summary>A nested block with decisions inside is the extract-method candidate; VB moves it by hand.</summary>
    private static void AddVisualBasicExtraction(Sy.MethodBlockSyntax method, List<ComplexitySuggestion> suggestions)
    {
        var candidates = new List<(Sy.StatementSyntax Block, int Cyclomatic, int Cognitive, int Start, int End)>();

        foreach (var block in VisualBasicNestedBlocks(method))
        {
            if (block is not Sy.MultiLineIfBlockSyntax and not Sy.ForBlockSyntax
                and not Sy.ForEachBlockSyntax and not Sy.WhileBlockSyntax and not Sy.DoLoopBlockSyntax
                and not Sy.SelectBlockSyntax and not Sy.TryBlockSyntax)
                continue;

            var score = ComplexityAnalyzer.ScoreVisualBasicSubtree(block);
            var decisions = Math.Max(0, score.Cyclomatic - 1);
            if (decisions < 2 && score.Cognitive < 4) continue;

            var span = block.GetLocation().GetLineSpan();
            candidates.Add((block, decisions, score.Cognitive,
                span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1));
        }

        foreach (var candidate in candidates
                     .OrderByDescending(entry => entry.Cyclomatic)
                     .ThenByDescending(entry => entry.Cognitive)
                     .Take(3))
        {
            suggestions.Add(new ComplexitySuggestion(
                "extract-method",
                candidate.Start,
                candidate.End,
                $"Move lines {candidate.Start}-{candidate.End} into their own private method " +
                $"({candidate.Cyclomatic} decision(s) leave this method).",
                candidate.Cyclomatic,
                candidate.Cognitive,
                AutoApplicable: false,
                "Extracting a block into a method is a hand edit in Visual Basic: the reducer only names conditions and flattens guards."));
        }
    }

    /// <summary>A boolean with three or more logical operators is one decision nobody can hold at once.</summary>
    private static void AddVisualBasicConditionExtraction(
        Sy.MethodBlockSyntax method, Sy.TypeBlockSyntax? owner, List<ComplexitySuggestion> suggestions)
    {
        foreach (var condition in VisualBasicConditions(method))
        {
            var operators = CountVisualBasicLogicalOperators(condition);
            if (operators < 3) continue;

            var span = condition.GetLocation().GetLineSpan();
            var startLine = span.StartLinePosition.Line + 1;
            var applicable = owner is not null;

            suggestions.Add(new ComplexitySuggestion(
                "extract-condition",
                startLine,
                span.EndLinePosition.Line + 1,
                $"Name the {operators}-operator condition on line {startLine} in a private Boolean function " +
                $"({operators} point(s) leave this method).",
                operators,
                operators,
                AutoApplicable: applicable,
                applicable ? null : "Only a method inside a class, structure or module can host the extracted condition."));
        }
    }

    /// <summary>
    /// <c>If c Then … End If</c> as the last statement of a <c>Sub</c> becomes
    /// <c>If Not (c) Then Return</c> followed by the body at the method's own level.
    /// </summary>
    private static void AddVisualBasicGuardClause(
        Sy.MethodBlockSyntax method, string hostName, List<ComplexitySuggestion> suggestions)
    {
        if (!method.SubOrFunctionStatement.SubOrFunctionKeyword.IsKind(VbKind.SubKeyword)) return;
        if (method.Statements.Count < 2) return;
        if (method.Statements[^1] is not Sy.MultiLineIfBlockSyntax tail) return;
        if (tail.ElseBlock is not null || tail.ElseIfBlocks.Count > 0) return;

        var inside = ComplexityAnalyzer.ScoreVisualBasicSubtree(tail);
        var decisions = Math.Max(0, inside.Cyclomatic - 1);
        if (decisions < 1) return;

        var span = tail.GetLocation().GetLineSpan();
        var startLine = span.StartLinePosition.Line + 1;
        _ = hostName;

        suggestions.Add(new ComplexitySuggestion(
            "guard-clause",
            startLine,
            span.EndLinePosition.Line + 1,
            $"Flatten the tail 'If' on line {startLine} into 'If Not (…) Then Return' — everything inside loses one nesting level.",
            CyclomaticReduction: 0,
            CognitiveReduction: decisions,
            AutoApplicable: true));
    }

    /// <summary>An <c>ElseIf</c> ladder longer than a <c>Select Case</c> reads as a table instead of a staircase.</summary>
    private static void AddVisualBasicLadder(Sy.MethodBlockSyntax method, List<ComplexitySuggestion> suggestions)
    {
        var ladder = method.Statements.OfType<Sy.MultiLineIfBlockSyntax>().FirstOrDefault();
        if (ladder is null) return;

        var branches = ladder.ElseIfBlocks.Count + 1;
        if (branches < 4) return;

        var span = ladder.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;

        suggestions.Add(new ComplexitySuggestion(
            "split-ladder",
            line,
            span.EndLinePosition.Line + 1,
            $"The If/ElseIf chain on line {line} has {branches} branches; a Select Case (or a dictionary of handlers) reads as a table.",
            CyclomaticReduction: 0,
            CognitiveReduction: branches,
            AutoApplicable: false,
            "Rewriting an If/ElseIf ladder into a Select Case changes the shape of the arms; do this one by hand."));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Every block statement of the method, without the inside of a nested lambda or method: those are
    /// their own units and their decisions do not leave the host when a block moves out.
    /// </summary>
    private static IEnumerable<Sy.StatementSyntax> VisualBasicNestedBlocks(Sy.MethodBlockSyntax method) =>
        VbSyntaxScope.OwnNodes(method)
            .Where(node => node is Sy.MultiLineIfBlockSyntax or Sy.ForBlockSyntax or Sy.ForEachBlockSyntax
                or Sy.WhileBlockSyntax or Sy.DoLoopBlockSyntax or Sy.SelectBlockSyntax or Sy.TryBlockSyntax)
            .Cast<Sy.StatementSyntax>();

    private static IEnumerable<Sy.ExpressionSyntax> VisualBasicConditions(Sy.MethodBlockSyntax method)
    {
        foreach (var node in VbSyntaxScope.OwnNodes(method))
        {
            switch (node)
            {
                case Sy.MultiLineIfBlockSyntax @if:
                    yield return @if.IfStatement.Condition;
                    foreach (var elseIf in @if.ElseIfBlocks)
                        yield return elseIf.ElseIfStatement.Condition;
                    break;
                case Sy.SingleLineIfStatementSyntax @if:
                    yield return @if.Condition;
                    break;
                case Sy.WhileBlockSyntax @while:
                    yield return @while.WhileStatement.Condition;
                    break;
                case Sy.DoLoopBlockSyntax @do:
                {
                    if (@do.DoStatement.WhileOrUntilClause is { } pre) yield return pre.Condition;
                    if (@do.LoopStatement.WhileOrUntilClause is { } post) yield return post.Condition;
                    break;
                }
            }
        }
    }

    private static int CountVisualBasicLogicalOperators(Sy.ExpressionSyntax condition) =>
        VbSyntaxScope.OwnNodesAndSelf(condition)
            .Count(node => node is Sy.BinaryExpressionSyntax binary &&
                           (binary.IsKind(VbKind.AndAlsoExpression) || binary.IsKind(VbKind.OrElseExpression)));
}
