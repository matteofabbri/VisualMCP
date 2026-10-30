using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.Razor;

namespace VisualMCP.Tools.Web;

[McpServerToolType]
public static class FindRazorVulnerabilitiesTool
{
    [McpServerTool, Description(
        "Security audit of Razor pages (.cshtml) and Blazor components (.razor): XSS through @Html.Raw / new HtmlString, " +
        "model data written inside a <script> block or an inline event handler, javascript:/data:text/html URIs, " +
        "POST forms without an anti-forgery token (CSRF), password fields submitted with GET, target=_blank without " +
        "noopener, hardcoded credentials in markup, http:// subresources, iframes with a data-driven src, and inline " +
        "script/style that defeats a Content-Security-Policy. Every finding carries a CWE id, a severity, file:line, " +
        "the source line and a remediation. Read-only. " +
        "Use this INSTEAD OF grepping for Html.Raw, type=\"password\" or target=\"_blank\" — the rules understand Razor " +
        "expressions, tag helpers and the anti-forgery token. Filter by minimum severity or by CWE (e.g. 79).")]
    public static Task<object> FindRazorVulnerabilities(
        IFindRazorVulnerabilitiesService svc,
        [Description("Optional: only this project (exact name from the loaded solution).")] string? projectName = null,
        [Description("Optional: a folder or a single .cshtml/.razor file (absolute, or relative to the solution).")] string? path = null,
        [Description("Minimum severity to report: Info, Low (default), Medium, High or Critical.")] string minSeverity = "Low",
        [Description("Optional: only this CWE, e.g. 'CWE-79' or '79'.")] string? cwe = null,
        [Description("Maximum findings to return (default 200, max 5000). The total is always reported.")] int maxFindings = 200)
        => svc.FindAsync(projectName, path, minSeverity, cwe, maxFindings);
}
