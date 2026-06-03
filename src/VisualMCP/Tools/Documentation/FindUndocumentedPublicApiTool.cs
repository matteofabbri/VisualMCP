using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Documentation;

[McpServerToolType]
public static class FindUndocumentedPublicApiTool
{
    [McpServerTool, Description(
        "When you need to find public/protected types and members that lack XML doc comments (e.g. before publishing a library), use this INSTEAD OF scanning files manually. " +
        "Roslyn enumerates the real public surface across the whole solution, so nothing is missed and internal members are correctly excluded. " +
        "The working-directory solution auto-loads on first use.")]
    public static async Task<object> FindUndocumentedPublicApi(
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Include protected members in addition to public (default: true)")] bool includeProtected = true)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var undocumented = new List<object>();

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root  = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                foreach (var node in root.DescendantNodes())
                {
                    var sym = model.GetDeclaredSymbol(node);
                    if (sym is null) continue;
                    if (sym.IsImplicitlyDeclared) continue;

                    var access = sym.DeclaredAccessibility;
                    bool isPublic    = access == Accessibility.Public;
                    bool isProtected = access is Accessibility.Protected or Accessibility.ProtectedOrInternal;
                    if (!isPublic && !(includeProtected && isProtected)) continue;

                    // Skip compiler-generated operators, property accessors, etc.
                    if (sym is IMethodSymbol m && m.MethodKind is
                        MethodKind.PropertyGet or MethodKind.PropertySet or
                        MethodKind.EventAdd  or MethodKind.EventRemove or
                        MethodKind.Destructor) continue;

                    var doc = sym.GetDocumentationCommentXml()?.Trim();
                    if (string.IsNullOrEmpty(doc))
                    {
                        var loc = sym.Locations.FirstOrDefault(l => l.IsInSource);
                        undocumented.Add(new
                        {
                            Symbol        = sym.ToDisplayString(),
                            Kind          = sym.Kind.ToString(),
                            Accessibility = access.ToString(),
                            Project       = project.Name,
                            FilePath      = loc?.SourceTree?.FilePath,
                            Line          = loc?.GetLineSpan().StartLinePosition.Line + 1,
                        });
                    }
                }
            }
        }

        var byKind = undocumented
            .GroupBy(s => ((dynamic)s).Kind.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        return new
        {
            projectFilter    = projectName ?? "all",
            includeProtected,
            undocumentedCount = undocumented.Count,
            byKind,
            undocumented,
        };
    }
}
