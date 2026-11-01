using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

public interface IAnalyzeCircularNamespaceDependenciesService
{
    Task<object> AnalyzeCircularNamespaceDependencies(string? namespaceFilter);
}

public sealed class AnalyzeCircularNamespaceDependenciesService : IAnalyzeCircularNamespaceDependenciesService
{
    private readonly IRoslynWorkspace _workspace;
    public AnalyzeCircularNamespaceDependenciesService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> AnalyzeCircularNamespaceDependencies(
        string? namespaceFilter = null)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        // Build directed graph: ns -> set of namespaces it depends on
        var graph = new Dictionary<string, HashSet<string>>();

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var model = await document.GetSemanticModelAsync();
                var root  = await document.GetSyntaxRootAsync();
                if (model is null || root is null) continue;

                foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var sym = model.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
                    if (sym is null) continue;

                    var sourceNs = sym.ContainingNamespace?.ToDisplayString() ?? "<global>";
                    if (namespaceFilter is not null && !sourceNs.Contains(namespaceFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!graph.ContainsKey(sourceNs)) graph[sourceNs] = [];

                    // Collect all type references used within this type
                    foreach (var node in typeDecl.DescendantNodes())
                    {
                        var typeInfo = model.GetTypeInfo(node);
                        var refSym   = (typeInfo.Type ?? typeInfo.ConvertedType) as INamedTypeSymbol;
                        if (refSym is null) continue;

                        var targetNs = refSym.ContainingNamespace?.ToDisplayString() ?? "<global>";
                        if (targetNs == sourceNs || targetNs == "<global>") continue;
                        if (namespaceFilter is not null && !targetNs.Contains(namespaceFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        graph[sourceNs].Add(targetNs);
                        if (!graph.ContainsKey(targetNs)) graph[targetNs] = [];
                    }
                }
            }
        }

        var cycles = FindAllCycles(graph);

        var edges = graph
            .SelectMany(kv => kv.Value.Select(t => new { From = kv.Key, To = t }))
            .ToList();

        return new
        {
            namespaceFilter  = namespaceFilter ?? "all",
            namespaceCount   = graph.Count,
            edgeCount        = edges.Count,
            hasCycles        = cycles.Count > 0,
            cycleCount       = cycles.Count,
            cycles           = cycles.Count == 0 ? null : cycles,
            graph            = graph.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(x => x).ToList()),
        };
    }

    private static List<List<string>> FindAllCycles(Dictionary<string, HashSet<string>> graph)
    {
        var nodes   = graph.Keys.ToList();
        var visited = new HashSet<string>();
        var inStack = new HashSet<string>();
        var stack   = new List<string>();
        var cycles  = new List<List<string>>();

        void Dfs(string node)
        {
            visited.Add(node);
            inStack.Add(node);
            stack.Add(node);

            foreach (var neighbour in graph.GetValueOrDefault(node) ?? [])
            {
                if (!graph.ContainsKey(neighbour)) continue;

                if (!visited.Contains(neighbour))
                    Dfs(neighbour);
                else if (inStack.Contains(neighbour))
                {
                    var idx   = stack.IndexOf(neighbour);
                    var cycle = stack[idx..].Concat([neighbour]).ToList();
                    // Avoid duplicate cycles (same set, different start)
                    if (!cycles.Any(c => c.Count == cycle.Count && new HashSet<string>(c).SetEquals(cycle)))
                        cycles.Add(cycle);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            inStack.Remove(node);
        }

        foreach (var node in nodes)
            if (!visited.Contains(node))
                Dfs(node);

        return cycles;
    }
}
