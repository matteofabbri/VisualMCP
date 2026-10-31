using Microsoft.CodeAnalysis;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// Lists the members a framework calls by convention rather than by an in-solution reference: MVC
/// actions, Razor Pages handlers, SignalR hub methods, hosted services, Blazor lifecycle methods and
/// parameters, EF Core overrides and DbSets, middleware, test methods, [JSInvokable], [Inject]. It is
/// the classification that <c>find_unused_symbols</c> applies before calling a symbol unused.
/// </summary>
public interface IFrameworkEntryPointsService
{
    Task<object> ListAsync(string? projectName, int maxResults);
}

public sealed class FrameworkEntryPointsService : IFrameworkEntryPointsService
{
    private readonly IRoslynWorkspace _workspace;
    public FrameworkEntryPointsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ListAsync(string? projectName = null, int maxResults = 500)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var projects = solution.Projects
            .Where(project => projectName is null || project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var limit = Math.Clamp(maxResults < 1 ? 500 : maxResults, 1, 5000);
        var entries = new List<Entry>();

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(document => document.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                foreach (var node in root.DescendantNodes())
                {
                    if (model.GetDeclaredSymbol(node) is not { } symbol) continue;
                    if (symbol is not (INamedTypeSymbol or IMethodSymbol or IPropertySymbol)) continue;

                    var classification = FrameworkInvocation.Classify(symbol);
                    if (classification is null) continue;

                    var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                    entries.Add(new Entry(
                        project.Name,
                        classification.Framework,
                        classification.Reason,
                        symbol.ToDisplayString(),
                        symbol.Kind.ToString(),
                        symbol.DeclaredAccessibility.ToString(),
                        location?.SourceTree?.FilePath ?? document.FilePath,
                        (location?.GetLineSpan().StartLinePosition.Line + 1) ?? 0));
                }
            }
        }

        var byFramework = entries
            .GroupBy(entry => entry.Framework, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return new
        {
            projectFilter = projectName ?? "all",
            total = entries.Count,
            byFramework,
            entries = entries.Take(limit).Select(entry => new
            {
                Project = entry.Project,
                entry.Framework,
                entry.Reason,
                entry.Symbol,
                entry.Kind,
                entry.Accessibility,
                entry.File,
                entry.Line,
            }).ToList(),
            truncated = entries.Count > limit,
            note = "These symbols have no in-solution reference because the framework calls them: routing, dependency " +
                   "injection, the test runner, the component renderer or the EF model. find_unused_symbols excludes them " +
                   "from its unused list for the same reason.",
        };
    }

    private sealed record Entry(
        string Project,
        string Framework,
        string Reason,
        string Symbol,
        string Kind,
        string Accessibility,
        string? File,
        int Line);
}
