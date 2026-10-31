using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Languages.CSharp.Analysis.DataFlow;

/// <summary>
/// A taint walk over the solution: it seeds a method with the values a caller controls, follows the
/// assignments and calls that carry them, and reports the ones that arrive at a sink in
/// <see cref="TaintCatalog"/>. It is a static analysis with a documented reach — locals, parameters and
/// call arguments to a caller-set depth, plus a field of the same type (written in one method, read in
/// another) — not a proof: a field of another object, a database or a queue is not followed, and
/// <c>trace_data_flow</c> says so in every answer.
/// </summary>
public static class TaintAnalyzer
{
    public static async Task<TaintScan> AnalyzeAsync(
        Solution solution,
        string? projectName,
        int depth,
        int maxFlows,
        CancellationToken cancellationToken = default)
    {
        var context = new Context(Math.Clamp(maxFlows, 1, 5_000));

        foreach (var method in await MethodCollector.CollectAsync(solution, projectName, cancellationToken))
        {
            context.Models.Add(method);
            context.Index[method.Definition] = method;
        }

        // Pass one: the fields a type parks tainted data in. A field written by one method and read by
        // another is the one store an intra-method walk cannot see.
        foreach (var method in context.Models)
        {
            var state = ComputeTaint(method, seeds: null, fieldSeeds: null);
            foreach (var (symbol, origin) in state)
                if (symbol is IFieldSymbol field && !context.FieldOrigins.ContainsKey(field))
                    context.FieldOrigins[field] = origin;
        }

        // Pass two: the report, with those fields seeded in every method of their containing type.
        foreach (var method in context.Models)
        {
            var state = ComputeTaint(method, seeds: null, fieldSeeds: context.FieldOrigins);
            foreach (var hit in FindHits(method, state))
                context.Add(new TaintFlow(hit.Origin.Source, hit.Sink, hit.Origin.Steps, hit.Origin.Steps.Count));

            if (context.Flows.Count < context.MaxFlows)
                TraceCalls(method, state, context, Math.Max(0, depth));
        }

        context.SourceParameters = context.Models.Sum(model =>
            model.Symbol.Parameters.Count(parameter => IsSourceParameter(parameter, model.IsAction)));

        var flows = context.Flows
            .GroupBy(Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(flow => flow.Sink.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(flow => flow.Sink.Line)
            .ToList();

        return new TaintScan(flows, context.Models.Count, context.SourceParameters, context.InterproceduralCalls);
    }

    // ── the walk of one method ─────────────────────────────────────────────────

    private static Dictionary<ISymbol, Origin> ComputeTaint(
        MethodDeclarationModel method,
        IReadOnlyDictionary<IParameterSymbol, Origin>? seeds,
        IReadOnlyDictionary<IFieldSymbol, Origin>? fieldSeeds)
    {
        var state = new Dictionary<ISymbol, Origin>(SymbolEqualityComparer.Default);

        foreach (var parameter in method.Symbol.Parameters)
            if (IsSourceParameter(parameter, method.IsAction))
                state[parameter] = new Origin(SourceOf(parameter, method), []);

        if (seeds is not null)
            foreach (var (parameter, origin) in seeds)
                state[parameter] = origin;

        if (fieldSeeds is not null)
            foreach (var (field, origin) in fieldSeeds)
                if (SymbolEqualityComparer.Default.Equals(field.ContainingType, method.Symbol.ContainingType))
                    state[field] = origin;

        var model = method.Model;

        // A fixed point, because a loop or a later assignment can taint a value an earlier read saw
        // as clean. Eight passes is far more than a hand-written method needs; the cap keeps a
        // pathological body from costing a minute.
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var changed = false;

            foreach (var node in method.Scope.DescendantNodes(DoNotDescendIntoLocalFunctions))
            {
                switch (node)
                {
                    case VariableDeclaratorSyntax { Initializer.Value: { } value } declarator:
                        if (model.GetDeclaredSymbol(declarator) is not ILocalSymbol local || state.ContainsKey(local)) break;
                        if (!TryOrigin(value, model, state, out var declarationOrigin)) break;
                        state[local] = WithStep(declarationOrigin, method, declarator, $"assigned to {local.Name}");
                        changed = true;
                        break;

                    case AssignmentExpressionSyntax assignment:
                        if (TryOwnField(assignment.Left, model) is { } field)
                        {
                            var fieldValue = assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                                ? assignment.Right
                                : assignment;

                            if (TryOrigin(fieldValue, model, state, out var fieldOrigin))
                            {
                                state[field] = WithStep(fieldOrigin, method, assignment, $"stored in field {field.Name}");
                                changed = true;
                            }
                            else if (state.Remove(field))
                            {
                                // Last write wins: a clean assignment clears what an earlier one stored.
                                changed = true;
                            }

                            break;
                        }

                        if (assignment.Left is MemberAccessExpressionSyntax or ElementAccessExpressionSyntax) break;
                        if (model.GetSymbolInfo(assignment.Left).Symbol is not { } assigned ||
                            assigned is not (ILocalSymbol or IParameterSymbol) || state.ContainsKey(assigned))
                            break;

                        var carried = assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ? assignment.Right : assignment;
                        if (!TryOrigin(carried, model, state, out var assignedOrigin)) break;
                        state[assigned] = WithStep(assignedOrigin, method, assignment, $"assigned to {assignment.Left}");
                        changed = true;
                        break;
                }
            }

            if (!changed) break;
        }

        return state;
    }

    private static List<Hit> FindHits(MethodDeclarationModel method, Dictionary<ISymbol, Origin> state)
    {
        var hits = new List<Hit>();
        var model = method.Model;

        foreach (var node in method.Scope.DescendantNodes(DoNotDescendIntoLocalFunctions))
        {
            switch (node)
            {
                case InvocationExpressionSyntax invocation
                    when ResolveCall(invocation, model) is { } callee && MatchSink(callee, out var sink, out var positions):
                {
                    foreach (var argument in Guarded(invocation, positions))
                    {
                        if (!TryOrigin(argument, model, state, out var origin)) continue;
                        hits.Add(HitOf(sink, argument, origin, method));
                        break;
                    }

                    break;
                }

                case ObjectCreationExpressionSyntax creation
                    when model.GetTypeInfo(creation).Type is { } created &&
                         MatchConstructor(created, out var sink, out var positions):
                {
                    if (creation.ArgumentList is not { } argumentList) break;

                    var candidates = positions is [-1]
                        ? argumentList.Arguments.Select(argument => argument.Expression)
                        : positions.Where(index => index >= 0 && index < argumentList.Arguments.Count)
                            .Select(index => argumentList.Arguments[index].Expression);

                    foreach (var argument in candidates)
                    {
                        if (!TryOrigin(argument, model, state, out var origin)) continue;
                        hits.Add(HitOf(sink, argument, origin, method));
                        break;
                    }

                    break;
                }

                case AssignmentExpressionSyntax assignment
                    when assignment.Left is MemberAccessExpressionSyntax access &&
                         model.GetSymbolInfo(access).Symbol is IPropertySymbol property &&
                         MatchSinkMember(property, out var sink):
                {
                    if (!TryOrigin(assignment.Right, model, state, out var origin)) break;
                    hits.Add(HitOf(sink, assignment.Right, origin, method));
                    break;
                }
            }
        }

        return hits;
    }

    private static Hit HitOf(SinkKind sink, ExpressionSyntax expression, Origin origin, MethodDeclarationModel method) =>
        new(new FlowSink(
                sink.Kind, sink.Cwe, sink.Severity, Shorten(expression.ToString()), method.File, Line(expression)),
            origin);

    // ── the call graph ─────────────────────────────────────────────────────────

    private static void TraceCalls(
        MethodDeclarationModel method, Dictionary<ISymbol, Origin> state, Context context, int remainingDepth)
    {
        if (remainingDepth <= 0) return;

        foreach (var invocation in method.Scope
                     .DescendantNodes(DoNotDescendIntoLocalFunctions).OfType<InvocationExpressionSyntax>())
        {
            if (ResolveCall(invocation, method.Model) is not { } callee) continue;
            if (!context.Index.TryGetValue(Normalize(callee), out var calleeModel)) continue;
            if (ReferenceEquals(calleeModel, method)) continue;

            var arguments = invocation.ArgumentList.Arguments;
            for (var index = 0; index < arguments.Count; index++)
            {
                if (!TryOrigin(arguments[index].Expression, method.Model, state, out var origin)) continue;

                var parameter = DeclaredParameter(callee, index);
                if (parameter is null) continue;

                var key = $"{calleeModel.Id}|{parameter.Ordinal}|{origin.Source.File}:{origin.Source.Line}|{origin.Source.Kind}";
                if (!context.Visited.Add(key)) continue;
                if (context.Flows.Count >= context.MaxFlows) return;

                context.InterproceduralCalls++;
                var steps = new List<FlowStep>(origin.Steps)
                {
                    new(method.File, Line(invocation), $"passed to {calleeModel.Symbol.Name}"),
                };

                var seeded = new Dictionary<IParameterSymbol, Origin>(SymbolEqualityComparer.Default)
                {
                    [parameter] = new Origin(origin.Source, steps),
                };

                var calleeState = ComputeTaint(calleeModel, seeded, context.FieldOrigins);
                foreach (var hit in FindHits(calleeModel, calleeState))
                    context.Add(new TaintFlow(hit.Origin.Source, hit.Sink, hit.Origin.Steps, hit.Origin.Steps.Count));

                TraceCalls(calleeModel, calleeState, context, remainingDepth - 1);
            }
        }
    }

    /// <summary>
    /// The declared parameter an argument of a (possibly reduced extension) call lands on. Named
    /// arguments are matched by position, which is what the call sites of generated code use.
    /// </summary>
    private static IParameterSymbol? DeclaredParameter(IMethodSymbol callee, int argumentIndex)
    {
        var declared = callee.ReducedFrom ?? callee;
        var offset = callee.ReducedFrom is null ? 0 : 1;
        var position = argumentIndex + offset;
        return position < declared.Parameters.Length ? declared.Parameters[position] : null;
    }

    // ── expression taint ───────────────────────────────────────────────────────

    private static bool TryOrigin(
        ExpressionSyntax expression, SemanticModel model, Dictionary<ISymbol, Origin> state, out Origin origin)
    {
        origin = default!;

        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return TryOrigin(parenthesized.Expression, model, state, out origin);

            case CastExpressionSyntax cast:
                return TryOrigin(cast.Expression, model, state, out origin);

            case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                return TryOrigin(postfix.Operand, model, state, out origin);

            case AwaitExpressionSyntax awaitExpression:
                return TryOrigin(awaitExpression.Expression, model, state, out origin);

            case ConditionalAccessExpressionSyntax conditional:
                return TryOrigin(conditional.Expression, model, state, out origin);

            case InvocationExpressionSyntax invocation:
                if (IsSanitizer(invocation, model)) return false;
                if (SourceOf(invocation, model) is { } invocationSource)
                {
                    origin = new Origin(invocationSource, []);
                    return true;
                }

                if (TryOrigin(invocation.Expression, model, state, out origin)) return true;
                foreach (var argument in invocation.ArgumentList.Arguments)
                    if (TryOrigin(argument.Expression, model, state, out origin))
                        return true;
                return false;

            case ObjectCreationExpressionSyntax creation:
                foreach (var argument in creation.ArgumentList?.Arguments ?? default)
                    if (TryOrigin(argument.Expression, model, state, out origin))
                        return true;
                return false;

            case InterpolatedStringExpressionSyntax interpolated:
                foreach (var content in interpolated.Contents.OfType<InterpolationSyntax>())
                    if (TryOrigin(content.Expression, model, state, out origin))
                        return true;
                return false;

            case BinaryExpressionSyntax binary:
                return TryOrigin(binary.Left, model, state, out origin) ||
                       TryOrigin(binary.Right, model, state, out origin);

            case ConditionalExpressionSyntax conditional:
                return TryOrigin(conditional.WhenTrue, model, state, out origin) ||
                       TryOrigin(conditional.WhenFalse, model, state, out origin);

            case AssignmentExpressionSyntax assignment:
                return TryOrigin(assignment.Right, model, state, out origin);

            case ElementAccessExpressionSyntax element:
                if (TryOrigin(element.Expression, model, state, out origin)) return true;
                foreach (var argument in element.ArgumentList.Arguments)
                    if (TryOrigin(argument.Expression, model, state, out origin))
                        return true;
                return false;

            case MemberAccessExpressionSyntax access:
                if (SourceOf(access, model) is { } memberSource)
                {
                    origin = new Origin(memberSource, []);
                    return true;
                }

                if (model.GetSymbolInfo(access).Symbol is IFieldSymbol memberField &&
                    state.TryGetValue(memberField, out var memberKnown))
                {
                    origin = memberKnown;
                    return true;
                }

                return TryOrigin(access.Expression, model, state, out origin);

            case IdentifierNameSyntax or GenericNameSyntax:
                if (model.GetSymbolInfo(expression).Symbol is not { } symbol) return false;
                if (state.TryGetValue(symbol, out var known))
                {
                    origin = known;
                    return true;
                }

                if (symbol is IParameterSymbol parameter && IsSourceParameter(parameter, isAction: false))
                {
                    origin = new Origin(
                        new FlowSource("parameter", parameter.Name, FileOf(expression, model), Line(expression)), []);
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// The field an assignment writes when this walk is allowed to follow it: a static field, or one
    /// reached through <c>this</c>/<c>base</c>. A field of another object would need alias tracking, so it
    /// is deliberately not followed.
    /// </summary>
    private static IFieldSymbol? TryOwnField(ExpressionSyntax left, SemanticModel model)
    {
        if (model.GetSymbolInfo(left).Symbol is not IFieldSymbol field) return null;

        return left switch
        {
            IdentifierNameSyntax => field,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or BaseExpressionSyntax } => field,
            MemberAccessExpressionSyntax when field.IsStatic => field,
            _ => null,
        };
    }

    private static bool IsSanitizer(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (ResolveCall(invocation, model) is not { } method) return false;
        var containing = method.ContainingType?.Name ?? string.Empty;
        return TaintCatalog.Sanitizers.Any(entry =>
            entry.Type == containing && entry.Method == method.Name);
    }

    private static FlowSource? SourceOf(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (ResolveCall(invocation, model) is not { } method) return null;
        var containing = method.ContainingType?.Name ?? string.Empty;

        var entry = TaintCatalog.SourceMethods
            .Where(candidate => candidate.Type == containing && candidate.Method == method.Name)
            .Select(candidate => candidate.Kind)
            .FirstOrDefault();

        return entry is null
            ? null
            : new FlowSource(entry, Shorten(invocation.ToString()), FileOf(invocation, model), Line(invocation));
    }

    private static FlowSource? SourceOf(MemberAccessExpressionSyntax access, SemanticModel model)
    {
        if (model.GetSymbolInfo(access).Symbol is not IPropertySymbol property) return null;
        var containing = property.ContainingType?.Name ?? string.Empty;

        var entry = TaintCatalog.SourceMembers
            .Where(candidate => candidate.Type == containing && candidate.Member == property.Name)
            .Select(candidate => candidate.Kind)
            .FirstOrDefault();

        return entry is null
            ? null
            : new FlowSource(entry, Shorten(access.ToString()), FileOf(access, model), Line(access));
    }

    // ── parameters ─────────────────────────────────────────────────────────────

    private static bool IsSourceParameter(IParameterSymbol parameter, bool isAction)
    {
        if (TaintCatalog.SourceParameterTypes.Contains(parameter.Type.Name)) return true;

        foreach (var attribute in parameter.GetAttributes())
        {
            var name = (attribute.AttributeClass?.Name ?? string.Empty).Replace("Attribute", string.Empty, StringComparison.Ordinal);
            if (TaintCatalog.SourceParameterAttributes.Contains(name)) return true;
        }

        if (!isAction) return false;

        // An action's remaining parameters are model-bound. A logger, a cancellation token or a
        // registered service is not user input, and treating it as one would drown the report.
        return parameter.Type.Name is not ("ILogger" or "CancellationToken" or "IServiceProvider");
    }

    private static FlowSource SourceOf(IParameterSymbol parameter, MethodDeclarationModel method) =>
        new(
            parameter.GetAttributes().Length > 0 ? "model-bound parameter" : "parameter",
            parameter.Name,
            method.File,
            Line(method.Scope));

    // ── sink tables ────────────────────────────────────────────────────────────

    private static bool MatchSink(IMethodSymbol method, out SinkKind sink, out int[] positions)
    {
        var containing = method.ContainingType?.Name ?? string.Empty;
        foreach (var entry in TaintCatalog.Sinks)
        {
            if (entry.Type != containing || entry.Method != method.Name) continue;
            sink = entry.Sink;
            positions = entry.Arguments;
            return true;
        }

        sink = default!;
        positions = [];
        return false;
    }

    private static bool MatchConstructor(ITypeSymbol type, out SinkKind sink, out int[] positions)
    {
        foreach (var entry in TaintCatalog.SinkConstructors)
        {
            if (entry.Type != type.Name) continue;
            sink = entry.Sink;
            positions = entry.Arguments;
            return true;
        }

        sink = default!;
        positions = [];
        return false;
    }

    private static bool MatchSinkMember(IPropertySymbol property, out SinkKind sink)
    {
        var containing = property.ContainingType?.Name ?? string.Empty;
        foreach (var entry in TaintCatalog.SinkMembers)
        {
            if (entry.Type != containing || entry.Member != property.Name) continue;
            sink = entry.Sink;
            return true;
        }

        sink = default!;
        return false;
    }

    private static IEnumerable<ExpressionSyntax> Guarded(InvocationExpressionSyntax invocation, int[] positions)
    {
        var arguments = invocation.ArgumentList.Arguments;

        if (positions.Length == 0)
        {
            yield return invocation;
            yield break;
        }

        if (positions.Length == 1 && positions[0] < 0)
        {
            foreach (var argument in arguments) yield return argument.Expression;
            yield break;
        }

        foreach (var position in positions)
        {
            if (position < 0 || position >= arguments.Count) continue;
            yield return arguments[position].Expression;
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static IMethodSymbol? ResolveCall(InvocationExpressionSyntax invocation, SemanticModel model) =>
        model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;

    private static IMethodSymbol Normalize(IMethodSymbol symbol) =>
        (symbol.ReducedFrom ?? symbol).OriginalDefinition;

    private static bool DoNotDescendIntoLocalFunctions(SyntaxNode node) =>
        node is not LocalFunctionStatementSyntax;

    private static Origin WithStep(Origin origin, MethodDeclarationModel method, SyntaxNode node, string what) =>
        new(origin.Source, [..origin.Steps, new FlowStep(method.File, Line(node), what)]);

    private static int Line(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string FileOf(SyntaxNode node, SemanticModel model) =>
        node.SyntaxTree.FilePath ?? model.Compilation.AssemblyName ?? "(text)";

    private static string Shorten(string text) =>
        text.Length <= 120 ? text : text[..120] + "…";

    private static string Key(TaintFlow flow) =>
        $"{flow.Sink.File}:{flow.Sink.Line}|{flow.Sink.Kind}|{flow.Source.File}:{flow.Source.Line}|{flow.Source.Kind}";

    private sealed record Origin(FlowSource Source, IReadOnlyList<FlowStep> Steps);

    private sealed record Hit(FlowSink Sink, Origin Origin);

    private sealed class Context(int maxFlows)
    {
        public int MaxFlows { get; } = maxFlows;

        public List<MethodDeclarationModel> Models { get; } = [];

        public Dictionary<IMethodSymbol, MethodDeclarationModel> Index { get; } = new(SymbolEqualityComparer.Default);

        /// <summary>The fields that hold tainted data, collected in pass one and seeded in pass two.</summary>
        public Dictionary<IFieldSymbol, Origin> FieldOrigins { get; } = new(SymbolEqualityComparer.Default);

        public HashSet<string> Visited { get; } = new(StringComparer.Ordinal);

        public List<TaintFlow> Flows { get; } = [];

        public int SourceParameters { get; set; }

        public int InterproceduralCalls { get; set; }

        public void Add(TaintFlow flow)
        {
            if (Flows.Count < MaxFlows) Flows.Add(flow);
        }
    }
}
