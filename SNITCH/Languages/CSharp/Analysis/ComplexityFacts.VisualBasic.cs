using Microsoft.CodeAnalysis;
using Sy = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using VbKind = Microsoft.CodeAnalysis.VisualBasic.SyntaxKind;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// The Visual Basic half of the Big-O walk. The growth algebra, the loop bookkeeping, the recursion
/// classification and the narrative are the ones the C# walk uses — what differs is the syntax:
/// <c>For … Next</c> and <c>Do … Loop</c> instead of <c>for</c>/<c>do</c>, <c>For Each</c> written as
/// two words, <c>Select Case</c> instead of <c>switch</c>, <c>If(a, b, c)</c> as the ternary, <c>&amp;</c>
/// as the concatenation operator and the extension-method form of LINQ.
/// </summary>
internal sealed partial class ComplexityWalker
{
    /// <summary>The body of a VB callable, walked statement by statement.</summary>
    private ComplexityCost VisitVisualBasicBody(SyntaxNode body)
    {
        var cost = ComplexityCost.Constant;
        foreach (var statement in VisualBasicStatements(body)) cost = cost.Max(VisitVisualBasicStatement(statement, 0));
        return cost;
    }

    private static IReadOnlyList<Sy.StatementSyntax> VisualBasicStatements(SyntaxNode body) => body switch
    {
        Sy.MethodBlockSyntax method           => method.Statements,
        Sy.ConstructorBlockSyntax constructor => constructor.Statements,
        Sy.OperatorBlockSyntax @operator      => @operator.Statements,
        Sy.AccessorBlockSyntax accessor       => accessor.Statements,
        _                                     => [],
    };

    private ComplexityCost VisitVisualBasicStatements(IReadOnlyList<Sy.StatementSyntax> statements, int depth)
    {
        var cost = ComplexityCost.Constant;
        foreach (var statement in statements) cost = cost.Max(VisitVisualBasicStatement(statement, depth));
        return cost;
    }

    // ---------------------------------------------------------------- statements

    private ComplexityCost VisitVisualBasicStatement(Sy.StatementSyntax statement, int depth)
    {
        switch (statement)
        {
            case Sy.MultiLineIfBlockSyntax @if:
            {
                var cost = VisitVisualBasicExpression(@if.IfStatement.Condition, depth)
                    .Max(VisitVisualBasicStatements(@if.Statements, depth));

                foreach (var elseIf in @if.ElseIfBlocks)
                {
                    cost = cost.Max(VisitVisualBasicExpression(elseIf.ElseIfStatement.Condition, depth))
                        .Max(VisitVisualBasicStatements(elseIf.Statements, depth));
                }

                if (@if.ElseBlock is { } elseBlock) cost = cost.Max(VisitVisualBasicStatements(elseBlock.Statements, depth));
                return cost;
            }

            case Sy.SingleLineIfStatementSyntax @if:
            {
                var cost = VisitVisualBasicExpression(@if.Condition, depth)
                    .Max(VisitVisualBasicStatements(@if.Statements, depth));

                if (@if.ElseClause is { } elseClause) cost = cost.Max(VisitVisualBasicStatements(elseClause.Statements, depth));
                return cost;
            }

            case Sy.ForBlockSyntax @for:
                return VisitVisualBasicFor(@for, depth);

            case Sy.ForEachBlockSyntax @foreach:
                return EnterLoop(@foreach, "for each", ComplexityCost.Linear,
                    Describe(@foreach.ForEachStatement.Expression), depth,
                    () => VisitVisualBasicStatements(@foreach.Statements, depth + 1),
                    VisitVisualBasicExpression(@foreach.ForEachStatement.Expression, depth));

            case Sy.WhileBlockSyntax @while:
                return EnterVisualBasicConditionalLoop(@while, "while", @while.WhileStatement.Condition,
                    @while.Statements, depth);

            case Sy.DoLoopBlockSyntax @do:
            {
                var condition = @do.DoStatement.WhileOrUntilClause ?? @do.LoopStatement.WhileOrUntilClause;
                return EnterVisualBasicConditionalLoop(@do, "do", condition?.Condition, @do.Statements, depth);
            }

            case Sy.SelectBlockSyntax @select:
            {
                var labels = @select.CaseBlocks.Count;
                var cost = VisitVisualBasicExpression(@select.SelectStatement.Expression, depth);

                _ = labels;
                foreach (var block in @select.CaseBlocks)
                    cost = cost.Max(VisitVisualBasicStatements(block.Statements, depth));

                return cost;
            }

            case Sy.TryBlockSyntax @try:
            {
                var cost = VisitVisualBasicStatements(@try.Statements, depth);
                foreach (var handler in @try.CatchBlocks)
                    cost = cost.Max(VisitVisualBasicStatements(handler.Statements, depth));
                if (@try.FinallyBlock is { } finallyBlock)
                    cost = cost.Max(VisitVisualBasicStatements(finallyBlock.Statements, depth));
                return cost;
            }

            case Sy.UsingBlockSyntax @using:
                return VisitVisualBasicStatements(@using.Statements, depth);

            case Sy.SyncLockBlockSyntax @lock:
                return VisitVisualBasicStatements(@lock.Statements, depth);

            case Sy.LocalDeclarationStatementSyntax declaration:
            {
                var cost = ComplexityCost.Constant;
                foreach (var declarator in declaration.Declarators)
                    cost = cost.Max(VisitVisualBasicExpression(declarator.Initializer?.Value, depth));
                return cost;
            }

            case Sy.AssignmentStatementSyntax assignment:
                return VisitVisualBasicAssignment(assignment, depth);

            case Sy.ExpressionStatementSyntax expression:
                return VisitVisualBasicExpression(expression.Expression, depth);

            case Sy.ReturnStatementSyntax @return:
                return VisitVisualBasicExpression(@return.Expression, depth);

            case Sy.ThrowStatementSyntax @throw:
                return VisitVisualBasicExpression(@throw.Expression, depth);

            case Sy.YieldStatementSyntax @yield:
                return VisitVisualBasicExpression(@yield.Expression, depth);

            default:
            {
                var cost = ComplexityCost.Constant;
                foreach (var child in statement.ChildNodes())
                {
                    switch (child)
                    {
                        case Sy.LambdaExpressionSyntax:
                            break;
                        case Sy.StatementSyntax childStatement:
                            cost = cost.Max(VisitVisualBasicStatement(childStatement, depth));
                            break;
                        case Sy.ExpressionSyntax childExpression:
                            cost = cost.Max(VisitVisualBasicExpression(childExpression, depth));
                            break;
                    }
                }

                return cost;
            }
        }
    }

    /// <summary>
    /// <c>For i = 0 To n - 1</c> runs n times; <c>For i = 0 To 9</c> runs a constant number of times.
    /// A <c>Step</c> clause that would halve the counter is not modelled: VB has no compound step.
    /// </summary>
    private ComplexityCost VisitVisualBasicFor(Sy.ForBlockSyntax @for, int depth)
    {
        var to = @for.ForStatement.ToValue;
        var factor = to is null || _model.GetConstantValue(to).HasValue
            ? ComplexityCost.Constant
            : ComplexityCost.Linear;

        // A constant-bounded loop still repeats its body, so the body is walked at the current depth
        // but only multiplied by the factor: exactly the C# `for` rule.
        var condition = VisitVisualBasicExpression(@for.ForStatement.FromValue, depth)
            .Max(VisitVisualBasicExpression(to, depth))
            .Max(VisitVisualBasicExpression(@for.ForStatement.StepClause?.StepValue, depth));

        return EnterLoop(@for, "for", factor, Describe(to), depth,
            () => VisitVisualBasicStatements(@for.Statements, depth + 1), condition);
    }

    /// <summary>A <c>While</c>/<c>Do</c> loop: <c>While True</c> has no bound and is reported as unknown.</summary>
    private ComplexityCost EnterVisualBasicConditionalLoop(
        SyntaxNode loop, string kind, Sy.ExpressionSyntax? condition,
        IReadOnlyList<Sy.StatementSyntax> statements, int depth)
    {
        if (condition is null)
            return EnterLoop(loop, kind, ComplexityCost.Linear, "no condition", depth,
                () => VisitVisualBasicStatements(statements, depth + 1), null);

        if (condition.IsKind(VbKind.TrueLiteralExpression))
        {
            _warnings.Add($"{kind} loop at line {LineOf(loop)} is unconditional: its bound cannot be determined");
            return EnterLoop(loop, kind, ComplexityCost.Unbounded, Describe(condition), depth,
                () => VisitVisualBasicStatements(statements, depth + 1),
                VisitVisualBasicExpression(condition, depth));
        }

        _inferredBound = true;
        return EnterLoop(loop, kind, ComplexityCost.Linear, Describe(condition), depth,
            () => VisitVisualBasicStatements(statements, depth + 1),
            VisitVisualBasicExpression(condition, depth));
    }

    private ComplexityCost VisitVisualBasicAssignment(Sy.AssignmentStatementSyntax assignment, int depth)
    {
        var cost = VisitVisualBasicExpression(assignment.Right, depth)
            .Max(VisitVisualBasicExpression(assignment.Left, depth));

        // VB concatenates strings with `&` (and with `+` when both sides are strings).
        if (assignment.IsKind(VbKind.ConcatenateAssignmentStatement) && IsVisualBasicString(assignment.Left))
        {
            cost = cost.Max(ComplexityCost.Linear);
            AddSpace(ComplexityCost.Linear, "string", assignment, "string concatenation allocates the result");
            if (_loopDepth > 0) _stringConcatInLoop = true;
        }

        return cost;
    }

    // ---------------------------------------------------------------- expressions

    private ComplexityCost VisitVisualBasicExpression(Sy.ExpressionSyntax? expression, int depth)
    {
        if (expression is null) return ComplexityCost.Constant;

        switch (expression)
        {
            case Sy.ParenthesizedExpressionSyntax parenthesized:
                return VisitVisualBasicExpression(parenthesized.Expression, depth);

            case Sy.InvocationExpressionSyntax invocation:
                return VisitVisualBasicInvocation(invocation, depth);

            case Sy.ObjectCreationExpressionSyntax creation:
                return VisitVisualBasicCreation(creation, depth);

            case Sy.ArrayCreationExpressionSyntax array:
                return VisitVisualBasicArrayCreation(array, depth);

            case Sy.BinaryExpressionSyntax binary:
            {
                var cost = VisitVisualBasicExpression(binary.Left, depth)
                    .Max(VisitVisualBasicExpression(binary.Right, depth));

                if (binary.IsKind(VbKind.ConcatenateExpression) && IsVisualBasicString(binary))
                {
                    cost = cost.Max(ComplexityCost.Linear);
                    AddSpace(ComplexityCost.Linear, "string", binary, "string concatenation allocates the result");
                    if (_loopDepth > 0) _stringConcatInLoop = true;
                }

                return cost;
            }

            case Sy.TernaryConditionalExpressionSyntax conditional:
                return VisitVisualBasicExpression(conditional.Condition, depth)
                    .Max(VisitVisualBasicExpression(conditional.WhenTrue, depth))
                    .Max(VisitVisualBasicExpression(conditional.WhenFalse, depth));

            case Sy.BinaryConditionalExpressionSyntax coalesce:
                return VisitVisualBasicExpression(coalesce.FirstExpression, depth)
                    .Max(VisitVisualBasicExpression(coalesce.SecondExpression, depth));

            case Sy.AwaitExpressionSyntax @await:
                return VisitVisualBasicExpression(@await.Expression, depth);

            case Sy.CastExpressionSyntax cast:
                return VisitVisualBasicExpression(cast.Expression, depth);

            case Sy.QueryExpressionSyntax query:
            {
                // VB query syntax reads like a pipeline: one pass over the source it reads, plus the
                // cost of whatever the clauses do. The clauses are walked as ordinary expressions.
                var cost = ComplexityCost.Linear;
                foreach (var child in query.ChildNodes())
                {
                    if (child is Sy.ExpressionSyntax childExpression)
                        cost = cost.Max(VisitVisualBasicExpression(childExpression, depth));
                }

                return cost;
            }

            // A lambda's cost is paid by whoever invokes it: LINQ chains walk the body themselves.
            case Sy.LambdaExpressionSyntax:
                return ComplexityCost.Constant;

            case Sy.InterpolatedStringExpressionSyntax interpolated:
            {
                var cost = ComplexityCost.Constant;
                foreach (var content in interpolated.Contents)
                {
                    if (content is Sy.InterpolationSyntax hole)
                        cost = cost.Max(VisitVisualBasicExpression(hole.Expression, depth));
                }

                return cost;
            }

            default:
            {
                var cost = ComplexityCost.Constant;
                foreach (var child in expression.ChildNodes())
                {
                    switch (child)
                    {
                        case Sy.LambdaExpressionSyntax:
                            break;
                        case Sy.ExpressionSyntax childExpression:
                            cost = cost.Max(VisitVisualBasicExpression(childExpression, depth));
                            break;
                        case Sy.ArgumentListSyntax arguments:
                            cost = cost.Max(VisitVisualBasicArguments(arguments, depth));
                            break;
                    }
                }

                return cost;
            }
        }
    }

    private ComplexityCost VisitVisualBasicInvocation(Sy.InvocationExpressionSyntax invocation, int depth)
    {
        var symbol = ResolveVisualBasicMethod(invocation);

        if (symbol is not null && IsSameMethod(symbol, _method.Symbol))
        {
            _selfCalls.Add(new SelfCall(
                LineOf(invocation), ClassifyVisualBasicRecursionArguments(invocation, symbol), _loopDepth));
            return ComplexityCost.Constant;
        }

        if (symbol is not null && _catalog.TryGet(symbol, out var callee))
        {
            var result = _catalog.Evaluate(callee);
            if (!_callees.Contains(callee.Method)) _callees.Add(callee.Method);
            if (result.Cyclic)
                _warnings.Add($"recursion cycle through {callee.Method} at line {LineOf(invocation)}: depth assumed O(n)");
            AddSpace(result.Space, "call", invocation, $"auxiliary memory of {callee.Method}");
            return result.Time.Max(VisitVisualBasicCallArguments(invocation, depth));
        }

        if (symbol is not null && IsLinq(symbol))
            return VisitVisualBasicLinqChain(invocation, symbol, depth);

        var arguments = VisitVisualBasicCallArguments(invocation, depth);
        if (symbol is not null &&
            VisitKnownMethod(symbol, invocation, ReceiverTypeOfVisualBasic(invocation), arguments) is { } known)
            return known;

        return arguments;
    }

    private ComplexityCost VisitVisualBasicCreation(Sy.ObjectCreationExpressionSyntax creation, int depth)
    {
        var cost = VisitVisualBasicArguments(creation.ArgumentList, depth);
        var type = _model.GetTypeInfo(creation).Type;
        var size = ComplexityCost.Constant;

        if (type is not null && (IsCollectionType(type) || type.SpecialType == SpecialType.System_String))
        {
            var constructor = _model.GetSymbolInfo(creation).Symbol as IMethodSymbol;
            var arguments = creation.ArgumentList?.Arguments ?? default;

            for (var index = 0; constructor is not null && index < arguments.Count && index < constructor.Parameters.Length; index++)
            {
                if (constructor.Parameters[index].Name is not ("capacity" or "count" or "collection" or "source" or "values"))
                    continue;

                var argument = ArgumentExpression(arguments[index]);
                if (argument is not null && !_model.GetConstantValue(argument).HasValue)
                    size = ComplexityCost.Linear;
            }
        }

        AddSpace(size, "allocation", creation, $"allocates {type?.Name ?? "an object"}");
        return cost;
    }

    private ComplexityCost VisitVisualBasicArrayCreation(Sy.ArrayCreationExpressionSyntax creation, int depth)
    {
        var cost = ComplexityCost.Constant;
        var degree = 0;

        foreach (var argument in creation.ArrayBounds?.Arguments ?? default)
        {
            var expression = ArgumentExpression(argument);
            if (expression is null) continue;

            cost = cost.Max(VisitVisualBasicExpression(expression, depth));
            if (!_model.GetConstantValue(expression).HasValue) degree++;
        }

        if (degree > 0) AddSpace(ComplexityCost.OfDegree(Math.Min(6, degree)), "allocation", creation, "array sized by the input");
        return cost;
    }

    /// <summary>
    /// A VB LINQ chain is the same pipeline as the C# one: the operators are extension methods, so the
    /// chain is a series of member accesses whose receiver is the next invocation.
    /// </summary>
    private ComplexityCost VisitVisualBasicLinqChain(
        Sy.InvocationExpressionSyntax terminal, IMethodSymbol terminalSymbol, int depth)
    {
        var operators = new List<(IMethodSymbol Symbol, Sy.InvocationExpressionSyntax Invocation)>();
        var current = terminal;
        var symbol = terminalSymbol;

        while (true)
        {
            operators.Add((symbol, current));
            if (current.Expression is not Sy.MemberAccessExpressionSyntax member) break;

            if (member.Expression is Sy.InvocationExpressionSyntax next
                && ResolveVisualBasicMethod(next) is { } nextSymbol
                && IsLinq(nextSymbol))
            {
                current = next;
                symbol = nextSymbol;
                continue;
            }

            break;
        }

        var source = current.Expression is Sy.MemberAccessExpressionSyntax outermost
            ? outermost.Expression
            : null;
        var sourceCost = source is null ? ComplexityCost.Constant : VisitVisualBasicExpression(source, depth);

        var lambdaCost = ComplexityCost.Constant;
        var previousProduct = _loopProduct;
        _loopProduct = _loopProduct.Multiply(ComplexityCost.Linear);
        try
        {
            foreach (var (_, invocation) in operators)
            {
                foreach (var argument in invocation.ArgumentList?.Arguments ?? default)
                {
                    if (ArgumentExpression(argument) is not Sy.LambdaExpressionSyntax lambda) continue;
                    lambdaCost = lambdaCost.Max(VisitVisualBasicLambda(lambda, depth));
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

    private ComplexityCost VisitVisualBasicLambda(Sy.LambdaExpressionSyntax lambda, int depth) => lambda switch
    {
        Sy.MultiLineLambdaExpressionSyntax multiLine => VisitVisualBasicStatements(multiLine.Statements, depth),
        Sy.SingleLineLambdaExpressionSyntax singleLine => singleLine.Body is Sy.ExpressionSyntax expression
            ? VisitVisualBasicExpression(expression, depth)
            : ComplexityCost.Constant,
        _ => ComplexityCost.Constant,
    };

    private ComplexityCost VisitVisualBasicCallArguments(Sy.InvocationExpressionSyntax invocation, int depth)
    {
        var cost = VisitVisualBasicArguments(invocation.ArgumentList, depth);
        if (invocation.Expression is Sy.MemberAccessExpressionSyntax member)
            cost = cost.Max(VisitVisualBasicExpression(member.Expression, depth));
        return cost;
    }

    private ComplexityCost VisitVisualBasicArguments(Sy.ArgumentListSyntax? argumentList, int depth)
    {
        var cost = ComplexityCost.Constant;
        if (argumentList is null) return cost;

        foreach (var argument in argumentList.Arguments)
        {
            var expression = ArgumentExpression(argument);
            if (expression is null or Sy.LambdaExpressionSyntax) continue;
            cost = cost.Max(VisitVisualBasicExpression(expression, depth));
        }

        return cost;
    }

    /// <summary>VB passes every argument through one node kind, named or positional.</summary>
    private static Sy.ExpressionSyntax? ArgumentExpression(Sy.ArgumentSyntax argument) =>
        argument is Sy.SimpleArgumentSyntax simple ? simple.Expression : null;

    private ITypeSymbol? ReceiverTypeOfVisualBasic(Sy.InvocationExpressionSyntax invocation) =>
        invocation.Expression is Sy.MemberAccessExpressionSyntax member
            ? _model.GetTypeInfo(member.Expression).Type
            : null;

    private IMethodSymbol? ResolveVisualBasicMethod(Sy.InvocationExpressionSyntax invocation)
    {
        var info = _model.GetSymbolInfo(invocation);
        return (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
    }

    private bool IsVisualBasicString(Sy.ExpressionSyntax expression)
    {
        var info = _model.GetTypeInfo(expression);
        return (info.Type ?? info.ConvertedType)?.SpecialType == SpecialType.System_String;
    }

    // ---------------------------------------------------------------- recursion

    private RecursionPattern ClassifyVisualBasicRecursionArguments(
        Sy.InvocationExpressionSyntax invocation, IMethodSymbol symbol)
    {
        var arguments = invocation.ArgumentList?.Arguments ?? default;
        if (symbol.Parameters.Length == 0 || arguments.Count == 0) return RecursionPattern.SameArgument;

        var patterns = new List<RecursionPattern>(arguments.Count);
        foreach (var argument in arguments)
        {
            if (ArgumentExpression(argument) is { } expression)
                patterns.Add(ClassifyVisualBasicArgument(expression, 0));
        }

        if (patterns.Count == 0) return RecursionPattern.SameArgument;
        if (patterns.Any(pattern => pattern == RecursionPattern.Halving)) return RecursionPattern.Halving;
        if (patterns.Any(pattern => pattern == RecursionPattern.Subtractive)) return RecursionPattern.Subtractive;
        return RecursionPattern.SameArgument;
    }

    private RecursionPattern ClassifyVisualBasicArgument(Sy.ExpressionSyntax argument, int depth)
    {
        switch (argument)
        {
            case Sy.ParenthesizedExpressionSyntax parenthesized:
                return ClassifyVisualBasicArgument(parenthesized.Expression, depth);

            case Sy.BinaryExpressionSyntax binary:
                if (binary.IsKind(VbKind.IntegerDivideExpression)
                    || binary.IsKind(VbKind.DivideExpression)
                    || binary.IsKind(VbKind.RightShiftExpression))
                    return RecursionPattern.Halving;

                if (binary.IsKind(VbKind.AddExpression) || binary.IsKind(VbKind.SubtractExpression))
                    return ResolvesToVisualBasicHalving(binary.Left, depth)
                           || ResolvesToVisualBasicHalving(binary.Right, depth)
                        ? RecursionPattern.Halving
                        : RecursionPattern.Subtractive;

                if (binary.IsKind(VbKind.MultiplyExpression) || binary.IsKind(VbKind.LeftShiftExpression))
                    return RecursionPattern.Subtractive;

                return RecursionPattern.SameArgument;

            case Sy.IdentifierNameSyntax identifier:
            {
                var initializer = VisualBasicLocalInitializer(identifier);
                return initializer is not null && depth < 2
                    ? ClassifyVisualBasicArgument(initializer, depth + 1)
                    : RecursionPattern.SameArgument;
            }

            default:
                return RecursionPattern.SameArgument;
        }
    }

    private bool ResolvesToVisualBasicHalving(Sy.ExpressionSyntax expression, int depth) => expression switch
    {
        Sy.ParenthesizedExpressionSyntax parenthesized => ResolvesToVisualBasicHalving(parenthesized.Expression, depth),
        Sy.BinaryExpressionSyntax binary when binary.IsKind(VbKind.IntegerDivideExpression)
            || binary.IsKind(VbKind.DivideExpression)
            || binary.IsKind(VbKind.RightShiftExpression) => true,
        Sy.IdentifierNameSyntax identifier => VisualBasicLocalInitializer(identifier) is { } initializer
            && depth < 2
            && ResolvesToVisualBasicHalving(initializer, depth + 1),
        _ => false,
    };

    private Sy.ExpressionSyntax? VisualBasicLocalInitializer(Sy.IdentifierNameSyntax identifier)
    {
        if (_model.GetSymbolInfo(identifier).Symbol is not ILocalSymbol local) return null;

        foreach (var reference in local.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is Sy.VariableDeclaratorSyntax declarator)
                return declarator.Initializer?.Value;
        }

        return null;
    }
}
