using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

public interface IFindUnusedSymbolsService
{
    Task<object> FindUnusedSymbols(string? projectName, string kinds);
}

public sealed class FindUnusedSymbolsService : IFindUnusedSymbolsService
{
    private readonly IRoslynWorkspace _workspace;
    public FindUnusedSymbolsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> FindUnusedSymbols(
        string? projectName = null,
        string kinds = "Type,Method")
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Provide an explicit path to the .sln/.slnx." };

        var kindSet = kinds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                          .Select(k => k.ToLowerInvariant())
                          .ToHashSet();

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var unused = new List<object>();
        var frameworkInvoked = new List<object>();

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

                    if (refCount > 0) continue;

                    var loc = sym.Locations.FirstOrDefault(l => l.IsInSource);

                    // No reference in source is not the same as no caller: routing, DI, the test runner
                    // and the component renderer call these by convention.
                    var entry = FrameworkInvocation.Classify(sym);
                    if (entry is not null)
                    {
                        frameworkInvoked.Add(new
                        {
                            Symbol        = sym.ToDisplayString(),
                            Kind          = sym.Kind.ToString(),
                            Framework     = entry.Framework,
                            Reason        = entry.Reason,
                            Project       = project.Name,
                            loc?.SourceTree?.FilePath,
                            Line          = loc?.GetLineSpan().StartLinePosition.Line + 1,
                        });
                        continue;
                    }

                    unused.Add(new
                    {
                        Symbol        = sym.ToDisplayString(),
                        Kind          = sym.Kind.ToString(),
                        Accessibility = sym.DeclaredAccessibility.ToString(),
                        Project       = project.Name,
                        loc?.SourceTree?.FilePath,
                        Line          = loc?.GetLineSpan().StartLinePosition.Line + 1,
                    });
                }
            }
        }

        return new
        {
            projectFilter = projectName ?? "all",
            kindsChecked  = kinds,
            unusedCount   = unused.Count,
            unused,
            frameworkInvokedCount = frameworkInvoked.Count,
            frameworkInvoked,
            note = frameworkInvoked.Count == 0
                ? null
                : "frameworkInvoked lists the symbols with no in-solution reference that a framework calls by convention " +
                  "(MVC actions, Razor Pages handlers, hub methods, hosted services, Blazor lifecycle and parameters, " +
                  "EF overrides and DbSets, middleware, test methods). Call framework_entry_points for the full classification.",
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
