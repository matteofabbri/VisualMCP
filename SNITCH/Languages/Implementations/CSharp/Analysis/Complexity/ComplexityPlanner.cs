using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Languages.CSharp.Refactoring;

namespace Snitch.Languages.CSharp.Analysis.Complexity;

/// <summary>
/// Turns a score into a worklist: the concrete moves that would lower it, ranked by what they buy.
/// Both <c>analyze_complexity</c> (which reports them) and <c>reduce_complexity</c> (which applies
/// them) read this, so the advice and the fix can never disagree.
/// <para>
/// The four moves are the ones professional tooling recommends for a method that is too complex
/// (Sonar S3776 / ReSharper's "extract method", "extract condition", "invert if"): pull a nested
/// block into its own method, name a tangled boolean, flatten a tail <c>if</c> into a guard clause,
/// or split an <c>else-if</c> ladder into a switch.
/// </para>
/// </summary>
public static partial class ComplexityPlanner
{
    /// <summary>Suggestions for one unit, best expected reduction first.</summary>
    public static IReadOnlyList<ComplexitySuggestion> Suggest(
        SyntaxNode unit,
        SemanticModel model,
        CancellationToken cancellationToken = default)
    {
        if (unit.SyntaxTree.Options.Language == LanguageNames.VisualBasic)
            return SuggestVisualBasic(unit, model, cancellationToken);

        var suggestions = new List<ComplexitySuggestion>();
        if (BodyBlock(unit) is not { } body) return suggestions;

        var host = unit as MethodDeclarationSyntax;
        var owner = unit.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var ownerSymbol = owner is null ? null : model.GetDeclaredSymbol(owner, cancellationToken) as INamedTypeSymbol;

        AddExtraction(unit, body, host, owner, ownerSymbol, model, suggestions, cancellationToken);
        AddConditionExtraction(unit, body, host, owner, suggestions);
        AddGuardClause(unit, body, host, model, suggestions, cancellationToken);
        AddSplitLadder(body, suggestions);

        return suggestions
            .OrderByDescending(suggestion => suggestion.AutoApplicable)
            .ThenByDescending(suggestion => suggestion.CyclomaticReduction + suggestion.CognitiveReduction)
            .ThenBy(suggestion => suggestion.Line)
            .ToList();
    }

    /// <summary>The body block of a unit, or null for an expression-bodied one.</summary>
    public static BlockSyntax? BodyBlock(SyntaxNode unit) => unit switch
    {
        BaseMethodDeclarationSyntax method => method.Body,
        LocalFunctionStatementSyntax local => local.Body,
        AccessorDeclarationSyntax accessor => accessor.Body,
        AnonymousFunctionExpressionSyntax lambda => lambda.Block,
        _ => null,
    };

    /// <summary>
    /// The highest-value block that can become its own method. Nested blocks only: extracting the
    /// whole body would just rename the method.
    /// </summary>
    private static void AddExtraction(
        SyntaxNode unit,
        BlockSyntax body,
        MethodDeclarationSyntax? host,
        TypeDeclarationSyntax? owner,
        INamedTypeSymbol? ownerSymbol,
        SemanticModel model,
        List<ComplexitySuggestion> suggestions,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(BlockSyntax Block, int Cyclomatic, int Cognitive)>();

        foreach (var block in OwnBlocks(body))
        {
            if (block.Statements.Count < 2) continue;

            var score = ComplexityAnalyzer.ScoreSubtree(block);
            var decisions = Math.Max(0, score.Cyclomatic - 1);
            if (decisions < 2 && score.Cognitive < 4) continue;

            candidates.Add((block, decisions, score.Cognitive));
        }

        foreach (var candidate in candidates
                     .OrderByDescending(entry => entry.Cyclomatic)
                     .ThenByDescending(entry => entry.Cognitive)
                     .Take(3))
        {
            var span = candidate.Block.GetLocation().GetLineSpan();
            var startLine = span.StartLinePosition.Line + 1;
            var endLine = span.EndLinePosition.Line + 1;

            if (host is null || owner is null || ownerSymbol is null)
            {
                suggestions.Add(new ComplexitySuggestion(
                    "extract-method", startLine, endLine,
                    $"Move lines {startLine}-{endLine} into their own private method ({candidate.Cyclomatic} decision(s) leave this method).",
                    candidate.Cyclomatic, candidate.Cognitive, AutoApplicable: false,
                    "Only a method can host an extracted method; this code lives in a lambda or a local function."));
                continue;
            }

            var extraction = BlockExtraction.TryCreate(
                unit.SyntaxTree.GetRoot(cancellationToken),
                model, host, owner, ownerSymbol,
                candidate.Block.Statements.ToList(), "Extracted", cancellationToken, out var refusal);

            suggestions.Add(new ComplexitySuggestion(
                "extract-method",
                startLine,
                endLine,
                $"Move lines {startLine}-{endLine} into their own private method ({candidate.Cyclomatic} decision(s) leave this method).",
                candidate.Cyclomatic,
                candidate.Cognitive,
                AutoApplicable: extraction is not null,
                extraction is null ? refusal : null));
        }
    }

    /// <summary>A boolean with several operators is one decision the reader cannot hold in their head at once.</summary>
    private static void AddConditionExtraction(
        SyntaxNode unit,
        BlockSyntax body,
        MethodDeclarationSyntax? host,
        TypeDeclarationSyntax? owner,
        List<ComplexitySuggestion> suggestions)
    {
        foreach (var condition in OwnConditions(body))
        {
            var operators = CountLogicalOperators(condition);
            if (operators < 3) continue;

            var span = condition.GetLocation().GetLineSpan();
            var startLine = span.StartLinePosition.Line + 1;

            var applicable = host is not null && owner is not null;
            suggestions.Add(new ComplexitySuggestion(
                "extract-condition",
                startLine,
                span.EndLinePosition.Line + 1,
                $"Name the {operators}-operator condition on line {startLine} in a private bool method " +
                $"({operators} point(s) leave this method).",
                operators,
                operators,
                AutoApplicable: applicable,
                applicable ? null : "Only a method can host the extracted condition."));
        }
    }

    /// <summary>
    /// `if (c) { ...big... }` as the last statement of a void method becomes
    /// `if (!c) return; ...big...` — same behaviour, one nesting level less.
    /// </summary>
    private static void AddGuardClause(
        SyntaxNode unit,
        BlockSyntax body,
        MethodDeclarationSyntax? host,
        SemanticModel model,
        List<ComplexitySuggestion> suggestions,
        CancellationToken cancellationToken)
    {
        if (host is null || body.Statements.Count < 2) return;
        if (body.Statements[^1] is not IfStatementSyntax tail || tail.Else is not null) return;
        if (tail.Statement is not { } branch) return;

        var symbol = model.GetDeclaredSymbol(host, cancellationToken);
        if (symbol is not IMethodSymbol method || !method.ReturnsVoid) return;

        var inside = branch is BlockSyntax block ? ComplexityAnalyzer.ScoreSubtree(block) : ComplexityAnalyzer.ScoreSubtree(branch);
        var decisions = Math.Max(0, inside.Cyclomatic - 1);
        if (decisions < 1) return;

        var span = tail.GetLocation().GetLineSpan();
        var startLine = span.StartLinePosition.Line + 1;

        suggestions.Add(new ComplexitySuggestion(
            "guard-clause",
            startLine,
            span.EndLinePosition.Line + 1,
            $"Flatten the tail 'if' on line {startLine} into 'if (!…) return;' — everything inside loses one nesting level.",
            CyclomaticReduction: 0,
            CognitiveReduction: decisions,
            AutoApplicable: true));
    }

    /// <summary>An else-if ladder longer than a switch is cheaper to read as a switch.</summary>
    private static void AddSplitLadder(BlockSyntax body, List<ComplexitySuggestion> suggestions)
    {
        var ladder = body.Statements.OfType<IfStatementSyntax>().FirstOrDefault();
        if (ladder is null) return;

        var branches = 0;
        for (var current = ladder; current is not null;)
        {
            branches++;
            current = current.Else?.Statement as IfStatementSyntax;
        }

        if (branches < 4) return;

        var line = ladder.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        suggestions.Add(new ComplexitySuggestion(
            "split-ladder",
            line,
            ladder.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
            $"The if/else-if chain on line {line} has {branches} branches; a switch (or a dictionary of handlers) reads as a table instead of a staircase.",
            CyclomaticReduction: 0,
            CognitiveReduction: branches,
            AutoApplicable: false,
            "Rewriting an if/else-if ladder into a switch changes the shapes of the arms; do this one by hand."));
    }

    private static IEnumerable<BlockSyntax> OwnBlocks(BlockSyntax body)
    {
        var stack = new Stack<SyntaxNode>();
        stack.Push(body);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var child in node.ChildNodes())
            {
                if (child is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) continue;
                if (child is BlockSyntax block) yield return block;
                stack.Push(child);
            }
        }
    }

    private static IEnumerable<ExpressionSyntax> OwnConditions(BlockSyntax body)
    {
        foreach (var node in body.DescendantNodes(child =>
                     child is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax))
        {
            switch (node)
            {
                case IfStatementSyntax @if:
                    yield return @if.Condition;
                    break;
                case WhileStatementSyntax @while:
                    yield return @while.Condition;
                    break;
                case DoStatementSyntax @do:
                    yield return @do.Condition;
                    break;
            }
        }
    }

    private static int CountLogicalOperators(ExpressionSyntax condition) =>
        condition.DescendantNodesAndSelf(child =>
                child is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
            .Count(node => node is BinaryExpressionSyntax binary &&
                           (binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression)));
}
