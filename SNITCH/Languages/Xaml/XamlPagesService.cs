using Snitch.Workspace;

namespace Snitch.Languages.Xaml;

/// <summary>
/// Inventory of the XAML files in the solution: what each one is (window, page, user control,
/// resource dictionary), which code-behind type it declares, and how much markup it carries.
/// </summary>
public interface IXamlPagesService
{
    Task<object> ListAsync(string? projectName, string? path, int maxFiles);
}

public sealed class XamlPagesService : IXamlPagesService
{
    private readonly IRoslynWorkspace _workspace;
    public XamlPagesService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ListAsync(string? projectName = null, string? path = null, int maxFiles = 200)
    {
        maxFiles = XamlAnalysis.Clamp(maxFiles, 200, 5000);

        var collection = await XamlAnalysis.CollectAsync(_workspace, projectName, path, maxFiles);
        if (collection.Error is not null) return new { error = collection.Error };

        var pages = new List<object>();
        var kinds = new List<string>();
        var projects = new List<string>();

        foreach (var file in collection.Files)
        {
            var document = XamlAnalysis.TryRead(file, collection, []);
            if (document is null) continue;

            var kind = Kind(document.RootName);
            kinds.Add(kind);
            projects.Add(file.Project ?? "(unassigned)");

            var codeBehind = document.ClassName is null
                ? null
                : await XamlAnalysis.ResolveTypeAsync(await _workspace.EnsureSolutionLoadedAsync(),
                    file.Project, document.ClassName);

            pages.Add(new
            {
                file = file.FilePath,
                relativePath = file.RelativePath,
                project = file.Project,
                kind,
                rootElement = document.RootName,
                className = document.ClassName,
                codeBehind = codeBehind is null
                    ? null
                    : new
                    {
                        fullName = codeBehind.FullName,
                        file = codeBehind.FilePath,
                        line = codeBehind.Line,
                        memberCount = codeBehind.MemberNames.Count,
                    },
                counts = new
                {
                    elements = document.ElementCount,
                    names = document.Names.Count,
                    keys = document.Keys.Count,
                    handlers = document.Handlers.Count,
                    resourceReferences = document.ResourceReferences.Count,
                    styles = document.Styles.Count,
                    templatesWithoutDataType = document.TemplatesWithoutDataType.Count,
                    nameReferences = document.NameReferences.Count,
                },
            });
        }

        return new
        {
            root = collection.Root,
            projectFilter = projectName ?? "all",
            pathFilter = path,
            fileCount = pages.Count,
            byKind = kinds
                .GroupBy(kind => kind, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            byProject = projects
                .GroupBy(project => project, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            truncated = collection.Truncated,
            skipped = collection.Skipped.Count > 0 ? collection.Skipped : null,
            unreadable = collection.Unreadable.Count > 0 ? collection.Unreadable : null,
            pages,
        };
    }

    private static string Kind(string rootName) => rootName switch
    {
        "Window" => "window",
        "Page" => "page",
        "UserControl" => "userControl",
        "ContentView" or "ContentPage" or "Shell" or "NavigationPage" or "TabbedPage" => "maui",
        "ResourceDictionary" => "resourceDictionary",
        "Application" => "application",
        _ => "other",
    };
}
