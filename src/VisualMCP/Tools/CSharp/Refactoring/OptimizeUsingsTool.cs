using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class OptimizeUsingsTool
{
    [McpServerTool, Description(
        "When you want to clean up 'using' directives, use this INSTEAD OF hand-pruning: Roslyn knows which usings are actually unused (text inspection cannot), " +
        "removes them, optionally sorts the rest, and writes to disk (ReSharper 'Optimize Usings'). " +
        "The working-directory solution auto-loads on first use.")]
    public static async Task<object> OptimizeUsings(
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Optional: restrict to a single file by absolute path")] string? filePath = null,
        [Description("Sort remaining using directives alphabetically (default: true)")] bool sort = true,
        [Description("Dry run â€” report changes without writing to disk (default: false)")] bool dryRun = false)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var results = new List<object>();
        int totalRemoved = 0;
        int totalFiles = 0;

        foreach (var project in projects)
        {
            var docs = project.Documents
                .Where(d => d.SourceCodeKind == SourceCodeKind.Regular &&
                            (filePath is null || string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var document in docs)
            {
                var root = await document.GetSyntaxRootAsync() as CompilationUnitSyntax;
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                var usings = root.Usings;
                if (usings.Count == 0) continue;

                var usedNamespaces = CollectUsedNamespaces(root, model);

                var toRemove = new HashSet<string>();
                foreach (var u in usings)
                {
                    // Keep aliases (using X = Y) and using static â€” only remove plain namespace usings
                    if (u.Alias is not null || u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
                        continue;

                    var ns = u.Name?.ToString();
                    if (ns is not null && !usedNamespaces.Contains(ns))
                        toRemove.Add(ns);
                }

                if (toRemove.Count == 0 && !sort) continue;

                var keptUsings = usings.Where(u =>
                {
                    if (u.Alias is not null || u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)) return true;
                    var ns = u.Name?.ToString();
                    return ns is null || !toRemove.Contains(ns);
                }).ToList();

                if (sort)
                    keptUsings = SortUsings(keptUsings);

                var newRoot = root.WithUsings(SyntaxFactory.List(keptUsings));

                if (toRemove.Count == 0 && newRoot.IsEquivalentTo(root)) continue;

                totalFiles++;
                totalRemoved += toRemove.Count;

                var entry = new
                {
                    FilePath = document.FilePath,
                    RemovedUsings = toRemove.OrderBy(x => x).ToList(),
                    Sorted = sort,
                };
                results.Add(entry);

                if (!dryRun && document.FilePath is not null)
                {
                    var newText = newRoot.ToFullString();
                    await File.WriteAllTextAsync(document.FilePath, newText, System.Text.Encoding.UTF8);
                }
            }
        }

        return new
        {
            dryRun,
            projectFilter = projectName ?? "all",
            fileFilter = filePath ?? "all",
            filesModified = totalFiles,
            totalUsingsRemoved = totalRemoved,
            files = results,
        };
    }

    private static HashSet<string> CollectUsedNamespaces(CompilationUnitSyntax root, SemanticModel model)
    {
        var used = new HashSet<string>();

        foreach (var node in root.DescendantNodes())
        {
            // Skip the using directives themselves
            if (node.Parent is UsingDirectiveSyntax) continue;
            if (node is UsingDirectiveSyntax) continue;

            ISymbol? symbol = null;

            if (node is IdentifierNameSyntax or GenericNameSyntax)
            {
                var info = model.GetSymbolInfo(node);
                symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            }

            if (symbol is null) continue;

            // Walk up to the containing namespace
            var ns = GetNamespace(symbol);
            if (ns is not null)
                used.Add(ns);

            // Also record namespace of the symbol's containing type (for nested types)
            if (symbol.ContainingType is not null)
            {
                var parentNs = GetNamespace(symbol.ContainingType);
                if (parentNs is not null)
                    used.Add(parentNs);
            }
        }

        return used;
    }

    private static string? GetNamespace(ISymbol symbol)
    {
        var ns = symbol.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace) return null;
        return ns.ToDisplayString();
    }

    private static List<UsingDirectiveSyntax> SortUsings(List<UsingDirectiveSyntax> usings)
    {
        // Order: System.* first, then others, each group alphabetically
        return usings
            .OrderBy(u => u.Alias is not null ? 2 : u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) ? 1 : 0)
            .ThenBy(u =>
            {
                var name = u.Name?.ToString() ?? "";
                return name.StartsWith("System", StringComparison.Ordinal) ? 0 : 1;
            })
            .ThenBy(u => u.Name?.ToString() ?? "", StringComparer.Ordinal)
            .Select(u => u.WithLeadingTrivia(SyntaxFactory.ElasticMarker).WithTrailingTrivia(SyntaxFactory.ElasticMarker))
            .ToList();
    }
}
