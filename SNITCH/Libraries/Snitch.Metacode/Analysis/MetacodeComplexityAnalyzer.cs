namespace Snitch.Metacode.Analysis;

/// <summary>One decision point, so a report can point at the line that owns the score.</summary>
public sealed record MetacodeComplexityContribution(
    string Kind,
    int Line,
    int Nesting,
    int Cyclomatic,
    int Cognitive,
    string? Detail = null);

/// <summary>
/// The two numbers professional tooling reports, computed over the language-neutral model instead of
/// over one language's syntax tree: McCabe's path count and SonarSource's cognitive score.
/// </summary>
public sealed record MetacodeComplexityMetrics(
    int Cyclomatic,
    int Cognitive,
    int MaxNesting,
    int Statements,
    int Branches,
    int Expressions,
    IReadOnlyList<MetacodeComplexityContribution> Contributions)
{
    public static MetacodeComplexityMetrics Empty { get; } =
        new(1, 0, 0, 0, 0, 0, Array.Empty<MetacodeComplexityContribution>());

    /// <summary>Low/Medium/High/Critical, whichever of the two metrics is worse (the same scale the C# report uses).</summary>
    public string Risk => MetacodeComplexityAnalyzer.RiskOf(Cyclomatic, Cognitive);
}

/// <summary>
/// Computes complexity from <see cref="MetacodeStatement"/> / <see cref="MetacodeExpression"/> nodes.
/// <para>
/// The rules are the ones the C# analyzer in this solution uses, restated on the model so they apply to
/// every language that can be lowered into it: one point per <c>if</c>, loop, <c>catch</c>, ternary,
/// <c>&amp;&amp;</c>/<c>||</c>, <c>??</c>, switch path, <c>goto</c> and recursive self-call. Cognitive
/// complexity charges each break in the flow one plus its nesting level, counts a run of like logical
/// operators once, and treats <c>else if</c> as cheap as a plain <c>else</c>.
/// </para>
/// <para>
/// A nested lambda or local function is its own unit: its decisions do not count towards the method
/// that contains it, which is how the compiler sees them.
/// </para>
/// </summary>
public static class MetacodeComplexityAnalyzer
{
    private const int ContributionCap = 500;

    public static MetacodeComplexityMetrics Analyze(MetacodeFunction function) =>
        Analyze(function.Body, SelfName(function));

    public static MetacodeComplexityMetrics Analyze(MetacodeBlock? body, string? selfName = null)
    {
        if (body is null) return MetacodeComplexityMetrics.Empty;

        var walker = new Walker(selfName);
        walker.WalkBlock(body, nesting: 0);
        return walker.Result();
    }

    /// <summary>A property's score is the score of its accessors together; a type has none of its own.</summary>
    public static MetacodeComplexityMetrics Analyze(MetacodeProperty property)
    {
        var walker = new Walker(selfName: property.Name);

        foreach (var accessor in new[] { property.Getter, property.Setter })
        {
            if (accessor?.Body is null) continue;
            walker.WalkBlock(accessor.Body, nesting: 0);
        }

        return walker.Result();
    }

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

    /// <summary>The name a recursive call would use: what the function calls itself in its own body.</summary>
    private static string? SelfName(MetacodeFunction function) =>
        function.Kind is MetacodeFunctionKind.Function or MetacodeFunctionKind.Method or MetacodeFunctionKind.LocalFunction
            ? function.Name
            : null;

    private sealed class Walker
    {
        private readonly string? _selfName;
        private readonly List<MetacodeComplexityContribution> _contributions = new();
        private int _cyclomatic = 1;
        private int _cognitive;
        private int _maxNesting;
        private int _statements;
        private int _branches;
        private int _expressions;

        public Walker(string? selfName) => _selfName = selfName;

        public MetacodeComplexityMetrics Result() => new(
            _cyclomatic, _cognitive, _maxNesting, _statements, _branches, _expressions, _contributions);

        private void Add(MetacodeOrigin origin, int nesting, int cyclomatic, int cognitive, string kind, string? detail = null)
        {
            _cyclomatic += cyclomatic;
            _cognitive += cognitive;
            if (cyclomatic > 0) _branches++;
            if (nesting > _maxNesting) _maxNesting = nesting;

            if (_contributions.Count >= ContributionCap) return;

            _contributions.Add(new MetacodeComplexityContribution(
                kind, origin.IsKnown ? origin.StartLine : 0, nesting, cyclomatic, cognitive, detail));
        }

        public void WalkBlock(MetacodeBlock block, int nesting)
        {
            foreach (var statement in block.Statements)
                WalkStatement(statement, nesting);
        }

        private void WalkStatement(MetacodeStatement statement, int nesting)
        {
            switch (statement)
            {
                case MetacodeBlock block:
                    WalkBlock(block, nesting);
                    return;

                case MetacodeIf @if:
                    WalkIf(@if, nesting);
                    return;

                case MetacodeWhile @while:
                    _statements++;
                    Add(@while.Origin, nesting, 1, 1 + nesting, "while", Detail(@while.Condition));
                    WalkExpression(@while.Condition, nesting);
                    WalkBlock(@while.Body, nesting + 1);
                    return;

                case MetacodeDoWhile doWhile:
                    _statements++;
                    Add(doWhile.Origin, nesting, 1, 1 + nesting, "do", Detail(doWhile.Condition));
                    WalkBlock(doWhile.Body, nesting + 1);
                    WalkExpression(doWhile.Condition, nesting);
                    return;

                case MetacodeForEach forEach:
                    _statements++;
                    Add(forEach.Origin, nesting, 1, 1 + nesting, "foreach", forEach.VariableName);
                    WalkExpression(forEach.Iterable, nesting);
                    WalkBlock(forEach.Body, nesting + 1);
                    return;

                case MetacodeFor @for:
                    _statements++;
                    Add(@for.Origin, nesting, 1, 1 + nesting, "for");
                    if (@for.Initializer is not null) WalkStatement(@for.Initializer, nesting);
                    if (@for.Condition is not null) WalkExpression(@for.Condition, nesting);
                    if (@for.Increment is not null) WalkStatement(@for.Increment, nesting);
                    WalkBlock(@for.Body, nesting + 1);
                    return;

                case MetacodeSwitch @switch:
                    _statements++;
                    WalkSwitch(@switch, nesting);
                    return;

                case MetacodeTry @try:
                    _statements++;
                    WalkBlock(@try.Body, nesting);
                    foreach (var @catch in @try.Catches)
                    {
                        Add(@catch.Origin, nesting, 1, 1 + nesting, "catch", @catch.ExceptionType?.Render());
                        if (@catch.Filter is not null) WalkExpression(@catch.Filter, nesting);
                        WalkBlock(@catch.Body, nesting + 1);
                    }

                    if (@try.Else is not null) WalkBlock(@try.Else, nesting);
                    if (@try.Finally is not null) WalkBlock(@try.Finally, nesting);
                    return;

                case MetacodeUsing @using:
                    _statements++;
                    if (@using.Resource is not null) WalkExpression(@using.Resource, nesting);
                    if (@using.Declaration?.Initializer is not null) WalkExpression(@using.Declaration.Initializer, nesting);
                    WalkBlock(@using.Body, nesting);
                    return;

                case MetacodeLock @lock:
                    _statements++;
                    WalkExpression(@lock.Target, nesting);
                    WalkBlock(@lock.Body, nesting);
                    return;

                case MetacodeLocalFunction:
                    // Its body is its own unit, exactly like a nested function in the C# analyzer.
                    _statements++;
                    return;

                case MetacodeLabel label:
                    _statements++;
                    WalkStatement(label.Statement, nesting);
                    return;

                case MetacodeGoto @goto:
                    _statements++;
                    Add(@goto.Origin, nesting, 1, 1, "goto", @goto.Label);
                    return;

                case MetacodeAssignment assignment:
                    _statements++;
                    if (assignment.Operator is MetacodeAssignmentOperator.Coalesce)
                        Add(assignment.Origin, nesting, 1, 0, "??=");
                    WalkExpression(assignment.Target, nesting);
                    WalkExpression(assignment.Value, nesting);
                    return;

                case MetacodeLocalVariable variable:
                    _statements++;
                    if (variable.Initializer is not null) WalkExpression(variable.Initializer, nesting);
                    return;

                case MetacodeReturn @return:
                    _statements++;
                    if (@return.Value is not null) WalkExpression(@return.Value, nesting);
                    return;

                case MetacodeThrow @throw:
                    _statements++;
                    if (@throw.Exception is not null) WalkExpression(@throw.Exception, nesting);
                    return;

                case MetacodeExpressionStatement expressionStatement:
                    _statements++;
                    WalkExpression(expressionStatement.Expression, nesting);
                    return;

                case MetacodeYield @yield:
                    _statements++;
                    if (@yield.Value is not null) WalkExpression(@yield.Value, nesting);
                    return;

                case MetacodeBreak or MetacodeContinue or MetacodeOpaqueStatement or MetacodeImport:
                    _statements++;
                    return;

                case MetacodeComment:
                    // A comment is not a statement a reader counts in the structure.
                    return;
            }
        }

        /// <summary>
        /// An <c>else if</c> costs one path and one cognitive point with no nesting penalty — it is a
        /// sibling of the <c>if</c> before it, not a nested branch; a plain <c>else</c> costs no path and
        /// one cognitive point. The model nests the chain as single <c>if</c>s inside else blocks, so the
        /// walk is a loop rather than recursion.
        /// </summary>
        private void WalkIf(MetacodeIf @if, int nesting)
        {
            _statements++;
            Add(@if.Origin, nesting, 1, 1 + nesting, "if", Detail(@if.Condition));
            WalkExpression(@if.Condition, nesting);
            WalkBlock(@if.Then, nesting + 1);

            var current = @if;
            while (current.Else is not null)
            {
                if (current.Else.Statements.Count == 1 && current.Else.Statements[0] is MetacodeIf nested)
                {
                    _statements++;
                    Add(nested.Origin, nesting, 1, 1, "else-if", Detail(nested.Condition));
                    WalkExpression(nested.Condition, nesting);
                    WalkBlock(nested.Then, nesting + 1);
                    current = nested;
                    continue;
                }

                _cognitive += 1;
                _contributions.Add(new MetacodeComplexityContribution(
                    "else", current.Else.Origin.IsKnown ? current.Else.Origin.StartLine : 0, nesting, 0, 1));
                WalkBlock(current.Else, nesting + 1);
                return;
            }
        }

        private void WalkSwitch(MetacodeSwitch @switch, int nesting)
        {
            var labels = @switch.Cases.Sum(@case => @case.IsDefault ? 1 : @case.Labels?.Count ?? 0);
            Add(@switch.Origin, nesting, Math.Max(0, labels - 1), 1 + nesting, "switch", $"{labels} label(s)");

            WalkExpression(@switch.Subject, nesting);
            foreach (var @case in @switch.Cases)
                WalkBlock(@case.Body, nesting + 1);
        }

        /// <summary>
        /// Expressions are walked for the decisions they contain (ternary, <c>&amp;&amp;</c>/<c>||</c>,
        /// <c>??</c>, a switch expression) and to count every node. A lambda body is skipped: it is its
        /// own unit.
        /// </summary>
        private void WalkExpression(MetacodeExpression? expression, int nesting, MetacodeBinaryOperator? parentLogical = null)
        {
            if (expression is null) return;
            _expressions++;

            switch (expression)
            {
                case MetacodeBinary binary when binary.Operator is MetacodeBinaryOperator.LogicalAnd or MetacodeBinaryOperator.LogicalOr:
                {
                    // A run of like operators is one cognitive decision, not one per operator:
                    // `a && b && c` parses as `(a && b) && c` and costs 1, `(a && b) || c` costs 2.
                    var sameOperatorAbove = parentLogical == binary.Operator;
                    Add(binary.Origin, nesting, 1, sameOperatorAbove ? 0 : 1,
                        binary.Operator == MetacodeBinaryOperator.LogicalAnd ? "&&" : "||",
                        sameOperatorAbove ? null : "logical sequence");
                    WalkExpression(binary.Left, nesting, binary.Operator);
                    WalkExpression(binary.Right, nesting, binary.Operator);
                    return;
                }

                case MetacodeBinary binary:
                    if (binary.Operator is MetacodeBinaryOperator.NullCoalesce)
                        Add(binary.Origin, nesting, 1, 0, "??");
                    WalkExpression(binary.Left, nesting);
                    WalkExpression(binary.Right, nesting);
                    return;

                case MetacodeConditional conditional:
                    Add(conditional.Origin, nesting, 1, 1 + nesting, "ternary", Detail(conditional.Condition));
                    WalkExpression(conditional.Condition, nesting);
                    WalkExpression(conditional.WhenTrue, nesting + 1);
                    WalkExpression(conditional.WhenFalse, nesting + 1);
                    return;

                case MetacodeWithExpression withExpression:
                    WalkExpression(withExpression.Target, nesting);
                    foreach (var member in withExpression.Initializers) WalkExpression(member.Value, nesting);
                    return;

                case MetacodeAnonymousObject anonymous:
                    foreach (var member in anonymous.Members) WalkExpression(member.Value, nesting);
                    return;

                case MetacodePatternMatch match:
                    // The operand is a real expression; the pattern text is not walked (the same choice a
                    // switch arm's pattern makes).
                    WalkExpression(match.Operand, nesting);
                    return;

                case MetacodePatternText:
                    return;

                case MetacodeSpread spread:
                    WalkExpression(spread.Operand, nesting);
                    return;

                case MetacodeSwitchExpression switchExpression:
                {
                    var arms = switchExpression.Arms.Count;
                    Add(switchExpression.Origin, nesting, Math.Max(0, arms - 1), 1 + nesting,
                        "switch-expression", $"{arms} arm(s)");
                    WalkExpression(switchExpression.Subject, nesting);
                    foreach (var arm in switchExpression.Arms)
                    {
                        if (arm.Guard is not null) WalkExpression(arm.Guard, nesting);
                        WalkExpression(arm.Value, nesting + 1);
                    }

                    return;
                }

                case MetacodeAssignmentExpression assignment when assignment.Operator is MetacodeAssignmentOperator.Coalesce:
                    Add(assignment.Origin, nesting, 1, 0, "??=");
                    WalkExpression(assignment.Target, nesting);
                    WalkExpression(assignment.Value, nesting);
                    return;

                case MetacodeAssignmentExpression assignment:
                    WalkExpression(assignment.Target, nesting);
                    WalkExpression(assignment.Value, nesting);
                    return;

                case MetacodeLambda:
                    // Its body is its own unit, and it may capture the host's state: nothing to add here.
                    return;

                case MetacodeInvocation invocation:
                    if (IsRecursive(invocation))
                        Add(invocation.Origin, nesting, 0, 1, "recursion", _selfName);

                    WalkExpression(invocation.Callee, nesting);
                    foreach (var argument in invocation.ArgumentList)
                        WalkExpression(argument.Value, nesting);
                    return;

                case MetacodeMemberAccess access:
                    WalkExpression(access.Target, nesting);
                    return;

                case MetacodeIndex index:
                    WalkExpression(index.Target, nesting);
                    foreach (var subscript in index.Indices) WalkExpression(subscript, nesting);
                    return;

                case MetacodeObjectCreation creation:
                    foreach (var argument in creation.Arguments ?? Array.Empty<MetacodeArgument>())
                        WalkExpression(argument.Value, nesting);
                    foreach (var element in creation.CollectionElements ?? Array.Empty<MetacodeExpression>())
                        WalkExpression(element, nesting);
                    foreach (var initializer in creation.MemberInitializers ?? Array.Empty<MetacodeMemberInitializer>())
                        WalkExpression(initializer.Value, nesting);
                    return;

                case MetacodeNewArray array:
                    foreach (var size in array.Sizes ?? Array.Empty<MetacodeExpression>()) WalkExpression(size, nesting);
                    foreach (var element in array.Elements ?? Array.Empty<MetacodeExpression>()) WalkExpression(element, nesting);
                    return;

                case MetacodeCast cast:
                    WalkExpression(cast.Operand, nesting);
                    return;

                case MetacodeTypeCheck check:
                    WalkExpression(check.Operand, nesting);
                    return;

                case MetacodeCollection collection:
                    foreach (var item in collection.Items) WalkExpression(item, nesting);
                    return;

                case MetacodeDictionary dictionary:
                    foreach (var entry in dictionary.Entries)
                    {
                        WalkExpression(entry.Key, nesting);
                        WalkExpression(entry.Value, nesting);
                    }

                    return;

                case MetacodeComprehension comprehension:
                    // A comprehension is a loop plus a filter, in one expression.
                    Add(comprehension.Origin, nesting, 1, 1 + nesting, "comprehension");
                    foreach (var clause in comprehension.Clauses)
                        WalkExpression(clause.Expression, nesting);
                    WalkExpression(comprehension.Element, nesting);
                    WalkExpression(comprehension.Value, nesting);
                    return;

                case MetacodeUnary unary:
                    WalkExpression(unary.Operand, nesting);
                    return;

                case MetacodeAwait awaitExpression:
                    WalkExpression(awaitExpression.Operand, nesting);
                    return;

                case MetacodeRange range:
                    WalkExpression(range.Start, nesting);
                    WalkExpression(range.End, nesting);
                    WalkExpression(range.Step, nesting);
                    return;

                case MetacodeIndexFromEnd fromEnd:
                    WalkExpression(fromEnd.Operand, nesting);
                    return;

                case MetacodeInterpolatedString interpolated:
                    foreach (var part in interpolated.Parts)
                        WalkExpression(part.Expression, nesting);
                    return;
            }
        }

        /// <summary>
        /// A call to itself: an unqualified <c>Name(…)</c> (which inside the member resolves to it) or
        /// <c>this.Name(…)</c>. Anything qualified is another object's member and is not recursion.
        /// </summary>
        private bool IsRecursive(MetacodeInvocation invocation)
        {
            if (_selfName is null) return false;

            return invocation.Callee switch
            {
                MetacodeIdentifier identifier => identifier.Name == _selfName,
                MetacodeMemberAccess { Member: var member, Target: MetacodeIdentifier { Name: "this" } } => member == _selfName,
                _ => false,
            };
        }

        private static string? Detail(MetacodeExpression? expression) => expression switch
        {
            null => null,
            MetacodeIdentifier identifier => identifier.Name,
            MetacodeMemberAccess access => access.Member,
            MetacodeLiteral literal => literal.Text,
            _ => expression.GetType().Name,
        };
    }
}
