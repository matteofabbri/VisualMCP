using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.CSharp.Analysis.Complexity;

/// <summary>
/// The Visual Basic half of the complexity metrics. VB spells every decision as a block or a
/// conditional expression, so the walk is a different switch — <c>If … End If</c> with its
/// <c>ElseIf</c> blocks, <c>Do … Loop</c> with a clause at either end, <c>Select Case</c> with its
/// clause kinds, <c>If(a, b)</c> as the coalescing form — while the numbers, the thresholds and the
/// Halstead/MI formulas are the ones the C# walk uses.
/// </summary>
public static partial class ComplexityAnalyzer
{
    /// <summary>The units a VB file can declare: methods, constructors, operators, accessors and lambdas.</summary>
    internal static bool IsVisualBasicUnit(SyntaxNode node) => node
        is MethodBlockSyntax or ConstructorBlockSyntax or OperatorBlockSyntax or AccessorBlockSyntax
        or LambdaExpressionSyntax;

    /// <summary>The method-like units of a VB tree (what a "hot method" ranking means).</summary>
    internal static bool IsVisualBasicMethodUnit(SyntaxNode node) =>
        node is MethodBlockSyntax or ConstructorBlockSyntax or OperatorBlockSyntax;

    private static ComplexityMetrics AnalyzeVisualBasic(SyntaxNode unit, SemanticModel? model, string? filePath)
    {
        var description = DescribeVisualBasic(unit);
        var span = unit.GetLocation().GetLineSpan();
        var firstLine = span.StartLinePosition.Line + 1;
        var lastLine = span.EndLinePosition.Line + 1;

        var statements = description.Statements;
        var expression = description.Expression;
        if ((statements is null || statements.Count == 0) && expression is null)
        {
            return new ComplexityMetrics(
                description.Name, FullNameOf(unit, model), filePath, firstLine, lastLine,
                Cyclomatic: 1, Cognitive: 0, MaxNesting: 0, LinesOfCode: lastLine - firstLine + 1,
                ParameterCount: description.Parameters, HalsteadVolume: 0, MaintainabilityIndex: 100,
                Risk: "Low", Contributions: Array.Empty<ComplexityContribution>());
        }

        var walker = new VisualBasicWalker(description.Name, model?.GetDeclaredSymbol(unit), model);

        var tokens = statements is { Count: > 0 }
            ? statements.SelectMany(statement => statement.DescendantTokens())
            : expression!.DescendantTokens();

        if (statements is { Count: > 0 }) walker.VisitStatements(statements, 0);
        else walker.VisitNode(expression!, 0);

        var lines = lastLine - firstLine + 1;
        var volume = HalsteadVolume(tokens);

        return new ComplexityMetrics(
            description.Name,
            FullNameOf(unit, model),
            filePath,
            firstLine,
            lastLine,
            walker.Cyclomatic,
            walker.Cognitive,
            walker.MaxNesting,
            lines,
            description.Parameters,
            volume,
            MaintainabilityIndex(volume, walker.Cyclomatic, lines),
            RiskOf(walker.Cyclomatic, walker.Cognitive),
            walker.Contributions);
    }

    internal static (IReadOnlyList<StatementSyntax>? Statements, SyntaxNode? Expression, string Name, int Parameters)
        DescribeVisualBasic(SyntaxNode unit) => unit switch
        {
            MethodBlockSyntax method => (
                method.Statements,
                null,
                method.SubOrFunctionStatement.Identifier.ValueText,
                method.SubOrFunctionStatement.ParameterList?.Parameters.Count ?? 0),

            ConstructorBlockSyntax constructor => (
                constructor.Statements,
                null,
                "New",
                constructor.SubNewStatement.ParameterList?.Parameters.Count ?? 0),

            OperatorBlockSyntax @operator => (
                @operator.Statements,
                null,
                "operator " + @operator.OperatorStatement.OperatorToken.Text,
                @operator.OperatorStatement.ParameterList?.Parameters.Count ?? 0),

            AccessorBlockSyntax accessor => (
                accessor.Statements,
                null,
                accessor.AccessorStatement.AccessorKeyword.Text,
                0),

            MultiLineLambdaExpressionSyntax multiLine => (
                multiLine.Statements,
                null,
                "lambda",
                ParametersOf(multiLine.SubOrFunctionHeader?.ParameterList)),

            // A single-line lambda's body is an expression or one statement, depending on Sub/Function.
            SingleLineLambdaExpressionSyntax singleLine => singleLine.Body switch
            {
                ExpressionSyntax body => (null, (SyntaxNode?)body, "lambda",
                    ParametersOf(singleLine.SubOrFunctionHeader?.ParameterList)),
                StatementSyntax body => (new[] { body }, null, "lambda",
                    ParametersOf(singleLine.SubOrFunctionHeader?.ParameterList)),
                _ => (null, null, "lambda", 0),
            },

            _ => (null, null, unit.Kind().ToString(), 0),
        };

    /// <summary>
    /// Scores a bare VB subtree — a block, a condition — as if it were a method body.
    /// <c>ScoreSubtree</c> runs the C# walk, so this is its VB counterpart for the reducer's yardstick:
    /// how much would leave the host method if this subtree moved out of it.
    /// </summary>
    public static ComplexityMetrics ScoreVisualBasicSubtree(SyntaxNode subtree)
    {
        var walker = new VisualBasicWalker(string.Empty, self: null, model: null);
        walker.VisitNode(subtree, 0);

        var span = subtree.GetLocation().GetLineSpan();
        var firstLine = span.StartLinePosition.Line + 1;
        var lastLine = span.EndLinePosition.Line + 1;
        var lines = Math.Max(1, lastLine - firstLine + 1);
        var volume = HalsteadVolume(subtree);

        return new ComplexityMetrics(
            subtree.Kind().ToString(), subtree.Kind().ToString(), null, firstLine, lastLine,
            walker.Cyclomatic, walker.Cognitive, walker.MaxNesting, lines,
            ParameterCount: 0, volume, MaintainabilityIndex(volume, walker.Cyclomatic, lines),
            RiskOf(walker.Cyclomatic, walker.Cognitive), walker.Contributions);
    }

    private static int ParametersOf(ParameterListSyntax? list) => list?.Parameters.Count ?? 0;

    /// <summary>
    /// The VB walk. Every decision adds to both scores and records a contribution with its line and
    /// nesting, exactly as the C# walk does, so <c>complexity_report</c> lists the same kinds of rows.
    /// </summary>
    private sealed class VisualBasicWalker
    {
        private const int ContributionCap = 500;

        private readonly string _selfName;
        private readonly ISymbol? _self;
        private readonly SemanticModel? _model;

        public VisualBasicWalker(string selfName, ISymbol? self, SemanticModel? model)
        {
            _selfName = selfName;
            _self = self;
            _model = model;
        }

        public int Cyclomatic { get; private set; } = 1;

        public int Cognitive { get; private set; }

        public int MaxNesting { get; private set; }

        public IReadOnlyList<ComplexityContribution> Contributions => _contributions;

        private readonly List<ComplexityContribution> _contributions = [];

        public void VisitStatements(IReadOnlyList<StatementSyntax> statements, int nesting)
        {
            foreach (var statement in statements) VisitNode(statement, nesting);
        }

        private void Add(SyntaxNode node, int nesting, int cyclomatic, int cognitive, string kind, string? detail = null)
        {
            Cyclomatic += cyclomatic;
            Cognitive += cognitive;
            if (nesting > MaxNesting) MaxNesting = nesting;

            if (_contributions.Count >= ContributionCap) return;

            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            _contributions.Add(new ComplexityContribution(kind, line, nesting, cyclomatic, cognitive, detail));
        }

        public void VisitNode(SyntaxNode node, int nesting)
        {
            switch (node)
            {
                // A nested function or lambda is its own unit: its body is measured on its own.
                case LambdaExpressionSyntax:
                case MethodBlockSyntax:
                    return;

                case MultiLineIfBlockSyntax @if:
                    Add(@if, nesting, 1, 1 + nesting, "if", Snippet(@if.IfStatement.Condition));
                    VisitNode(@if.IfStatement.Condition, nesting);
                    VisitStatements(@if.Statements, nesting + 1);

                    foreach (var elseIf in @if.ElseIfBlocks)
                    {
                        Add(elseIf, nesting, 1, 1, "else-if", Snippet(elseIf.ElseIfStatement.Condition));
                        VisitNode(elseIf.ElseIfStatement.Condition, nesting);
                        VisitStatements(elseIf.Statements, nesting + 1);
                    }

                    if (@if.ElseBlock is { } elseBlock)
                    {
                        Add(elseBlock, nesting, 0, 1, "else");
                        VisitStatements(elseBlock.Statements, nesting + 1);
                    }

                    return;

                case SingleLineIfStatementSyntax @if:
                    Add(@if, nesting, 1, 1 + nesting, "if", Snippet(@if.Condition));
                    VisitNode(@if.Condition, nesting);
                    VisitStatements(@if.Statements, nesting + 1);

                    if (@if.ElseClause is { } singleLineElse)
                    {
                        Add(singleLineElse, nesting, 0, 1, "else");
                        VisitStatements(singleLineElse.Statements, nesting + 1);
                    }

                    return;

                case WhileBlockSyntax @while:
                    Add(@while, nesting, 1, 1 + nesting, "while", Snippet(@while.WhileStatement.Condition));
                    VisitNode(@while.WhileStatement.Condition, nesting);
                    VisitStatements(@while.Statements, nesting + 1);
                    return;

                case DoLoopBlockSyntax @do:
                {
                    Add(@do, nesting, 1, 1 + nesting, "do",
                        Snippet((@do.DoStatement.WhileOrUntilClause ?? @do.LoopStatement.WhileOrUntilClause)?.Condition));
                    if (@do.DoStatement.WhileOrUntilClause is { } pre) VisitNode(pre.Condition, nesting);
                    if (@do.LoopStatement.WhileOrUntilClause is { } post) VisitNode(post.Condition, nesting);
                    VisitStatements(@do.Statements, nesting + 1);
                    return;
                }

                case ForBlockSyntax @for:
                    Add(@for, nesting, 1, 1 + nesting, "for");
                    VisitNode(@for.ForStatement.FromValue, nesting);
                    VisitNode(@for.ForStatement.ToValue, nesting);
                    if (@for.ForStatement.StepClause is { } step) VisitNode(step.StepValue, nesting);
                    VisitStatements(@for.Statements, nesting + 1);
                    return;

                case ForEachBlockSyntax forEach:
                    Add(forEach, nesting, 1, 1 + nesting, "foreach");
                    VisitNode(forEach.ForEachStatement.Expression, nesting);
                    VisitStatements(forEach.Statements, nesting + 1);
                    return;

                case CatchBlockSyntax @catch:
                    Add(@catch, nesting, 1, 1 + nesting, "catch",
                        @catch.CatchStatement.AsClause?.ToString());
                    if (@catch.CatchStatement.WhenClause is { } when) VisitNode(when.Filter, nesting);
                    VisitStatements(@catch.Statements, nesting + 1);
                    return;

                case SelectBlockSyntax @select:
                {
                    var labels = @select.CaseBlocks.Sum(block => block.CaseStatement.Cases.Count);
                    Add(@select, nesting, Math.Max(0, labels - 1), 1 + nesting, "select-case", $"{labels} clause(s)");

                    VisitNode(@select.SelectStatement.Expression, nesting);
                    foreach (var block in @select.CaseBlocks)
                    {
                        foreach (var clause in block.CaseStatement.Cases)
                        {
                            switch (clause)
                            {
                                case SimpleCaseClauseSyntax simple:
                                    VisitNode(simple.Value, nesting);
                                    break;
                                case RangeCaseClauseSyntax range:
                                    VisitNode(range.LowerBound, nesting);
                                    VisitNode(range.UpperBound, nesting);
                                    break;
                                case RelationalCaseClauseSyntax relational:
                                    VisitNode(relational.Value, nesting);
                                    break;
                            }
                        }

                        VisitStatements(block.Statements, nesting + 1);
                    }

                    return;
                }

                case TernaryConditionalExpressionSyntax conditional:
                    Add(conditional, nesting, 1, 1 + nesting, "if-expression", Snippet(conditional.Condition));
                    VisitNode(conditional.Condition, nesting);
                    VisitNode(conditional.WhenTrue, nesting + 1);
                    VisitNode(conditional.WhenFalse, nesting + 1);
                    return;

                case BinaryConditionalExpressionSyntax coalesce:
                    Add(coalesce, nesting, 1, 0, "??");
                    VisitNode(coalesce.FirstExpression, nesting);
                    VisitNode(coalesce.SecondExpression, nesting);
                    return;

                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AndAlsoExpression) ||
                                                        binary.IsKind(SyntaxKind.OrElseExpression):
                {
                    // A run of like operators is one cognitive decision, not one per operator.
                    var sameOperatorAbove = binary.Parent is BinaryExpressionSyntax parent &&
                                            parent.IsKind(binary.Kind());
                    Add(binary,
                        nesting,
                        cyclomatic: 1,
                        cognitive: sameOperatorAbove ? 0 : 1,
                        kind: binary.IsKind(SyntaxKind.AndAlsoExpression) ? "AndAlso" : "OrElse",
                        detail: sameOperatorAbove ? null : "logical sequence");
                    break;
                }

                case GoToStatementSyntax @goto:
                    Add(@goto, nesting, 1, 1, "goto");
                    break;

                case InvocationExpressionSyntax invocation when IsRecursive(invocation):
                    Add(invocation, nesting, 0, 1, "recursion", _selfName);
                    break;
            }

            foreach (var child in node.ChildNodes()) VisitNode(child, nesting);
        }

        private bool IsRecursive(InvocationExpressionSyntax invocation)
        {
            if (_self is null || _model is null) return false;

            return SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(invocation).Symbol, _self);
        }

        private static string? Snippet(SyntaxNode? node)
        {
            if (node is null) return null;

            var text = node.ToString().Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length == 0) return null;
            return text.Length <= 60 ? text : text[..57] + "...";
        }
    }
}
