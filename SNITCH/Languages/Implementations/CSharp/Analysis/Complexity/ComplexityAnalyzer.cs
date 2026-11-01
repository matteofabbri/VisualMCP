using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Languages.CSharp.Analysis.Complexity;

/// <summary>
/// The two complexity numbers professional tooling reports, computed from the syntax tree instead of
/// guessed from the source text.
/// <para>
/// <b>Cyclomatic complexity</b> (McCabe) counts the independent paths through a method: one point per
/// <c>if</c>, loop, <c>catch</c>, <c>?:</c>, <c>&amp;&amp;</c>/<c>||</c>, <c>??</c>, pattern combinator,
/// <c>goto</c> and switch path.
/// </para>
/// <para>
/// <b>Cognitive complexity</b> (SonarSource) scores how hard the method is to follow: each break in the
/// linear flow costs 1 plus its nesting level, a run of like logical operators costs 1 for the whole
/// run rather than 1 each, and a <c>switch</c> is cheaper than the equivalent <c>else-if</c> ladder
/// because its paths are visually parallel. Neither metric is a substitute for the other, which is why
/// both are reported.
/// </para>
/// <para>
/// Lambdas and local functions are their own units: their decisions do not count towards the method
/// that contains them (the same way the compiler sees them, each with its own body).
/// </para>
/// </summary>
public static partial class ComplexityAnalyzer
{
    /// <summary>
    /// Analyzes one method, constructor, operator, accessor, lambda or local function, in C# or in
    /// Visual Basic: the numbers are the same metric, and the unit's syntax decides the walk.
    /// </summary>
    public static ComplexityMetrics Analyze(SyntaxNode unit, SemanticModel? model = null, string? filePath = null)
    {
        if (unit.SyntaxTree.Options.Language == LanguageNames.VisualBasic)
            return AnalyzeVisualBasic(unit, model, filePath);

        var (body, name, parameters) = Describe(unit);
        var span = unit.GetLocation().GetLineSpan();
        var firstLine = span.StartLinePosition.Line + 1;
        var lastLine = span.EndLinePosition.Line + 1;

        if (body is null)
        {
            return new ComplexityMetrics(
                name, FullNameOf(unit, model), filePath, firstLine, lastLine,
                Cyclomatic: 1, Cognitive: 0, MaxNesting: 0, LinesOfCode: lastLine - firstLine + 1,
                ParameterCount: parameters, HalsteadVolume: 0, MaintainabilityIndex: 100,
                Risk: "Low", Contributions: Array.Empty<ComplexityContribution>());
        }

        var walker = new Walker(name, SelfSymbolOf(unit, model), model);
        walker.Visit(body, 0);

        var lines = lastLine - firstLine + 1;
        var volume = HalsteadVolume(body);

        return new ComplexityMetrics(
            name,
            FullNameOf(unit, model),
            filePath,
            firstLine,
            lastLine,
            walker.Cyclomatic,
            walker.Cognitive,
            walker.MaxNesting,
            lines,
            parameters,
            volume,
            MaintainabilityIndex(volume, walker.Cyclomatic, lines),
            RiskOf(walker.Cyclomatic, walker.Cognitive),
            walker.Contributions);
    }

    /// <summary>
    /// Scores a bare subtree — a block, a condition — as if it were a method body. Used to measure
    /// what a move would take out of the host method before it is applied.
    /// </summary>
    public static ComplexityMetrics ScoreSubtree(SyntaxNode subtree)
    {
        var walker = new Walker(string.Empty, self: null, model: null);
        walker.Visit(subtree, 0);

        var span = subtree.GetLocation().GetLineSpan();
        var firstLine = span.StartLinePosition.Line + 1;
        var lastLine = span.EndLinePosition.Line + 1;
        var lines = lastLine - firstLine + 1;
        var volume = HalsteadVolume(subtree);

        return new ComplexityMetrics(
            subtree.Kind().ToString(), subtree.Kind().ToString(), null, firstLine, lastLine,
            walker.Cyclomatic, walker.Cognitive, walker.MaxNesting, lines,
            ParameterCount: 0, volume, MaintainabilityIndex(volume, walker.Cyclomatic, lines),
            RiskOf(walker.Cyclomatic, walker.Cognitive), walker.Contributions);
    }

    /// <summary>
    /// Every unit in <paramref name="root"/> that gets its own score: methods, constructors, operators,
    /// accessors, expression-bodied properties/indexers, local functions and lambdas — in C# or VB.
    /// </summary>
    public static IEnumerable<SyntaxNode> Units(SyntaxNode root) => root.DescendantNodes().Where(IsUnit);

    /// <summary>The method-like units only (no accessors or lambdas): what a hotspot ranking lists.</summary>
    public static IEnumerable<SyntaxNode> MethodUnits(SyntaxNode root) =>
        root.DescendantNodes().Where(IsMethodUnit);

    public static bool IsUnit(SyntaxNode node) => node.SyntaxTree.Options.Language == LanguageNames.VisualBasic
        ? IsVisualBasicUnit(node)
        : node switch
        {
            BaseMethodDeclarationSyntax                                => true,
            LocalFunctionStatementSyntax                               => true,
            AccessorDeclarationSyntax                                  => true,
            AnonymousFunctionExpressionSyntax                          => true,
            PropertyDeclarationSyntax { ExpressionBody: not null }     => true,
            IndexerDeclarationSyntax { ExpressionBody: not null }      => true,
            _                                                          => false,
        };

    /// <summary>The units a "worst methods" ranking reads: methods, constructors and operators.</summary>
    public static bool IsMethodUnit(SyntaxNode node) => node.SyntaxTree.Options.Language == LanguageNames.VisualBasic
        ? IsVisualBasicMethodUnit(node)
        : node is BaseMethodDeclarationSyntax;

    /// <summary>The name a unit is ranked under, in either language.</summary>
    public static string NameOf(SyntaxNode unit) => unit.SyntaxTree.Options.Language == LanguageNames.VisualBasic
        ? DescribeVisualBasic(unit).Name
        : Describe(unit).Name;

    /// <summary>Low/Medium/High/Critical, whichever of the two metrics is worse.</summary>
    public static string RiskOf(int cyclomatic, int cognitive) =>
        Math.Max(Level(cyclomatic, 10, 15, 25), Level(cognitive, 10, 15, 25)) switch
        {
            3 => "Critical",
            2 => "High",
            1 => "Medium",
            _ => "Low",
        };

    private static int Level(int value, int medium, int high, int critical) =>
        value >= critical ? 3 : value >= high ? 2 : value >= medium ? 1 : 0;

    /// <summary>
    /// Halstead volume: (N1+N2)·log2(n1+n2) over the tokens of the body, identifiers and literals as
    /// operands and everything else (keywords included) as operators. The token kinds are compared by
    /// name so the same formula reads a C# and a VB body.
    /// </summary>
    private static int HalsteadVolume(SyntaxNode body) => HalsteadVolume(body.DescendantTokens());

    private static int HalsteadVolume(IEnumerable<SyntaxToken> tokens)
    {
        var operators = new HashSet<string>(StringComparer.Ordinal);
        var operands = new HashSet<string>(StringComparer.Ordinal);
        var operatorCount = 0;
        var operandCount = 0;

        foreach (var token in tokens)
        {
            if (IsOperandToken(token))
            {
                operands.Add(token.Text);
                operandCount++;
            }
            else
            {
                operators.Add(token.Text);
                operatorCount++;
            }
        }

        var vocabulary = operators.Count + operands.Count;
        if (vocabulary == 0) return 0;

        return (int)Math.Round((operatorCount + operandCount) * Math.Log2(vocabulary));
    }

    /// <summary>
    /// The operand half of Halstead: a name or a literal. The kind is read by name because the C# and
    /// Visual Basic enums are different types with the same spellings.
    /// </summary>
    private static bool IsOperandToken(SyntaxToken token) => token.Kind().ToString() is
        "IdentifierToken" or
        "NumericLiteralToken" or
        "StringLiteralToken" or
        "CharacterLiteralToken" or
        "Utf8StringLiteralToken" or
        "InterpolatedStringTextToken" or
        "DateLiteralToken";

    /// <summary>Visual Studio's maintainability index, clamped to 0-100.</summary>
    private static int MaintainabilityIndex(int volume, int cyclomatic, int lines)
    {
        if (volume <= 0 || lines <= 0) return 100;

        var raw = 171 - 5.2 * Math.Log(volume) - 0.23 * cyclomatic - 16.2 * Math.Log(lines);
        return (int)Math.Round(Math.Clamp(raw * 100 / 171, 0, 100));
    }

    private static (SyntaxNode? Body, string Name, int Parameters) Describe(SyntaxNode unit) => unit switch
    {
        MethodDeclarationSyntax method => (BodyOf(method.Body, method.ExpressionBody), method.Identifier.ValueText, method.ParameterList.Parameters.Count),
        ConstructorDeclarationSyntax constructor => (BodyOf(constructor.Body, constructor.ExpressionBody), constructor.Identifier.ValueText, constructor.ParameterList.Parameters.Count),
        DestructorDeclarationSyntax destructor => (BodyOf(destructor.Body, destructor.ExpressionBody), "~" + destructor.Identifier.ValueText, 0),
        OperatorDeclarationSyntax @operator => (BodyOf(@operator.Body, @operator.ExpressionBody), "operator " + @operator.OperatorToken.Text, @operator.ParameterList.Parameters.Count),
        ConversionOperatorDeclarationSyntax conversion => (BodyOf(conversion.Body, conversion.ExpressionBody), "operator " + conversion.Type, conversion.ParameterList.Parameters.Count),
        AccessorDeclarationSyntax accessor => (BodyOf(accessor.Body, accessor.ExpressionBody), accessor.Keyword.Text, 0),
        LocalFunctionStatementSyntax local => (BodyOf(local.Body, local.ExpressionBody), local.Identifier.ValueText, local.ParameterList.Parameters.Count),
        AnonymousFunctionExpressionSyntax lambda => (lambda.Block ?? (SyntaxNode?)lambda.ExpressionBody, "lambda", AnonymousParameters(lambda)),
        PropertyDeclarationSyntax property => (property.ExpressionBody?.Expression, property.Identifier.ValueText, 0),
        IndexerDeclarationSyntax indexer => (indexer.ExpressionBody?.Expression, "this[]", indexer.ParameterList.Parameters.Count),
        _ => (null, unit.Kind().ToString(), 0),
    };

    private static SyntaxNode? BodyOf(BlockSyntax? block, ArrowExpressionClauseSyntax? expressionBody) =>
        block ?? (SyntaxNode?)expressionBody?.Expression;

    private static int AnonymousParameters(AnonymousFunctionExpressionSyntax lambda) =>
        lambda switch
        {
            SimpleLambdaExpressionSyntax => 1,
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.Count,
            _ => 0,
        };

    private static string FullNameOf(SyntaxNode unit, SemanticModel? model)
    {
        if (model is null) return NameOf(unit);

        var symbol = model.GetDeclaredSymbol(unit);
        return symbol?.ToDisplayString() ?? NameOf(unit);
    }

    private static ISymbol? SelfSymbolOf(SyntaxNode unit, SemanticModel? model) => model?.GetDeclaredSymbol(unit);

    /// <summary>
    /// The recursive walk. Every decision point adds its cost to both scores and records a
    /// contribution, so the report can point at the exact line that owns the score.
    /// </summary>
    private sealed class Walker
    {
        private const int ContributionCap = 500;

        private readonly string _selfName;
        private readonly ISymbol? _self;
        private readonly SemanticModel? _model;

        public Walker(string selfName, ISymbol? self, SemanticModel? model)
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

        private void Add(SyntaxNode node, int nesting, int cyclomatic, int cognitive, string kind, string? detail = null)
        {
            Cyclomatic += cyclomatic;
            Cognitive += cognitive;
            if (nesting > MaxNesting) MaxNesting = nesting;

            if (_contributions.Count >= ContributionCap) return;

            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            _contributions.Add(new ComplexityContribution(kind, line, nesting, cyclomatic, cognitive, detail));
        }

        public void Visit(SyntaxNode node, int nesting)
        {
            switch (node)
            {
                // A nested function is its own unit: its body is measured on its own.
                case AnonymousFunctionExpressionSyntax:
                case LocalFunctionStatementSyntax:
                    return;

                case IfStatementSyntax @if:
                    Add(@if, nesting, 1, 1 + nesting, "if", Snippet(@if.Condition));
                    Visit(@if.Condition, nesting);
                    Visit(@if.Statement, nesting + 1);
                    if (@if.Else is not null) VisitElse(@if.Else, nesting);
                    return;

                case ElseClauseSyntax @else:
                    VisitElse(@else, nesting);
                    return;

                case ForStatementSyntax @for:
                    Add(@for, nesting, 1, 1 + nesting, "for");
                    if (@for.Declaration is not null) Visit(@for.Declaration, nesting);
                    if (@for.Condition is not null) Visit(@for.Condition, nesting);
                    foreach (var incrementor in @for.Incrementors) Visit(incrementor, nesting);
                    Visit(@for.Statement, nesting + 1);
                    return;

                case ForEachStatementSyntax forEach:
                    Add(forEach, nesting, 1, 1 + nesting, "foreach");
                    Visit(forEach.Expression, nesting);
                    Visit(forEach.Statement, nesting + 1);
                    return;

                case WhileStatementSyntax @while:
                    Add(@while, nesting, 1, 1 + nesting, "while", Snippet(@while.Condition));
                    Visit(@while.Condition, nesting);
                    Visit(@while.Statement, nesting + 1);
                    return;

                case DoStatementSyntax @do:
                    Add(@do, nesting, 1, 1 + nesting, "do", Snippet(@do.Condition));
                    Visit(@do.Statement, nesting + 1);
                    Visit(@do.Condition, nesting);
                    return;

                case CatchClauseSyntax @catch:
                    Add(@catch, nesting, 1, 1 + nesting, "catch", @catch.Declaration?.Type.ToString());
                    if (@catch.Declaration is not null) Visit(@catch.Declaration, nesting);
                    if (@catch.Filter is not null) Visit(@catch.Filter, nesting);
                    Visit(@catch.Block, nesting + 1);
                    return;

                case SwitchStatementSyntax @switch:
                {
                    var labels = @switch.Sections.Sum(section => section.Labels.Count);
                    Add(@switch, nesting, Math.Max(0, labels - 1), 1 + nesting, "switch", $"{labels} label(s)");

                    Visit(@switch.Expression, nesting);
                    foreach (var section in @switch.Sections)
                    {
                        foreach (var label in section.Labels)
                        {
                            if (label is CasePatternSwitchLabelSyntax { WhenClause: not null } pattern)
                                Visit(pattern.WhenClause.Condition, nesting);
                        }

                        foreach (var statement in section.Statements) Visit(statement, nesting + 1);
                    }

                    return;
                }

                case SwitchExpressionSyntax @switchExpression:
                {
                    var arms = @switchExpression.Arms.Count;
                    Add(@switchExpression, nesting, Math.Max(0, arms - 1), 1 + nesting, "switch-expression", $"{arms} arm(s)");

                    Visit(@switchExpression.GoverningExpression, nesting);
                    foreach (var arm in @switchExpression.Arms)
                    {
                        if (arm.WhenClause is not null) Visit(arm.WhenClause.Condition, nesting);
                        Visit(arm.Expression, nesting + 1);
                    }

                    return;
                }

                case ConditionalExpressionSyntax conditional:
                    Add(conditional, nesting, 1, 1 + nesting, "ternary", Snippet(conditional.Condition));
                    Visit(conditional.Condition, nesting);
                    Visit(conditional.WhenTrue, nesting + 1);
                    Visit(conditional.WhenFalse, nesting + 1);
                    return;

                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) ||
                                                        binary.IsKind(SyntaxKind.LogicalOrExpression):
                {
                    // A run of like operators is one cognitive decision, not one per operator:
                    // `a && b && c` parses as `(a && b) && c` and costs 1, `(a && b) || c` costs 2.
                    var sameOperatorAbove = binary.Parent is BinaryExpressionSyntax parent &&
                                            parent.IsKind(binary.Kind());
                    Add(binary,
                        nesting,
                        cyclomatic: 1,
                        cognitive: sameOperatorAbove ? 0 : 1,
                        kind: binary.IsKind(SyntaxKind.LogicalAndExpression) ? "&&" : "||",
                        detail: sameOperatorAbove ? null : "logical sequence");
                    break;
                }

                case BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression):
                    Add(coalesce, nesting, 1, 0, "??");
                    break;

                case AssignmentExpressionSyntax assignment when assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression):
                    Add(assignment, nesting, 1, 0, "??=");
                    break;

                case BinaryPatternSyntax pattern:
                    Add(pattern,
                        nesting,
                        cyclomatic: 1,
                        cognitive: 0,
                        kind: pattern.IsKind(SyntaxKind.AndPattern) ? "and-pattern" : "or-pattern");
                    break;

                case GotoStatementSyntax @goto:
                    Add(@goto, nesting, 1, 1, "goto");
                    break;

                case InvocationExpressionSyntax invocation when IsRecursive(invocation):
                    Add(invocation, nesting, 0, 1, "recursion", _selfName);
                    break;
            }

            foreach (var child in node.ChildNodes()) Visit(child, nesting);
        }

        /// <summary>`else if` costs 1 with no nesting penalty; a plain `else` costs 1 as well.</summary>
        private void VisitElse(ElseClauseSyntax elseClause, int nesting)
        {
            if (elseClause.Statement is IfStatementSyntax elseIf)
            {
                Add(elseIf, nesting, 1, 1, "else-if", Snippet(elseIf.Condition));
                Visit(elseIf.Condition, nesting);
                Visit(elseIf.Statement, nesting + 1);
                if (elseIf.Else is not null) VisitElse(elseIf.Else, nesting);
                return;
            }

            Add(elseClause, nesting, 0, 1, "else");
            Visit(elseClause.Statement, nesting + 1);
        }

        private bool IsRecursive(InvocationExpressionSyntax invocation)
        {
            if (_self is null || _model is null) return false;

            return SymbolEqualityComparer.Default.Equals(
                _model.GetSymbolInfo(invocation).Symbol, _self);
        }

        private static string? Snippet(SyntaxNode node)
        {
            var text = node.ToString().Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length == 0) return null;
            return text.Length <= 60 ? text : text[..57] + "...";
        }
    }
}
