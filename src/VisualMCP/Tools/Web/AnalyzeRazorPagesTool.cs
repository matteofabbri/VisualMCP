using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.Razor;

namespace VisualMCP.Tools.Web;

[McpServerToolType]
public static class AnalyzeRazorPagesTool
{
    [McpServerTool, Description(
        "Analyze Razor markup for problems that are not security vulnerabilities: unbalanced or unclosed Razor/HTML, " +
        "duplicate ids across the rendered composition (the page, the layout it declares and the partials either " +
        "renders) and duplicate attributes, nested forms, missing alt text, labels without a control, html without lang, " +
        "missing viewport meta, form controls without a name, buttons without a type, deprecated HTML, ViewBag/ViewData, " +
        "legacy Html.Partial/RenderPartial/Action helpers, oversized @functions/code blocks, and forms without an action. " +
        "Every finding carries ruleId (RZ2001…), category (Markup, Accessibility, Maintainability, Razor), severity, " +
        "file:line, the source line and a suggested fix. Read-only. " +
        "Use this INSTEAD OF reading .cshtml/.razor files by hand to review a view. " +
        "Security issues (XSS, CSRF, secrets, CSP) are reported by find_razor_vulnerabilities, not here.")]
    public static Task<object> AnalyzeRazorPages(
        IAnalyzeRazorPagesService svc,
        [Description("Optional: only this project (exact name from the loaded solution).")] string? projectName = null,
        [Description("Optional: a folder or a single .cshtml/.razor file (absolute, or relative to the solution).")] string? path = null,
        [Description("Optional: only this category — Markup, Accessibility, Maintainability or Razor.")] string? category = null,
        [Description("Optional: only this rule id, e.g. RZ2004 (missing alt) or RZ2010 (ViewBag).")] string? rule = null,
        [Description("Minimum severity to report: Info, Low (default), Medium, High or Critical.")] string minSeverity = "Low",
        [Description("Maximum findings to return (default 200, max 5000). The total is always reported.")] int maxFindings = 200)
        => svc.AnalyzeAsync(projectName, path, category, rule, minSeverity, maxFindings);
}
