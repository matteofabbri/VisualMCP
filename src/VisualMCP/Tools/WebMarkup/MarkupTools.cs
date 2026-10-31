using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.WebMarkup;

namespace VisualMCP.Tools.WebMarkup;

[McpServerToolType]
public static class MarkupTools
{
    [McpServerTool(Name = "markup_files"), Description(
        "What the solution's HTML and stylesheets are: every .html/.htm and .css/.scss file under the solution (or " +
        "under `path`), with its line, element and id counts (how many ids repeat) and the findings it carries. " +
        "`kind` picks html, css or both. Build output, node_modules, dist, coverage and vendored libraries are " +
        "skipped. Use it to find the page before reading it; analyze_markup lists the findings. Read-only.")]
    public static Task<object> MarkupFiles(
        IMarkupService svc,
        [Description("A file or a directory (default: the solution directory).")] string? path = null,
        [Description("html, css or both (default: both).")] string kind = "both",
        [Description("Maximum files to read (default 500).")] int maxFiles = 500)
        => svc.InventoryAsync(path, kind, maxFiles);

    [McpServerTool(Name = "analyze_markup"), Description(
        "Pattern-based HTML and CSS diagnostics — the static-site half of analyze_razor_pages. HTML rules: " +
        "HTML0001 <img> without alt, HTML0002 <html> without lang, HTML0003 no viewport meta, HTML0004 duplicate " +
        "id, HTML0005 <label> attached to nothing, HTML0006 nested <form>, HTML0007 deprecated element, HTML0008 " +
        "inline event handler, HTML0009 target=_blank without rel, HTML0010 empty href, HTML0011 <button> without " +
        "type, HTML0012 form control without name, HTML0013 subresource over http://, HTML0014 unclosed or " +
        "mismatched tag. CSS rules: CSS0001 !important, CSS0002 ID selector, CSS0003 empty rule, CSS0004 @import, " +
        "CSS0005 a selector declared twice, CSS0006 unbalanced braces, CSS0007 javascript: URL, CSS0008 " +
        "expression(). It reads tags with a quote-aware scanner and both languages with comments masked. It does " +
        "not render the page or resolve selectors against a DOM. Read-only.")]
    public static Task<object> AnalyzeMarkup(
        IMarkupService svc,
        [Description("A file or a directory (default: the solution directory).")] string? path = null,
        [Description("html, css or both (default: both).")] string kind = "both",
        [Description("Minimum severity: info (default), warning or error.")] string minSeverity = "info",
        [Description("Only this rule id, e.g. HTML0001.")] string? rule = null,
        [Description("Maximum files to read (default 500).")] int maxFiles = 500,
        [Description("Maximum findings to return (default 200; the total is always reported).")] int maxFindings = 200)
        => svc.AnalyzeAsync(path, kind, minSeverity, rule, maxFiles, maxFindings);
}
