using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Analysis;

[McpServerToolType]
public static class FindUnusedSymbolsTool
{
    [McpServerTool, Description(
        "Call this tool to find public/internal types and members with zero references in the solution " +
        "(potential dead code candidates for removal). " +
        "Do NOT check for unused symbols yourself — this tool uses SymbolFinder to search all reference " +
        "locations across every project in the solution, which cannot be replicated by reading source. " +
        "Warning: can be slow on large solutions. " +
        "Requires load_solution first.")]
    public static async Task<object> FindUnusedSymbols(
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Symbol kinds to check — comma-separated: Type,Method,Property,Field,Event (default: Type,Method)")] string kinds = "Type,Method")
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        var kindSet = kinds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                          .Select(k => k.ToLowerInvariant())
                          .ToHashSet();

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var unused = new List<object>();

        foreach (var project in projects)
        {
            foreach (var document in project.Documents)
            {
                var model = await document.GetSemanticModelAsync();
                var root  = await document.GetSyntaxRootAsync();
                if (model is null || root is null) continue;

                foreach (var node in root.DescendantNodes())
                {
                    var sym = model.GetDeclaredSymbol(node);
                    if (sym is null) continue;
                    if (!IsCandidate(sym, kindSet)) continue;

                    // Skip compiler-generated, overrides, interface implementations, entry points
                    if (sym.IsImplicitlyDeclared) continue;
                    if (sym is IMethodSymbol m && (m.IsOverride || m.ExplicitInterfaceImplementations.Length > 0 || m.MethodKind == MethodKind.Constructor)) continue;
                    if (sym is IPropertySymbol prop && prop.IsOverride) continue;
                    if (sym.DeclaredAccessibility == Accessibility.Private) continue; // private unused is a different concern

                    var refs = await SymbolFinder.FindReferencesAsync(sym, solution);
                    var refCount = refs.Sum(r => r.Locations.Count());

                    if (refCount == 0)
                    {
                        var loc = sym.Locations.FirstOrDefault(l => l.IsInSource);
                        unused.Add(new
                        {
                            Symbol        = sym.ToDisplayString(),
                            Kind          = sym.Kind.ToString(),
                            Accessibility = sym.DeclaredAccessibility.ToString(),
                            Project       = project.Name,
                            FilePath      = loc?.SourceTree?.FilePath,
                            Line          = loc?.GetLineSpan().StartLinePosition.Line + 1,
                        });
                    }
                }
            }
        }

        return new
        {
            projectFilter = projectName ?? "all",
            kindsChecked  = kinds,
            unusedCount   = unused.Count,
            unused,
        };
    }

    private static bool IsCandidate(ISymbol sym, HashSet<string> kinds) => sym.Kind switch
    {
        SymbolKind.NamedType => kinds.Contains("type"),
        SymbolKind.Method    => kinds.Contains("method"),
        SymbolKind.Property  => kinds.Contains("property"),
        SymbolKind.Field     => kinds.Contains("field"),
        SymbolKind.Event     => kinds.Contains("event"),
        _                    => false,
    };
}
