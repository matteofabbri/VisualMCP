using Snitch.Workspace;

namespace Snitch.Languages.Razor;

/// <summary>
/// Inventory of the Razor files in the solution: what each one is, which route/model/layout it
/// declares, and how much markup and C# it carries.
/// </summary>
public interface IRazorPagesService
{
    Task<object> ListAsync(string? projectName, string? path, int maxPages);
}

public sealed class RazorPagesService : IRazorPagesService
{
    private readonly IRoslynWorkspace _workspace;
    public RazorPagesService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ListAsync(string? projectName = null, string? path = null, int maxPages = 200)
    {
        maxPages = RazorAnalysis.Clamp(maxPages, 200, 5000);

        var collection = await RazorAnalysis.CollectAsync(_workspace, projectName, path, maxPages);
        if (collection.Error is not null)
            return new { error = collection.Error };

        var pages = new List<object>();
        var kinds = new List<string>();
        var projects = new List<string>();

        foreach (var file in collection.Files)
        {
            var document = RazorAnalysis.TryRead(file, collection);
            if (document is null) continue;

            pages.Add(Describe(file, document));
            kinds.Add(RazorAnalysis.KindName(document.Kind));
            projects.Add(file.Project ?? "(unassigned)");
        }

        return new
        {
            root = collection.Root,
            projectFilter = projectName ?? "all",
            pathFilter = path,
            pageCount = pages.Count,
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

    private static object Describe(RazorFileEntry file, RazorDocument document)
    {
        var elements = document.AllElements().ToList();
        var forms = elements.Where(element => Is(element, "form")).ToList();
        var postForms = forms.Count(form =>
            string.Equals(form.Literal("method")?.Trim(), "post", StringComparison.OrdinalIgnoreCase));
        var inlineScripts = elements.Count(element =>
            Is(element, "script") &&
            !element.HasAttribute("src") &&
            !string.IsNullOrWhiteSpace(element.RawText));
        var rawSinks = document.Expressions.Count(expression =>
                             expression.Text.Contains("Html.Raw", StringComparison.Ordinal)) +
                         elements.SelectMany(element => element.Attributes).Count(attribute =>
                             attribute.Value?.Contains("Html.Raw", StringComparison.Ordinal) == true);

        return new
        {
            file = file.FilePath,
            relativePath = file.RelativePath,
            project = file.Project,
            kind = RazorAnalysis.KindName(document.Kind),
            route = document.Route,
            model = document.ModelType,
            inherits = document.Inherits,
            layout = document.LayoutDirective,
            sections = document.Sections,
            injections = document.Injections.Select(injection => new
            {
                type = injection.ServiceType,
                name = injection.PropertyName,
            }),
            usings = document.Usings,
            handlers = document.Handlers,
            tagHelpers = document.TagHelpers,
            hasCodeBehind = File.Exists(file.FilePath + ".cs"),
            lineCount = document.LineCount,
            counts = new
            {
                elements = elements.Count,
                forms = forms.Count,
                postForms,
                inlineScripts,
                rawHtmlSinks = rawSinks,
                expressions = document.Expressions.Count,
                codeBlocks = document.CodeBlocks.Count,
            },
            directives = document.Directives.Select(directive =>
                string.IsNullOrWhiteSpace(directive.Value)
                    ? "@" + directive.Name
                    : "@" + directive.Name + " " + directive.Value),
        };
    }

    private static bool Is(RazorElement element, string name) =>
        element.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
}
