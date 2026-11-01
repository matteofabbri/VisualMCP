using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.VB.ContextActions;

public sealed partial class VbContextActionService
{
    /// <summary>
    /// "Convert LINQ query syntax to a method chain": <c>From x In xs Where … Select …</c> becomes
    /// <c>xs.Where(Function(x) …).Select(Function(x) …)</c>. The clause set VB allows is wider than this
    /// action's shape (joins, Group By, Aggregate), so anything else is refused with that sentence.
    /// </summary>
    private Outcome LinqQueryToMethod(Target target)
    {
        var query = target.Root.DescendantNodes().OfType<QueryExpressionSyntax>()
            .Where(candidate => LineOf(candidate) <= target.Line && EndLine(candidate) >= target.Line)
            .OrderByDescending(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (query is null) return Outcome.Fail($"No LINQ query expression on line {target.Line}.");

        string? range = null;
        string? source = null;
        var calls = new List<string>();

        foreach (var clause in query.Clauses)
        {
            switch (clause)
            {
                case FromClauseSyntax from:
                    if (source is not null)
                        return Outcome.Fail("A second From clause (a join) has no single method-chain form.");
                    if (from.Variables.Count != 1)
                        return Outcome.Fail("A From clause that declares several range variables has no single method-chain form.");

                    var variable = from.Variables[0];
                    if (variable.AsClause is not null)
                        return Outcome.Fail("A typed range variable has no method-chain form: the lambda parameter carries no type.");
                    range = variable.Identifier.Identifier.ValueText;
                    source = variable.Expression.ToString();
                    break;

                case WhereClauseSyntax where:
                    if (range is null) return Outcome.Fail("A Where clause precedes the From clause.");
                    calls.Add($"Where(Function({range}) {where.Condition})");
                    break;

                case OrderByClauseSyntax orderBy:
                    if (range is null) return Outcome.Fail("An Order By clause precedes the From clause.");
                    foreach (var ordering in orderBy.Orderings)
                    {
                        var method = ordering.AscendingOrDescendingKeyword.IsKind(SyntaxKind.DescendingKeyword)
                            ? "OrderByDescending"
                            : "OrderBy";
                        calls.Add($"{method}(Function({range}) {ordering.Expression})");
                    }
                    break;

                case SelectClauseSyntax select:
                    if (range is null) return Outcome.Fail("A Select clause precedes the From clause.");
                    if (select.Variables.Count != 1 || select.Variables[0].NameEquals is not null)
                        return Outcome.Fail("A Select clause that projects several or named values has no single method-chain form.");
                    calls.Add($"Select(Function({range}) {select.Variables[0].Expression})");
                    break;

                default:
                    return Outcome.Fail($"The '{clause.Kind()}' clause has no method-chain form.");
            }
        }

        if (source is null || calls.Count == 0)
            return Outcome.Fail("The query has no From clause followed by a Where, Order By or Select.");

        return new Outcome(ReplaceNode(target, query, source + "." + string.Join(".", calls)),
            [$"query over '{source}' → {calls.Count} method call(s)"]);
    }

    /// <summary>
    /// "Convert a LINQ method chain to query syntax": the reverse of the action above, for a chain of
    /// Where/Select/OrderBy/OrderByDescending calls with a single-parameter single-line Function lambda.
    /// </summary>
    private Outcome LinqMethodToQuery(Target target)
    {
        var chain = target.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(candidate => LineOf(candidate) <= target.Line && EndLine(candidate) >= target.Line)
            .OrderByDescending(candidate => candidate.Span.Length)
            .FirstOrDefault(candidate => TryUnchain(candidate, out _, out _));

        if (chain is null)
            return Outcome.Fail("No Where/Select/OrderBy method chain with a single-parameter Function lambda was found on that line.");

        TryUnchain(chain, out var steps, out var source);

        var parameter = ParameterOf(steps[0].Lambda);
        if (parameter is null)
            return Outcome.Fail("The lambda does not declare exactly one parameter, so it has no range variable.");

        for (var index = 1; index < steps.Count; index++)
        {
            if (!string.Equals(ParameterOf(steps[index].Lambda), parameter, StringComparison.Ordinal))
                return Outcome.Fail("The lambdas do not all use the same parameter name, so one range variable cannot serve them.");
        }

        var builder = new System.Text.StringBuilder();
        builder.Append("From ").Append(parameter).Append(" In ").Append(source);

        foreach (var (method, lambda) in steps)
        {
            var body = BodyOf(lambda);
            if (body is null)
                return Outcome.Fail("A lambda has a statement body: a query clause needs an expression.");

            builder.Append(method switch
            {
                "Where" => " Where ",
                "OrderBy" => " Order By ",
                "OrderByDescending" => " Order By ",
                _ => " Select ",
            });
            builder.Append(body);
            if (method == "OrderByDescending") builder.Append(" Descending");
        }

        if (steps[^1].Method != "Select")
            builder.Append(" Select ").Append(parameter);

        return new Outcome(ReplaceNode(target, chain, builder.ToString()),
            [$"method chain of {steps.Count} call(s) → query expression"]);
    }

    /// <summary>
    /// Peels the <c>Where</c>/<c>Select</c>/<c>OrderBy</c>/<c>OrderByDescending</c> calls off the chain,
    /// outermost first, and leaves the receiver as the query's source.
    /// </summary>
    private static bool TryUnchain(
        ExpressionSyntax expression,
        out List<(string Method, SingleLineLambdaExpressionSyntax Lambda)> steps,
        out string source)
    {
        steps = [];
        while (expression is InvocationExpressionSyntax invocation &&
               invocation.Expression is MemberAccessExpressionSyntax member &&
               invocation.ArgumentList is { Arguments.Count: 1 } &&
               invocation.ArgumentList.Arguments[0].GetExpression() is SingleLineLambdaExpressionSyntax lambda &&
               member.Name.ToString() is "Where" or "Select" or "OrderBy" or "OrderByDescending")
        {
            steps.Insert(0, (member.Name.ToString(), lambda));
            expression = member.Expression;
        }

        source = expression.ToString().Trim();
        return steps.Count > 0;
    }

    /// <summary>The single parameter name of a <c>Function(x) …</c> lambda, or null for any other shape.</summary>
    private static string? ParameterOf(SingleLineLambdaExpressionSyntax lambda)
    {
        if (!lambda.SubOrFunctionHeader.SubOrFunctionKeyword.IsKind(SyntaxKind.FunctionKeyword))
            return null;
        if (lambda.SubOrFunctionHeader.ParameterList is not { Parameters.Count: 1 })
            return null;

        return lambda.SubOrFunctionHeader.ParameterList.Parameters[0].Identifier.Identifier.ValueText;
    }

    /// <summary>The expression a single-line Function lambda returns.</summary>
    private static string? BodyOf(SingleLineLambdaExpressionSyntax lambda) =>
        lambda.Body is ExpressionSyntax expression ? expression.ToString().Trim() : null;
}
