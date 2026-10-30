using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.Razor;

namespace VisualMCP.Tools.Web;

[McpServerToolType]
public static class RazorPagesTool
{
    [McpServerTool, Description(
        "Inventory the Razor files of the solution (or of one project, folder or file): .cshtml Razor Pages, " +
        "MVC views, layouts, partials and Blazor .razor components. For each page it reports the kind, the @page " +
        "route, @model type, layout, sections, @inject services, @functions handlers, whether a code-behind exists, " +
        "and markup counts (elements, forms, POST forms, inline scripts, Html.Raw sinks). " +
        "Use this INSTEAD OF listing .cshtml files by hand when you need to know which pages exist and how they are wired. " +
        "Read-only. For defects call analyze_razor_pages; for security call find_razor_vulnerabilities.")]
    public static Task<object> RazorPages(
        IRazorPagesService svc,
        [Description("Optional: only this project (exact name from the loaded solution).")] string? projectName = null,
        [Description("Optional: a folder or a single .cshtml/.razor file (absolute, or relative to the solution).")] string? path = null,
        [Description("Maximum pages to return (default 200, max 5000).")] int maxPages = 200)
        => svc.ListAsync(projectName, path, maxPages);
}
