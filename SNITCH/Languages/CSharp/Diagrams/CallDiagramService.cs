using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Diagrams;

/// <summary>
/// The call graph as data a diagram tool renders: a Mermaid flowchart or Graphviz DOT of the methods that
/// call, or are called by, one entry point. <c>call_hierarchy</c> answers the tree in JSON; this emits the
/// artifact — and the same walk feeds the sequence diagram, a flat static call order.
/// </summary>
public interface ICallDiagramService
{
    Task<object> CallGraphAsync(string symbolName, string? containingType, string direction, int depth, string format, int maxNodes);

    Task<object> SequenceAsync(string symbolName, string? containingType, int depth, int maxMessages);
}

public sealed class CallDiagramService : ICallDiagramService
{
    private const int DefaultNodes = 120;

    private readonly IRoslynWorkspace _workspace;
    public CallDiagramService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> CallGraphAsync(
        string symbolName, string? containingType, string direction = "outgoing", int depth = 2,
        string format = "mermaid", int maxNodes = DefaultNodes)
    {
        var (solution, entry, error) = await ResolveEntryAsync(symbolName, containingType);
        if (error is not null) return new { error };
        if (solution is null || entry is null) return new { error = $"No method named '{symbolName}' was found in the solution." };

        var kind = (format ?? "mermaid").Trim().ToLowerInvariant();
        if (kind is not ("mermaid" or "dot")) return new { error = "format must be mermaid or dot." };

        var guard = Math.Clamp(depth < 1 ? 2 : depth, 1, 5);
        var limit = Math.Clamp(maxNodes < 1 ? DefaultNodes : maxNodes, 1, 1000);
        var edges = new List<(string From, string To)>();
        var seenEdges = new HashSet<string>(StringComparer.Ordinal);

        var edgeDirection = (direction ?? "outgoing").Trim().ToLowerInvariant();
        if (edgeDirection is not ("outgoing" or "incoming" or "both"))
            return new { error = "direction must be outgoing, incoming or both." };

        var level = new List<IMethodSymbol> { entry };
        var visited = new HashSet<string>(StringComparer.Ordinal) { entry.ToDisplayString() };

        for (var step = 0; step < guard && level.Count > 0 && edges.Count < limit * 4; step++)
        {
            var next = new List<IMethodSymbol>();

            foreach (var method in level)
            {
                if (edgeDirection is "outgoing" or "both")
                    foreach (var callee in await CalleesAsync(method, solution))
                    {
                        AddEdge(edges, seenEdges, method.ToDisplayString(), callee.ToDisplayString());
                        if (visited.Add(callee.ToDisplayString())) next.Add(callee);
                    }

                if (edgeDirection is "incoming" or "both")
                    foreach (var caller in await CallersAsync(method, solution))
                    {
                        AddEdge(edges, seenEdges, caller.ToDisplayString(), method.ToDisplayString());
                        if (visited.Add(caller.ToDisplayString())) next.Add(caller);
                    }
            }

            level = next;
            if (edges.Count >= limit * 4) break;
        }

        var nodes = edges.SelectMany(edge => new[] { edge.From, edge.To }).Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal).ToList();
        var truncated = nodes.Count > limit;
        if (truncated)
        {
            var kept = nodes.Take(limit).ToHashSet(StringComparer.Ordinal);
            edges = edges.Where(edge => kept.Contains(edge.From) && kept.Contains(edge.To)).ToList();
            nodes = kept.OrderBy(name => name, StringComparer.Ordinal).ToList();
        }

        var graph = kind == "dot" ? Dot(nodes, edges) : Mermaid(nodes, edges);

        return new
        {
            tool = "call_graph",
            entry = entry.ToDisplayString(),
            direction = edgeDirection,
            depth = guard,
            format = kind,
            nodeCount = nodes.Count,
            edgeCount = edges.Count,
            truncated,
            graph,
        };
    }

    public async Task<object> SequenceAsync(
        string symbolName, string? containingType, int depth = 2, int maxMessages = 60)
    {
        var (solution, entry, error) = await ResolveEntryAsync(symbolName, containingType);
        if (error is not null) return new { error };
        if (solution is null || entry is null) return new { error = $"No method named '{symbolName}' was found in the solution." };

        var guard = Math.Clamp(depth < 1 ? 2 : depth, 1, 4);
        var limit = Math.Clamp(maxMessages < 1 ? 60 : maxMessages, 1, 300);
        var messages = new List<(string From, string To, string Call, int Depth)>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { entry.ToDisplayString() };

        async Task WalkAsync(IMethodSymbol method, int remaining)
        {
            if (remaining <= 0 || messages.Count >= limit) return;

            foreach (var callee in await CalleesAsync(method, solution))
            {
                messages.Add((TypeOf(method), TypeOf(callee), callee.Name + "()", guard - remaining + 1));
                if (!visited.Add(callee.ToDisplayString())) continue;
                await WalkAsync(callee, remaining - 1);
                if (messages.Count >= limit) return;
            }
        }

        await WalkAsync(entry, guard);

        var participants = messages.Select(m => m.From).Concat(messages.Select(m => m.To))
            .Distinct(StringComparer.Ordinal).ToList();

        var builder = new StringBuilder();
        builder.AppendLine("sequenceDiagram");
        foreach (var participant in participants)
            builder.Append("  participant ").Append(Id(participant)).Append(" as ").AppendLine(Escape(participant));

        foreach (var (from, to, call, callDepth) in messages)
            builder.Append("  ").Append(Id(from)).Append("->>").Append(Id(to)).Append(": ")
                .Append(Escape(call)).Append(" (depth ").Append(callDepth).AppendLine(")");

        return new
        {
            tool = "sequence_diagram",
            entry = entry.ToDisplayString(),
            depth = guard,
            messageCount = messages.Count,
            truncated = messages.Count > limit,
            participants,
            graph = builder.ToString().TrimEnd(),
            note = "A static call order: the messages are the calls the source makes, in the order the walk finds " +
                   "them, not the order a run takes. Conditional and repeated calls appear once per site.",
        };
    }

    // ── call edges ──────────────────────────────────────────────────────────
    private static async Task<List<IMethodSymbol>> CalleesAsync(IMethodSymbol method, Solution solution)
    {
        var callees = new List<IMethodSymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            var node = await reference.GetSyntaxAsync();
            var document = solution.GetDocument(node.SyntaxTree);
            if (document is null) continue;
            var model = await document.GetSemanticModelAsync();
            if (model is null) continue;

            foreach (var invocation in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol target) continue;
                if (target.MethodKind is MethodKind.DelegateInvoke) continue;
                if (target.IsImplicitlyDeclared || target.Name is "Equals" or "GetHashCode" or "ToString") continue;
                if (target.ContainingType?.SpecialType == SpecialType.System_Object) continue;
                if (!seen.Add(target.ToDisplayString())) continue;
                callees.Add(target);
            }
        }

        return callees;
    }

    private static async Task<List<IMethodSymbol>> CallersAsync(IMethodSymbol method, Solution solution)
    {
        var callers = new List<IMethodSymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var info in await SymbolFinder.FindCallersAsync(method, solution))
        {
            if (info.CallingSymbol is not IMethodSymbol caller) continue;
            if (caller.IsImplicitlyDeclared) continue;
            if (!seen.Add(caller.ToDisplayString())) continue;
            callers.Add(caller);
        }

        return callers;
    }

    private static void AddEdge(List<(string From, string To)> edges, HashSet<string> seen, string from, string to)
    {
        if (from == to) return;
        if (!seen.Add(from + "\u0000" + to)) return;
        edges.Add((from, to));
    }

    private static string TypeOf(IMethodSymbol method) =>
        method.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? method.Name;

    private static string Dot(IReadOnlyList<string> nodes, IReadOnlyList<(string From, string To)> edges)
    {
        var builder = new StringBuilder();
        builder.AppendLine("digraph calls {");
        builder.AppendLine("  rankdir=LR;");
        builder.AppendLine("  node [shape=box, fontname=\"Segoe UI\", fontsize=10];");
        foreach (var node in nodes)
            builder.Append("  \"").Append(Escape(node)).AppendLine("\";");
        foreach (var (from, to) in edges)
            builder.Append("  \"").Append(Escape(from)).Append("\" -> \"").Append(Escape(to)).AppendLine("\";");
        builder.Append('}');
        return builder.ToString();
    }

    private static string Mermaid(IReadOnlyList<string> nodes, IReadOnlyList<(string From, string To)> edges)
    {
        var builder = new StringBuilder();
        builder.AppendLine("flowchart LR");
        foreach (var node in nodes)
            builder.Append("  ").Append(Id(node)).Append("[\"").Append(Escape(node)).AppendLine("\"]");
        foreach (var (from, to) in edges)
            builder.Append("  ").Append(Id(from)).Append(" --> ").AppendLine(Id(to));
        return builder.ToString().TrimEnd();
    }

    private static string Id(string name) =>
        new(name.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private async Task<(Solution? Solution, IMethodSymbol? Entry, string? Error)> ResolveEntryAsync(
        string symbolName, string? containingType)
    {
        if (string.IsNullOrWhiteSpace(symbolName)) return (null, null, "A symbol name is required.");

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null) return (null, null, "No C# solution could be auto-loaded from the working directory.");

        var name = symbolName.Trim();
        var candidates = new List<IMethodSymbol>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null) continue;

            foreach (var symbol in compilation.GetSymbolsWithName(name, SymbolFilter.Member))
                if (symbol is IMethodSymbol method) candidates.Add(method);
        }

        var entry = candidates.FirstOrDefault(method =>
                        containingType is null ||
                        (method.ContainingType?.Name.Equals(containingType, StringComparison.OrdinalIgnoreCase) ?? false))
                    ?? candidates.FirstOrDefault();

        return (solution, entry, null);
    }
}
