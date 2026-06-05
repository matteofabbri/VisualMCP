using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class AnalyzeDependenciesTool
{
    [McpServerTool, Description(
        "Call this tool to build the project-level dependency graph for the loaded solution, " +
        "detect circular project references, and identify root projects (nothing depends on them). " +
        "Do NOT trace project dependencies yourself by reading .csproj files — this tool uses " +
        "Roslyn's project reference model which is authoritative. " +
        "For namespace-level cycles within a project, use analyze_circular_namespace_dependencies instead. " +
        "Requires load_solution first.")]
    public static async Task<object> AnalyzeDependencies()
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var idToName = solution.Projects.ToDictionary(p => p.Id, p => p.Name);

        // Build adjacency list
        var edges = solution.Projects
            .SelectMany(p => p.ProjectReferences.Select(r => (From: p.Name, To: idToName.GetValueOrDefault(r.ProjectId, r.ProjectId.ToString()))))
            .ToList();

        var projectNodes = solution.Projects.Select(p => new
        {
            Name         = p.Name,
            DependsOn    = p.ProjectReferences.Select(r => idToName.GetValueOrDefault(r.ProjectId, r.ProjectId.ToString())).ToList(),
            DependedOnBy = edges.Where(e => e.To == p.Name).Select(e => e.From).ToList(),
        }).ToList();

        // Detect cycles via DFS
        var cycles = FindCycles(solution.Projects.Select(p => p.Name).ToList(), edges);

        // Find projects that are referenced but whose types are not actually used
        // (heuristic: no ProjectReference in Roslyn model means no type cross-usage)
        // — Roslyn doesn't expose unused-ref detection at the project level directly,
        // so we flag projects with zero in-edges (potential leaf orphans) separately.
        var referencedProjects = new HashSet<string>(edges.Select(e => e.To));
        var rootProjects = solution.Projects
            .Where(p => !referencedProjects.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();

        return new
        {
            projectCount   = solution.ProjectIds.Count,
            edgeCount      = edges.Count,
            rootProjects,   // projects nothing depends on
            cycles          = cycles.Count == 0 ? null : cycles,
            hasCycles       = cycles.Count > 0,
            projects        = projectNodes,
        };
    }

    private static List<List<string>> FindCycles(List<string> nodes, List<(string From, string To)> edges)
    {
        var adj = nodes.ToDictionary(n => n, _ => new List<string>());
        foreach (var (from, to) in edges)
            if (adj.ContainsKey(from) && adj.ContainsKey(to))
                adj[from].Add(to);

        var visited  = new HashSet<string>();
        var inStack  = new HashSet<string>();
        var cycles   = new List<List<string>>();
        var stack    = new List<string>();

        void Dfs(string node)
        {
            visited.Add(node);
            inStack.Add(node);
            stack.Add(node);

            foreach (var neighbour in adj[node])
            {
                if (!visited.Contains(neighbour))
                    Dfs(neighbour);
                else if (inStack.Contains(neighbour))
                {
                    // Extract cycle
                    var idx = stack.IndexOf(neighbour);
                    cycles.Add(stack[idx..].Concat([neighbour]).ToList());
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
