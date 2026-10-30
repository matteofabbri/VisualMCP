using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// A growth class in one input size n: <c>n^Degree · log^LogPower n</c>, or <c>2^n</c>.
/// Multiplication composes loops (a loop of O(n) around a body of O(n) is O(n^2)); Max composes
/// sibling statements (the dominant term wins). This is the whole cost algebra the analysis needs,
/// and keeping it in one small type is what stops every rule from inventing its own ranking.
/// </summary>
internal readonly record struct ComplexityCost(int Degree, int LogPower, bool Exponential, bool Unknown)
    : IComparable<ComplexityCost>
{
    public static readonly ComplexityCost Constant = new(0, 0, false, false);
    public static readonly ComplexityCost Logarithmic = new(0, 1, false, false);
    public static readonly ComplexityCost Linear = new(1, 0, false, false);
    public static readonly ComplexityCost Linearithmic = new(1, 1, false, false);
    public static readonly ComplexityCost Quadratic = new(2, 0, false, false);
    public static readonly ComplexityCost ExponentialTime = new(0, 0, true, false);

    /// <summary>A bound that could not be determined: <c>while (true)</c>, unchanged recursion arguments.</summary>
    public static readonly ComplexityCost Unbounded = new(0, 0, false, true);

    public bool IsConstant => Degree == 0 && LogPower == 0 && !Exponential && !Unknown;

    /// <summary>Total order used for ranking (exponential above any polynomial, unknown above everything).</summary>
    public int Rank => Unknown ? int.MaxValue : Exponential ? 1_000_000 : Degree * 100 + LogPower;

    public static ComplexityCost OfDegree(int degree, int logPower = 0) =>
        new(Math.Clamp(degree, 0, 6), Math.Clamp(logPower, 0, 6), false, false);

    public ComplexityCost Multiply(ComplexityCost other) =>
        Unknown || other.Unknown ? Unbounded
        : Exponential || other.Exponential ? ExponentialTime
        : OfDegree(Degree + other.Degree, LogPower + other.LogPower);

    public ComplexityCost Max(ComplexityCost other)
    {
        if (Unknown || other.Unknown) return Unbounded;
        if (Exponential || other.Exponential) return ExponentialTime;
        return other.Rank > Rank ? other : this;
    }

    public int CompareTo(ComplexityCost other) => Rank.CompareTo(other.Rank);

    /// <summary>The canonical Big-O spelling used by every result and by the minComplexity parser.</summary>
    public string ToBigO()
    {
        if (Unknown) return "O(?)";
        if (Exponential) return "O(2^n)";
        if (Degree == 0 && LogPower == 0) return "O(1)";

        var parts = new List<string>(2);
        if (Degree == 1) parts.Add("n");
        else if (Degree > 1) parts.Add($"n^{Degree}");
        if (LogPower == 1) parts.Add("log n");
        else if (LogPower > 1) parts.Add($"log^{LogPower} n");

        return "O(" + string.Join(" ", parts) + ")";
    }

    /// <summary>
    /// Parses the <c>minComplexity</c> argument: "O(n)", "n", "linear", "n^2", "2^n", … are all
    /// accepted; anything unrecognised means "no threshold" rather than an error, because a typo
    /// should not hide every method.
    /// </summary>
    public static ComplexityCost ParseMin(string? text)
    {
        var key = (text ?? string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        if (key.StartsWith("o(", StringComparison.Ordinal) && key.EndsWith(')'))
            key = key[2..^1];

        return key switch
        {
            "" or "1" or "constant" => Constant,
            "logn" or "log" or "logarithmic" => Logarithmic,
            "n" or "linear" => Linear,
            "nlogn" or "linearithmic" or "nlog" => Linearithmic,
            "n^2" or "n2" or "n*n" or "quadratic" => Quadratic,
            "n^3" or "n3" or "cubic" => OfDegree(3),
            "2^n" or "exponential" or "exp" => ExponentialTime,
            "?" or "unknown" => Unbounded,
            _ => Constant,
        };
    }
}

internal sealed record LoopFact(string Kind, int Line, ComplexityCost Iterations, int NestingDepth, string Bound);

internal sealed record SpaceFact(string Kind, int Line, ComplexityCost Cost, string Basis);

internal enum RecursionPattern { SameArgument, Subtractive, Halving }

internal sealed record SelfCall(int Line, RecursionPattern Pattern, int LoopDepth);

internal sealed record RecursionFact(
    int SelfCalls,
    string Pattern,
    ComplexityCost Time,
    ComplexityCost Stack,
    IReadOnlyList<string> Evidence);

/// <summary>One analysable declaration: a method, constructor, operator, accessor or local function.</summary>
internal sealed class Callable
{
    public required IMethodSymbol Symbol { get; init; }
    public required SyntaxNode Declaration { get; init; }
    public required SyntaxNode Body { get; init; }
    public required SemanticModel Model { get; init; }
    public required Document Document { get; init; }
    public required Project Project { get; init; }
    public required string Method { get; init; }
    public required string FullName { get; init; }
    public required string ContainingType { get; init; }
    public required string Kind { get; init; }
    public required int Line { get; init; }
}

internal sealed class ComplexityResult
{
    public ComplexityCost Time { get; init; } = ComplexityCost.Constant;
    public ComplexityCost Space { get; init; } = ComplexityCost.Constant;
    public ComplexityCost Stack { get; init; } = ComplexityCost.Constant;
    public string Confidence { get; init; } = "High";
    public bool Cyclic { get; init; }
    public RecursionFact? Recursion { get; init; }
    public IReadOnlyList<LoopFact> Loops { get; init; } = [];
    public IReadOnlyList<SpaceFact> SpaceFacts { get; init; } = [];
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> Recommendations { get; init; } = [];
    public IReadOnlyList<string> Callees { get; init; } = [];
}

/// <summary>
/// The in-solution call graph. Calls to methods that live in the analysed solution are inlined with
/// their own complexity, so <c>Total()</c> that calls a linear <c>Sum()</c> is reported O(n) instead
/// of O(1). Results are memoised per symbol; a cycle returns a linear placeholder and is flagged.
/// </summary>
internal sealed class MethodCatalog
{
    private static readonly ComplexityResult CyclicPlaceholder = new()
    {
        Time = ComplexityCost.Linear,
        Space = ComplexityCost.Linear,
        Stack = ComplexityCost.Linear,
        Confidence = "Low",
        Cyclic = true,
        Warnings = ["recursion cycle detected"],
    };

    private readonly Dictionary<ISymbol, Callable> _methods = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, ComplexityResult> _results = new(SymbolEqualityComparer.Default);
    private readonly HashSet<ISymbol> _visiting = new(SymbolEqualityComparer.Default);

    public void Add(Callable method) => _methods.TryAdd(Key(method.Symbol), method);

    public bool TryGet(IMethodSymbol symbol, out Callable method)
    {
        if (_methods.TryGetValue(Key(symbol), out var found))
        {
            method = found;
            return true;
        }

        method = null!;
        return false;
    }

    public ComplexityResult Evaluate(Callable method)
    {
        var key = Key(method.Symbol);
        if (_results.TryGetValue(key, out var cached)) return cached;
        if (!_visiting.Add(key)) return CyclicPlaceholder;

        try
        {
            var result = new ComplexityWalker(method, this).Analyze();
            _results[key] = result;
            return result;
        }
        finally
        {
            _visiting.Remove(key);
        }
    }

    private static ISymbol Key(IMethodSymbol symbol) => (symbol.ReducedFrom ?? symbol).OriginalDefinition;
}

/// <summary>
/// Computes the time and space growth of one callable by walking its syntax tree. The rules are
/// deliberately explicit: a loop contributes its iteration bound, a LINQ chain contributes one pass
/// (lazy operators compose, they do not multiply), sorting adds a log factor, a linear collection
/// lookup inside a loop multiplies, recursion is classified by how its argument shrinks, and memory
/// is the peak of the sizes that are materialised plus the call-stack depth.
/// </summary>
internal sealed class ComplexityWalker
{
    private static readonly string[] OrderingOperators =
        ["OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending", "Order", "OrderDescending"];

    private static readonly string[] MaterializerOperators =
        ["ToList", "ToArray", "ToDictionary", "ToLookup", "ToHashSet", "ToListAsync", "ToArrayAsync", "ToDictionaryAsync", "ToHashSetAsync"];

    private static readonly string[] AllocatingOperators =
        ["GroupBy", "Distinct", "DistinctBy", "Union", "UnionBy", "Except", "ExceptBy", "Intersect", "IntersectBy", "Reverse", "Chunk"];

    private readonly Callable _method;
    private readonly MethodCatalog _catalog;
    private readonly SemanticModel _model;

    private readonly List<SelfCall> _selfCalls = [];
    private readonly List<LoopFact> _loops = [];
    private readonly List<SpaceFact> _spaceFacts = [];
    private readonly List<string> _evidence = [];
    private readonly List<string> _warnings = [];
    private readonly List<string> _recommendations = [];
    private readonly List<string> _callees = [];

    private ComplexityCost _loopProduct = ComplexityCost.Constant;
    private int _loopDepth;
    private bool _inferredBound;
    private bool _linearLookupInLoop;
    private bool _stringConcatInLoop;

    public ComplexityWalker(Callable method, MethodCatalog catalog)
    {
        _method = method;
        _catalog = catalog;
        _model = method.Model;
    }

    public ComplexityResult Analyze()
    {
        var local = _method.Body switch
        {
            BlockSyntax block => VisitBlock(block, 0),
            ExpressionSyntax expression => VisitExpression(expression, 0),
            _ => ComplexityCost.Constant,
        };

        var recursion = BuildRecursion(local);
        var time = recursion is null ? local : local.Max(recursion.Time);

        var space = ComplexityCost.Constant;
        foreach (var fact in _spaceFacts) space = space.Max(fact.Cost);
        var stack = recursion?.Stack ?? ComplexityCost.Constant;
        space = space.Max(stack);

        BuildNarrative(time, space, recursion);

        return new ComplexityResult
        {
            Time = time,
            Space = space,
            Stack = stack,
            Confidence = ConfidenceOf(time, space, recursion),
            Recursion = recursion,
            Loops = _loops,
            SpaceFacts = _spaceFacts,
            Evidence = _evidence.Distinct().Take(20).ToList(),
            Warnings = _warnings.Distinct().ToList(),
            Recommendations = _recommendations.Distinct().Take(5).ToList(),
            Callees = _callees.Distinct().Take(10).ToList(),
        };
    }

    // ---------------------------------------------------------------- statements

    private ComplexityCost VisitBlock(BlockSyntax block, int depth)
    {
        var cost = ComplexityCost.Constant;
        foreach (var statement in block.Statements) cost = cost.Max(VisitStatement(statement, depth));
        return cost;
    }

    private ComplexityCost VisitStatement(StatementSyntax statement, int depth)
    {
        switch (statement)
        {
            case BlockSyntax block:
                return VisitBlock(block, depth);

            case IfStatementSyntax @if:
                return VisitExpression(@if.Condition, depth)
                    .Max(VisitStatement(@if.Statement, depth))
                    .Max(@if.Else is null ? ComplexityCost.Constant : VisitStatement(@if.Else.Statement, depth));

            case SwitchStatementSyntax @switch:
            {
                var cost = VisitExpression(@switch.Expression, depth);
                foreach (var section in @switch.Sections)
                    foreach (var child in section.Statements)
                        cost = cost.Max(VisitStatement(child, depth));
                return cost;
            }

            case ForStatementSyntax @for:
                return VisitFor(@for, depth);

            case ForEachStatementSyntax @foreach:
            {
                var loop = EnterLoop(@foreach, "foreach", ComplexityCost.Linear, Describe(@foreach.Expression), @foreach.Statement, depth);
                return loop.Max(VisitExpression(@foreach.Expression, depth));
            }

            case ForEachVariableStatementSyntax @foreachVariable:
            {
                var loop = EnterLoop(@foreachVariable, "foreach", ComplexityCost.Linear, Describe(@foreachVariable.Expression), @foreachVariable.Statement, depth);
                return loop.Max(VisitExpression(@foreachVariable.Expression, depth));
            }

            case WhileStatementSyntax @while:
                return EnterLoop(@while, "while", WhileFactor(@while.Condition, @while.Statement, "while", @while), Describe(@while.Condition), @while.Statement, depth,
                    VisitExpression(@while.Condition, depth));

            case DoStatementSyntax @do:
                return EnterLoop(@do, "do", WhileFactor(@do.Condition, @do.Statement, "do", @do), Describe(@do.Condition), @do.Statement, depth,
                    VisitExpression(@do.Condition, depth));

            case TryStatementSyntax @try:
            {
                var cost = VisitBlock(@try.Block, depth);
                foreach (var handler in @try.Catches)
                    if (handler.Block is not null)
                        cost = cost.Max(VisitBlock(handler.Block, depth));
                if (@try.Finally is not null) cost = cost.Max(VisitBlock(@try.Finally.Block, depth));
                return cost;
            }

            case UsingStatementSyntax @using:
            {
                var cost = VisitExpression(@using.Expression, depth);
                return @using.Statement is null ? cost : cost.Max(VisitStatement(@using.Statement, depth));
            }

            case LockStatementSyntax @lock:
                return VisitStatement(@lock.Statement, depth);

            case FixedStatementSyntax @fixed:
                return VisitStatement(@fixed.Statement, depth);

            case CheckedStatementSyntax @checked:
                return VisitBlock(@checked.Block, depth);

            case UnsafeStatementSyntax @unsafe:
                return VisitBlock(@unsafe.Block, depth);

            case LabeledStatementSyntax labeled:
                return VisitStatement(labeled.Statement, depth);

            case LocalDeclarationStatementSyntax declaration:
            {
                var cost = ComplexityCost.Constant;
                foreach (var variable in declaration.Declaration.Variables)
                    cost = cost.Max(VisitExpression(variable.Initializer?.Value, depth));
                return cost;
            }

            case ExpressionStatementSyntax expression:
                return VisitExpression(expression.Expression, depth);

            case ReturnStatementSyntax @return:
                return VisitExpression(@return.Expression, depth);

            case ThrowStatementSyntax @throw:
                return VisitExpression(@throw.Expression, depth);

            case YieldStatementSyntax @yield:
                return VisitExpression(@yield.Expression, depth);

            // A local function is analysed as its own entry; its calls go through the catalog.
            case LocalFunctionStatementSyntax:
                return ComplexityCost.Constant;

            default:
            {
                var cost = ComplexityCost.Constant;
                foreach (var child in statement.ChildNodes())
                {
                    switch (child)
                    {
                        case LambdaExpressionSyntax or AnonymousMethodExpressionSyntax:
                            break;
                        case StatementSyntax childStatement:
                            cost = cost.Max(VisitStatement(childStatement, depth));
                            break;
                        case ExpressionSyntax childExpression:
                            cost = cost.Max(VisitExpression(childExpression, depth));
                            break;
                    }
                }
                return cost;
            }
        }
    }

    private ComplexityCost VisitFor(ForStatementSyntax loop, int depth)
    {
        var factor = ForFactor(loop);
        return EnterLoop(loop, "for", factor, Describe(loop.Condition), loop.Statement, depth,
            VisitExpression(loop.Condition, depth));
    }

    private ComplexityCost ForFactor(ForStatementSyntax loop)
    {
        if (loop.Condition is null || loop.Condition.IsKind(SyntaxKind.TrueLiteralExpression))
        {
            _warnings.Add($"for loop at line {LineOf(loop)} has no terminating condition: its bound cannot be determined");
            return ComplexityCost.Unbounded;
        }

        if (HasHalvingIncrement(loop)) return ComplexityCost.Logarithmic;

        var bound = loop.Condition is BinaryExpressionSyntax binary ? binary.Right : null;
        var degree = BoundDegree(bound);
        return degree == 0 ? ComplexityCost.Constant : ComplexityCost.OfDegree(degree);
    }

    private bool HasHalvingIncrement(ForStatementSyntax loop)
    {
        foreach (var incrementor in loop.Incrementors)
        {
            if (incrementor is not AssignmentExpressionSyntax assignment) continue;

            if (assignment.IsKind(SyntaxKind.MultiplyAssignmentExpression)
                || assignment.IsKind(SyntaxKind.DivideAssignmentExpression)
                || assignment.IsKind(SyntaxKind.LeftShiftAssignmentExpression)
                || assignment.IsKind(SyntaxKind.RightShiftAssignmentExpression))
                return true;

            if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                && assignment.Right is BinaryExpressionSyntax right
                && (right.IsKind(SyntaxKind.MultiplyExpression)
                    || right.IsKind(SyntaxKind.DivideExpression)
                    || right.IsKind(SyntaxKind.LeftShiftExpression)
                    || right.IsKind(SyntaxKind.RightShiftExpression)))
                return true;
        }

        return false;
    }

    private ComplexityCost WhileFactor(ExpressionSyntax condition, StatementSyntax? body, string kind, SyntaxNode loop)
    {
        if (condition.IsKind(SyntaxKind.TrueLiteralExpression))
        {
            _warnings.Add($"{kind} loop at line {LineOf(loop)} is unconditional: its bound cannot be determined");
            return ComplexityCost.Unbounded;
        }

        _inferredBound = true;
        return body is not null && HasHalvingUpdate(condition, body)
            ? ComplexityCost.Logarithmic
            : ComplexityCost.Linear;
    }

    private static bool HasHalvingUpdate(ExpressionSyntax condition, StatementSyntax body)
    {
        var name = condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .FirstOrDefault()?.Identifier.ValueText;
        if (name is null) return false;

        foreach (var assignment in body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is not IdentifierNameSyntax left || left.Identifier.ValueText != name) continue;

            if (assignment.IsKind(SyntaxKind.DivideAssignmentExpression)
                || assignment.IsKind(SyntaxKind.MultiplyAssignmentExpression)
                || assignment.IsKind(SyntaxKind.LeftShiftAssignmentExpression)
                || assignment.IsKind(SyntaxKind.RightShiftAssignmentExpression))
                return true;

            if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                && assignment.Right is BinaryExpressionSyntax right
                && (right.IsKind(SyntaxKind.DivideExpression)
                    || right.IsKind(SyntaxKind.MultiplyExpression)
                    || right.IsKind(SyntaxKind.LeftShiftExpression)
                    || right.IsKind(SyntaxKind.RightShiftExpression)))
                return true;
        }

        return false;
    }

    private ComplexityCost EnterLoop(
        SyntaxNode loop, string kind, ComplexityCost factor, string bound, StatementSyntax? body, int depth,
        ComplexityCost? conditionCost = null)
    {
        var line = LineOf(loop);
        var nesting = depth + 1;

        if (_loops.Count < 40)
            _loops.Add(new LoopFact(kind, line, factor, nesting, bound));

        var previousProduct = _loopProduct;
        var previousDepth = _loopDepth;
        _loopProduct = _loopProduct.Multiply(factor);
        _loopDepth = nesting;

        try
        {
            var bodyCost = body is null ? ComplexityCost.Constant : VisitStatement(body, depth + 1);
            if (conditionCost is { } condition) bodyCost = bodyCost.Max(condition);
            var cost = factor.Multiply(bodyCost);
            _evidence.Add($"{kind} loop at line {line} ({bound}) → {factor.ToBigO()} per iteration; loop and body {cost.ToBigO()}");
            return cost;
        }
        finally
        {
            _loopProduct = previousProduct;
            _loopDepth = previousDepth;
        }
    }

    /// <summary>
    /// The number of times a <c>for</c> condition runs: a constant bound is O(1) iterations, a
    /// product of input sizes raises the degree (<c>i &lt; n * n</c> is O(n^2)), anything else is O(n).
    /// </summary>
    private int BoundDegree(ExpressionSyntax? bound)
    {
        if (bound is null) return 1;
        if (_model.GetConstantValue(bound).HasValue) return 0;

        return bound switch
        {
            ParenthesizedExpressionSyntax parenthesized => BoundDegree(parenthesized.Expression),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.MultiplyExpression) =>
                Math.Min(6, BoundDegree(binary.Left) + BoundDegree(binary.Right)),
            _ => 1,
        };
    }

    // ---------------------------------------------------------------- expressions

    private ComplexityCost VisitExpression(ExpressionSyntax? expression, int depth)
    {
        if (expression is null) return ComplexityCost.Constant;

        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return VisitExpression(parenthesized.Expression, depth);

            case InvocationExpressionSyntax invocation:
                return VisitInvocation(invocation, depth);

            case ObjectCreationExpressionSyntax creation:
                return VisitObjectCreation(creation, creation.ArgumentList, creation.Initializer, depth);

            case ImplicitObjectCreationExpressionSyntax implicitCreation:
                return VisitObjectCreation(implicitCreation, implicitCreation.ArgumentList, implicitCreation.Initializer, depth);

            case ArrayCreationExpressionSyntax array:
                return VisitArrayCreation(array, depth);

            case ImplicitArrayCreationExpressionSyntax implicitArray:
                return VisitInitializer(implicitArray.Initializer, depth);

            case StackAllocArrayCreationExpressionSyntax stackAlloc:
                return stackAlloc.Type is ArrayTypeSyntax stackArray
                    ? VisitArraySizes(stackArray.RankSpecifiers, stackAlloc, depth)
                    : ComplexityCost.Constant;

            case ImplicitStackAllocArrayCreationExpressionSyntax implicitStackAlloc:
                return VisitInitializer(implicitStackAlloc.Initializer, depth);

            case AssignmentExpressionSyntax assignment:
                return VisitAssignment(assignment, depth);

            case BinaryExpressionSyntax binary:
            {
                var cost = VisitExpression(binary.Left, depth).Max(VisitExpression(binary.Right, depth));
                if (binary.IsKind(SyntaxKind.AddExpression) && IsString(binary))
                {
                    cost = cost.Max(ComplexityCost.Linear);
                    AddSpace(ComplexityCost.Linear, "string", binary, "string concatenation allocates the result");
                    if (_loopDepth > 0) _stringConcatInLoop = true;
                }
                return cost;
            }

            case ConditionalExpressionSyntax conditional:
                return VisitExpression(conditional.Condition, depth)
                    .Max(VisitExpression(conditional.WhenTrue, depth))
                    .Max(VisitExpression(conditional.WhenFalse, depth));

            case AwaitExpressionSyntax @await:
                return VisitExpression(@await.Expression, depth);

            case CastExpressionSyntax cast:
                return VisitExpression(cast.Expression, depth);

            case CheckedExpressionSyntax @checked:
                return VisitExpression(@checked.Expression, depth);

            case SwitchExpressionSyntax switchExpression:
            {
                var cost = VisitExpression(switchExpression.GoverningExpression, depth);
                foreach (var arm in switchExpression.Arms) cost = cost.Max(VisitExpression(arm.Expression, depth));
                return cost;
            }

            case QueryExpressionSyntax query:
                return VisitQuery(query, depth);

            case InterpolatedStringExpressionSyntax interpolated:
            {
                var cost = ComplexityCost.Constant;
                foreach (var hole in interpolated.Contents.OfType<InterpolationSyntax>())
                    cost = cost.Max(VisitExpression(hole.Expression, depth));
                return cost;
            }

            // A lambda's cost is paid by whoever invokes it: LINQ chains and collection methods walk
            // the body themselves, every other callee is assumed not to iterate it.
            case LambdaExpressionSyntax or AnonymousMethodExpressionSyntax:
                return ComplexityCost.Constant;

            default:
            {
                var cost = ComplexityCost.Constant;
                foreach (var child in expression.ChildNodes())
                {
                    switch (child)
                    {
                        case LambdaExpressionSyntax or AnonymousMethodExpressionSyntax:
                            break;
                        case ExpressionSyntax childExpression:
                            cost = cost.Max(VisitExpression(childExpression, depth));
                            break;
                        case ArgumentSyntax argument:
                            cost = cost.Max(VisitExpression(argument.Expression, depth));
                            break;
                        case BaseArgumentListSyntax argumentList:
                            cost = cost.Max(VisitArguments(argumentList, depth));
                            break;
                    }
                }
                return cost;
            }
        }
    }

    private ComplexityCost VisitInvocation(InvocationExpressionSyntax invocation, int depth)
    {
        var symbol = ResolveMethod(invocation);

        if (symbol is not null && IsSameMethod(symbol, _method.Symbol))
        {
            _selfCalls.Add(new SelfCall(LineOf(invocation), ClassifyRecursionArguments(invocation, symbol), _loopDepth));
            return ComplexityCost.Constant;
        }

        if (symbol is not null && _catalog.TryGet(symbol, out var callee))
        {
            var result = _catalog.Evaluate(callee);
            if (!_callees.Contains(callee.Method)) _callees.Add(callee.Method);
            if (result.Cyclic)
                _warnings.Add($"recursion cycle through {callee.Method} at line {LineOf(invocation)}: depth assumed O(n)");
            AddSpace(result.Space, "call", invocation, $"auxiliary memory of {callee.Method}");
            return result.Time.Max(VisitCallArguments(invocation, depth));
        }

        if (symbol is not null && IsLinq(symbol))
            return VisitLinqChain(invocation, symbol, depth);

        var arguments = VisitCallArguments(invocation, depth);
        if (symbol is not null && VisitKnownMethod(symbol, invocation, arguments) is { } known)
            return known;

        // A call outside the analysed solution: its own cost is unknown, assumed O(1); its
        // arguments still cost what they cost.
        return arguments;
    }

    private ComplexityCost VisitAssignment(AssignmentExpressionSyntax assignment, int depth)
    {
        var cost = VisitExpression(assignment.Right, depth).Max(VisitExpression(assignment.Left, depth));

        if (assignment.IsKind(SyntaxKind.AddAssignmentExpression) && IsString(assignment.Left))
        {
            cost = cost.Max(ComplexityCost.Linear);
            AddSpace(ComplexityCost.Linear, "string", assignment, "string concatenation allocates the result");
            if (_loopDepth > 0) _stringConcatInLoop = true;
        }

        if (assignment.Left is ElementAccessExpressionSyntax element
            && IsGrowableCollection(_model.GetTypeInfo(element.Expression).Type))
        {
            AddSpace(_loopProduct, "collection", assignment, "indexer writes grow the collection");
        }

        return cost;
    }

    private ComplexityCost VisitObjectCreation(
        BaseObjectCreationExpressionSyntax creation,
        ArgumentListSyntax? arguments,
        InitializerExpressionSyntax? initializer,
        int depth)
    {
        var cost = VisitArguments(arguments, depth).Max(VisitInitializer(initializer, depth));
        var type = _model.GetTypeInfo(creation).Type;
        var size = CreationSize(creation, type, arguments);
        AddSpace(size, "allocation", creation, $"allocates {type?.Name ?? "an object"}");
        return cost;
    }

    /// <summary>
    /// The initial size of a created object: an array sized by an input is O(n), a collection
    /// constructed with capacity or from a source is O(n), an empty one is O(1).
    /// </summary>
    private ComplexityCost CreationSize(
        BaseObjectCreationExpressionSyntax creation, ITypeSymbol? type, ArgumentListSyntax? arguments)
    {
        if (type?.SpecialType == SpecialType.System_String)
        {
            var sizeArgument = arguments?.Arguments.LastOrDefault()?.Expression;
            return sizeArgument is null ? ComplexityCost.Constant : DegreeCost(sizeArgument);
        }

        if (!IsCollectionType(type)) return ComplexityCost.Constant;

        var constructor = _model.GetSymbolInfo(creation).Symbol as IMethodSymbol;
        if (constructor is null || arguments is null) return ComplexityCost.Constant;

        for (var i = 0; i < arguments.Arguments.Count && i < constructor.Parameters.Length; i++)
        {
            var parameter = constructor.Parameters[i];
            if (parameter.Name is "capacity" or "count" or "collection" or "source" or "values")
            {
                var degree = BoundDegree(arguments.Arguments[i].Expression);
                if (degree > 0) return ComplexityCost.OfDegree(degree);
            }
        }

        return ComplexityCost.Constant;
    }

    private ComplexityCost VisitArrayCreation(ArrayCreationExpressionSyntax creation, int depth)
    {
        var cost = VisitInitializer(creation.Initializer, depth);
        return cost.Max(VisitArraySizes(creation.Type.RankSpecifiers, creation, depth));
    }

    private ComplexityCost VisitArraySizes(SyntaxList<ArrayRankSpecifierSyntax> rankSpecifiers, SyntaxNode site, int depth)
    {
        var totalDegree = 0;
        foreach (var size in rankSpecifiers.SelectMany(rank => rank.Sizes))
        {
            totalDegree += BoundDegree(size);
            VisitExpression(size, depth);
        }

        if (totalDegree == 0) return ComplexityCost.Constant;

        var cost = ComplexityCost.OfDegree(totalDegree);
        AddSpace(cost, "allocation", site, "array sized by the input");
        return cost;
    }

    private ComplexityCost VisitInitializer(InitializerExpressionSyntax? initializer, int depth)
    {
        if (initializer is null) return ComplexityCost.Constant;

        var cost = ComplexityCost.Constant;
        foreach (var expression in initializer.Expressions)
            cost = cost.Max(VisitExpression(expression, depth));
        return cost;
    }

    private ComplexityCost VisitQuery(QueryExpressionSyntax query, int depth)
    {
        var cost = VisitExpression(query.FromClause.Expression, depth).Max(ComplexityCost.Linear);
        if (query.Body.Clauses.OfType<OrderByClauseSyntax>().Any())
        {
            cost = cost.Max(ComplexityCost.Linearithmic);
            AddSpace(ComplexityCost.Linear, "linq", query, "orderby materialises a sorted copy");
        }

        return cost;
    }

    /// <summary>
    /// A LINQ chain is one pass, not one pass per operator: <c>Where(...).Select(...)</c> is O(n),
    /// <c>OrderBy</c> adds the log factor of the sort, and a lambda that itself costs O(n) multiplies
    /// (SelectMany and nested scans). Materialisers are the operators that make peak memory O(n).
    /// </summary>
    private ComplexityCost VisitLinqChain(InvocationExpressionSyntax terminal, IMethodSymbol terminalSymbol, int depth)
    {
        var operators = new List<(IMethodSymbol Symbol, InvocationExpressionSyntax Invocation)>();
        var current = terminal;
        var symbol = terminalSymbol;

        while (true)
        {
            operators.Add((symbol, current));
            if (current.Expression is not MemberAccessExpressionSyntax member) break;

            var receiver = member.Expression;
            if (receiver is InvocationExpressionSyntax next
                && ResolveMethod(next) is { } nextSymbol
                && IsLinq(nextSymbol))
            {
                current = next;
                symbol = nextSymbol;
                continue;
            }

            break;
        }

        var source = current.Expression is MemberAccessExpressionSyntax outermost ? outermost.Expression : null;
        var sourceCost = source is null ? ComplexityCost.Constant : VisitExpression(source, depth);

        var lambdaCost = ComplexityCost.Constant;
        var previousProduct = _loopProduct;
        _loopProduct = _loopProduct.Multiply(ComplexityCost.Linear);
        try
        {
            foreach (var (_, invocation) in operators)
            {
                foreach (var argument in invocation.ArgumentList.Arguments)
                {
                    switch (argument.Expression)
                    {
                        case LambdaExpressionSyntax lambda:
                            lambdaCost = lambdaCost.Max(VisitLambda(lambda, depth));
                            break;
                        case AnonymousMethodExpressionSyntax anonymous:
                            lambdaCost = lambdaCost.Max(VisitBlock(anonymous.Block, depth));
                            break;
                    }
                }
            }
        }
        finally
        {
            _loopProduct = previousProduct;
        }

        var orders = operators.Any(op => OrderingOperators.Contains(op.Symbol.Name));
        var materialises = operators.Any(op =>
            OrderingOperators.Contains(op.Symbol.Name)
            || MaterializerOperators.Contains(op.Symbol.Name)
            || AllocatingOperators.Contains(op.Symbol.Name));

        if (materialises)
            AddSpace(ComplexityCost.Linear, "linq", terminal,
                orders ? "sorting materialises the sequence" : "materialises the sequence");

        if (operators.Any(op => op.Symbol.Name == "SelectMany"))
            _warnings.Add($"SelectMany at line {LineOf(terminal)} can flatten to O(n*m); the estimate assumes O(n)");

        var factor = orders ? ComplexityCost.Linearithmic : ComplexityCost.Linear;
        return factor.Multiply(lambdaCost).Max(sourceCost);
    }

    private ComplexityCost VisitLambda(LambdaExpressionSyntax lambda, int depth) => lambda switch
    {
        SimpleLambdaExpressionSyntax simple => VisitLambdaBody(simple.Body, depth),
        ParenthesizedLambdaExpressionSyntax parenthesized => VisitLambdaBody(parenthesized.Body, depth),
        _ => ComplexityCost.Constant,
    };

    private ComplexityCost VisitLambdaBody(CSharpSyntaxNode body, int depth) => body switch
    {
        BlockSyntax block => VisitBlock(block, depth),
        ExpressionSyntax expression => VisitExpression(expression, depth),
        _ => ComplexityCost.Constant,
    };

    // ---------------------------------------------------------------- known BCL methods

    /// <summary>
    /// Costs Roslyn can know without the body: sorting, linear scans, hash lookups, string and file
    /// APIs. Returning null means "not modelled", which falls back to O(1) for the call itself.
    /// </summary>
    private ComplexityCost? VisitKnownMethod(
        IMethodSymbol symbol, InvocationExpressionSyntax invocation, ComplexityCost arguments)
    {
        var type = symbol.ContainingType;
        var typeName = type?.Name ?? string.Empty;
        var name = symbol.Name;
        var receiverType = ReceiverTypeOf(invocation);

        if (typeName == "Array")
        {
            switch (name)
            {
                case "Sort":
                    NoteSort(invocation);
                    return ComplexityCost.Linearithmic.Max(arguments);
                case "BinarySearch":
                    return ComplexityCost.Logarithmic.Max(arguments);
                case "IndexOf":
                case "LastIndexOf":
                case "Reverse":
                case "Copy":
                case "Resize":
                case "Clear":
                case "Fill":
                    return ComplexityCost.Linear.Max(arguments);
            }
        }

        if (typeName == "Parallel" && name is "For" or "ForEach" or "Invoke")
            return ComplexityCost.Linear.Max(arguments);

        if (IsCollectionType(receiverType))
        {
            switch (name)
            {
                case "Add":
                case "Enqueue":
                case "Push":
                case "TryAdd":
                case "TryPush":
                    AddSpace(_loopProduct, "collection", invocation, $"{name} stores one element");
                    if (_loopDepth > 0 && !_loopProduct.IsConstant)
                        _evidence.Add($"{typeName}.{name} at line {LineOf(invocation)} grows the collection to {_loopProduct.ToBigO()} inside a loop");
                    return ComplexityCost.Constant.Max(arguments);

                case "AddRange":
                case "InsertRange":
                case "UnionWith":
                case "ExceptWith":
                case "IntersectWith":
                    AddSpace(ComplexityCost.Linear.Multiply(_loopProduct), "collection", invocation, $"{name} copies a collection");
                    return ComplexityCost.Linear.Max(arguments);

                case "Insert":
                    AddSpace(_loopProduct, "collection", invocation, "Insert stores one element");
                    return ComplexityCost.Linear.Max(arguments);

                case "Contains":
                case "IndexOf":
                case "LastIndexOf":
                case "Find":
                case "FindIndex":
                case "FindLast":
                case "Remove":
                case "RemoveAt":
                case "RemoveAll":
                case "TryGetValue":
                case "BinarySearch":
                {
                    var lookup = LookupCost(receiverType);
                    if (!lookup.IsConstant && _loopDepth > 0)
                    {
                        _linearLookupInLoop = true;
                        _evidence.Add($"{typeName}.{name} at line {LineOf(invocation)} scans inside a loop → {lookup.ToBigO()} per lookup");
                    }

                    if (name == "FindAll")
                        AddSpace(ComplexityCost.Linear, "collection", invocation, "FindAll materialises a list");

                    return lookup.Max(arguments);
                }

                case "Sort":
                    NoteSort(invocation);
                    return ComplexityCost.Linearithmic.Max(arguments);
            }
        }

        if (type?.SpecialType == SpecialType.System_String)
        {
            if (name is "Concat" or "Join" or "Format")
            {
                AddSpace(ComplexityCost.Linear, "string", invocation, "builds a new string");
                return ComplexityCost.Linear.Max(arguments);
            }

            if (name is "Substring" or "Replace" or "Split" or "ToLower" or "ToLowerInvariant"
                or "ToUpper" or "ToUpperInvariant" or "Trim" or "TrimStart" or "TrimEnd"
                or "PadLeft" or "PadRight" or "Insert" or "Remove")
            {
                if (name is "Replace" or "Split")
                    AddSpace(ComplexityCost.Linear, "string", invocation, "allocates new strings");
                return ComplexityCost.Linear.Max(arguments);
            }

            if (name is "IndexOf" or "LastIndexOf" or "StartsWith" or "EndsWith" or "Contains" or "Compare" or "CompareTo")
                return ComplexityCost.Linear.Max(arguments);
        }

        if (typeName == "StringBuilder")
        {
            if (name is "Append" or "AppendLine" or "AppendFormat")
            {
                AddSpace(ComplexityCost.Linear, "string", invocation, "the builder holds the whole text");
                return ComplexityCost.Constant.Max(arguments);
            }

            if (name == "ToString")
                AddSpace(ComplexityCost.Linear, "string", invocation, "copies the builder buffer");
        }

        if (typeName == "Regex" && name is "IsMatch" or "Match" or "Matches" or "Replace" or "Split")
            return ComplexityCost.Linear.Max(arguments);

        if (typeName == "File" && name is "ReadAllText" or "ReadAllLines" or "ReadLines")
        {
            AddSpace(ComplexityCost.Linear, "io", invocation, "reads the whole file");
            return ComplexityCost.Linear.Max(arguments);
        }

        return null;
    }

    private void NoteSort(SyntaxNode site)
    {
        _recommendations.Add($"Sorting at line {LineOf(site)} dominates the cost at O(n log n); a top-k heap avoids the full sort.");
    }

    private static ComplexityCost LookupCost(ITypeSymbol? type)
    {
        var name = type?.OriginalDefinition?.Name ?? type?.Name ?? string.Empty;

        return name switch
        {
            "Dictionary" or "HashSet" or "ConcurrentDictionary" or "ConcurrentBag"
                or "ImmutableDictionary" or "ImmutableHashSet"
                or "IDictionary" or "IReadOnlyDictionary" or "ISet" or "IReadOnlySet" => ComplexityCost.Constant,
            "SortedDictionary" or "SortedSet" or "SortedList"
                or "ImmutableSortedDictionary" or "ImmutableSortedSet" => ComplexityCost.Logarithmic,
            _ => ComplexityCost.Linear,
        };
    }

    private static bool IsCollectionType(ITypeSymbol? type)
    {
        if (type is null) return false;
        if (type is IArrayTypeSymbol) return true;
        if (type.SpecialType == SpecialType.System_String) return false;

        var name = type.OriginalDefinition?.Name ?? type.Name;
        if (name == "StringBuilder") return true;

        return type.AllInterfaces.Any(i => i.OriginalDefinition.Name is
            "IEnumerable" or "ICollection" or "IList" or "IDictionary" or "ISet" or "IReadOnlyCollection");
    }

    private static bool IsGrowableCollection(ITypeSymbol? type)
    {
        if (type is null) return false;
        if (type is IArrayTypeSymbol) return false;

        var name = type.OriginalDefinition?.Name ?? type.Name;
        return name is "Dictionary" or "SortedDictionary" or "ConcurrentDictionary" or "ImmutableDictionary"
            or "HashSet" or "SortedSet"
            || type.AllInterfaces.Any(i => i.OriginalDefinition.Name == "IDictionary");
    }

    private static bool IsLinq(IMethodSymbol? symbol) =>
        symbol?.ContainingType?.Name is "Enumerable" or "Queryable"
        && symbol.ContainingNamespace?.ToDisplayString() == "System.Linq";

    // ---------------------------------------------------------------- recursion

    private RecursionFact? BuildRecursion(ComplexityCost local)
    {
        if (_selfCalls.Count == 0) return null;

        var halving = _selfCalls.Count(call => call.Pattern == RecursionPattern.Halving);
        var subtractive = _selfCalls.Count(call => call.Pattern == RecursionPattern.Subtractive);
        var same = _selfCalls.Count - halving - subtractive;
        var calls = _selfCalls.Count;
        var firstLine = _selfCalls.Min(call => call.Line);

        string pattern;
        ComplexityCost time;
        ComplexityCost stack;
        var evidence = new List<string>();

        if (halving == 0 && subtractive == 0 && same > 0)
        {
            pattern = "same-argument";
            time = ComplexityCost.Unbounded;
            stack = ComplexityCost.Unbounded;
            evidence.Add($"recursive call at line {firstLine} passes its argument unchanged → unbounded depth");
            _warnings.Add("A recursive call does not reduce its argument: the method recurses until the stack overflows.");
        }
        else if (subtractive > 0)
        {
            pattern = calls == 1 ? "subtractive" : "exponential";
            time = calls == 1 ? local.Multiply(ComplexityCost.Linear) : ComplexityCost.ExponentialTime;
            stack = ComplexityCost.Linear;
            evidence.Add(calls == 1
                ? $"one recursive call at line {firstLine} reduces the argument by a constant → O(n) levels, O(n) stack"
                : $"{calls} recursive calls reduce the argument by a constant → O(2^n) time, O(n) stack");
        }
        else
        {
            pattern = "halving";
            stack = ComplexityCost.Logarithmic;

            if (calls == 1)
            {
                time = local.Max(ComplexityCost.Logarithmic);
                evidence.Add($"one recursive call at line {firstLine} halves the argument → O(log n) levels");
            }
            else if (calls == 2)
            {
                time = local.IsConstant ? ComplexityCost.Linear : local.Multiply(ComplexityCost.Logarithmic);
                evidence.Add("two recursive calls halve the argument → O(n) leaves, O(n log n) when each level does O(n) work");
            }
            else
            {
                var degree = Math.Max(1, (int)Math.Ceiling(Math.Log2(calls)));
                time = ComplexityCost.OfDegree(degree).Max(local.Multiply(ComplexityCost.Logarithmic));
                evidence.Add($"{calls} recursive calls halve the argument → about O(n^{degree}) leaves (approximation)");
                _warnings.Add("Divide-and-conquer with more than two branches: the exponent is approximated.");
            }
        }

        if (_selfCalls.Any(call => call.LoopDepth > 0))
        {
            _warnings.Add("A recursive call sits inside a loop: the real cost multiplies the two factors.");
            evidence.Add("a recursive call is inside a loop");
        }

        return new RecursionFact(calls, pattern, time, stack, evidence);
    }

    private RecursionPattern ClassifyRecursionArguments(InvocationExpressionSyntax invocation, IMethodSymbol symbol)
    {
        var parameters = symbol.Parameters;
        var arguments = invocation.ArgumentList.Arguments;
        if (parameters.Length == 0 || arguments.Count == 0) return RecursionPattern.SameArgument;

        var patterns = new List<RecursionPattern>(arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var expression = argument.Expression;

            var parameter = argument.NameColon is { } named
                ? parameters.FirstOrDefault(p => p.Name == named.Name.Identifier.ValueText)
                : i < parameters.Length ? parameters[i] : null;

            if (argument.NameColon is not null && parameter is null) continue;
            patterns.Add(ClassifyArgument(expression, 0));
        }

        if (patterns.Count == 0) return RecursionPattern.SameArgument;
        if (patterns.Any(p => p == RecursionPattern.Halving)) return RecursionPattern.Halving;
        if (patterns.Any(p => p == RecursionPattern.Subtractive)) return RecursionPattern.Subtractive;
        return RecursionPattern.SameArgument;
    }

    private RecursionPattern ClassifyArgument(ExpressionSyntax argument, int depth)
    {
        switch (argument)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return ClassifyArgument(parenthesized.Expression, depth);

            case BinaryExpressionSyntax binary:
                if (binary.IsKind(SyntaxKind.DivideExpression)
                    || binary.IsKind(SyntaxKind.RightShiftExpression)
                    || binary.IsKind(SyntaxKind.UnsignedRightShiftExpression))
                    return RecursionPattern.Halving;

                if (binary.IsKind(SyntaxKind.AddExpression) || binary.IsKind(SyntaxKind.SubtractExpression))
                    return ResolvesToHalving(binary.Left, depth) || ResolvesToHalving(binary.Right, depth)
                        ? RecursionPattern.Halving
                        : RecursionPattern.Subtractive;

                if (binary.IsKind(SyntaxKind.MultiplyExpression) || binary.IsKind(SyntaxKind.LeftShiftExpression))
                    return RecursionPattern.Subtractive;

                return RecursionPattern.SameArgument;

            case IdentifierNameSyntax identifier:
            {
                var initializer = LocalInitializer(identifier);
                return initializer is not null && depth < 2
                    ? ClassifyArgument(initializer, depth + 1)
                    : RecursionPattern.SameArgument;
            }

            default:
                return RecursionPattern.SameArgument;
        }
    }

    private bool ResolvesToHalving(ExpressionSyntax expression, int depth) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => ResolvesToHalving(parenthesized.Expression, depth),
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.DivideExpression)
            || binary.IsKind(SyntaxKind.RightShiftExpression)
            || binary.IsKind(SyntaxKind.UnsignedRightShiftExpression) => true,
        IdentifierNameSyntax identifier => LocalInitializer(identifier) is { } initializer
            && depth < 2
            && ResolvesToHalving(initializer, depth + 1),
        _ => false,
    };

    private ExpressionSyntax? LocalInitializer(IdentifierNameSyntax identifier)
    {
        var symbol = _model.GetSymbolInfo(identifier).Symbol as ILocalSymbol;
        var declarator = symbol?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax;
        return declarator?.Initializer?.Value;
    }

    // ---------------------------------------------------------------- helpers

    private ComplexityCost VisitCallArguments(InvocationExpressionSyntax invocation, int depth)
    {
        var cost = VisitArguments(invocation.ArgumentList, depth);
        if (ReceiverOf(invocation) is { } receiver) cost = cost.Max(VisitExpression(receiver, depth));
        return cost;
    }

    private ComplexityCost VisitArguments(BaseArgumentListSyntax? argumentList, int depth)
    {
        var cost = ComplexityCost.Constant;
        if (argumentList is null) return cost;

        foreach (var argument in argumentList.Arguments)
        {
            if (argument.Expression is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax) continue;
            cost = cost.Max(VisitExpression(argument.Expression, depth));
        }

        return cost;
    }

    private static ExpressionSyntax? ReceiverOf(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax member ? member.Expression : null;

    private ITypeSymbol? ReceiverTypeOf(InvocationExpressionSyntax invocation) =>
        ReceiverOf(invocation) is { } receiver ? _model.GetTypeInfo(receiver).Type : null;

    private IMethodSymbol? ResolveMethod(InvocationExpressionSyntax invocation)
    {
        var info = _model.GetSymbolInfo(invocation);
        return (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
    }

    private static bool IsSameMethod(IMethodSymbol candidate, IMethodSymbol method) =>
        SymbolEqualityComparer.Default.Equals(
            (candidate.ReducedFrom ?? candidate).OriginalDefinition,
            (method.ReducedFrom ?? method).OriginalDefinition);

    private bool IsString(ExpressionSyntax expression)
    {
        var info = _model.GetTypeInfo(expression);
        return (info.Type ?? info.ConvertedType)?.SpecialType == SpecialType.System_String;
    }

    private ComplexityCost DegreeCost(ExpressionSyntax expression)
    {
        var degree = BoundDegree(expression);
        return degree == 0 ? ComplexityCost.Constant : ComplexityCost.OfDegree(degree);
    }

    private void AddSpace(ComplexityCost cost, string kind, SyntaxNode site, string basis)
    {
        if (cost.IsConstant || _spaceFacts.Count >= 40) return;
        _spaceFacts.Add(new SpaceFact(kind, LineOf(site), cost, basis));
    }

    private void BuildNarrative(ComplexityCost time, ComplexityCost space, RecursionFact? recursion)
    {
        if (_loops.Count > 0)
            _evidence.Insert(0, $"{_loops.Count} loop(s) in the body, combined time {time.ToBigO()}");

        if (time.Degree >= 2)
            _recommendations.Add("Nested loops multiply their bounds: collapse the inner scan with a Dictionary/HashSet lookup, or sort and merge.");

        if (_linearLookupInLoop)
            _recommendations.Add("A linear collection lookup (Contains/IndexOf/Remove) runs inside a loop: a HashSet/Dictionary makes each lookup O(1).");

        if (_stringConcatInLoop)
            _recommendations.Add("String += inside a loop copies the whole string on every iteration: use a StringBuilder.");

        if (recursion is not null)
        {
            if (recursion.Pattern == "exponential")
                _recommendations.Add("The recursion branches without memoisation: cache sub-results in a Dictionary or use dynamic programming.");
            else if (recursion.Pattern == "subtractive" && recursion.SelfCalls == 1)
                _recommendations.Add("Recursion reduces the argument by a constant: an iterative loop removes the O(n) call stack.");
        }

        if (space.Degree >= 1 && _spaceFacts.Any(fact => fact.Kind == "linq"))
            _recommendations.Add("A LINQ chain materialises a full copy (ToList/ToArray/OrderBy): stream with IEnumerable or process in batches.");
    }

    private string ConfidenceOf(ComplexityCost time, ComplexityCost space, RecursionFact? recursion)
    {
        if (time.Unknown || space.Unknown) return "Low";
        if (recursion is not null || _inferredBound) return "Medium";
        return "High";
    }

    private int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string Describe(SyntaxNode? node)
    {
        var text = node?.ToString().Trim();
        if (string.IsNullOrEmpty(text)) return "unknown bound";
        return text.Length <= 80 ? text : text[..80] + "…";
    }
}
