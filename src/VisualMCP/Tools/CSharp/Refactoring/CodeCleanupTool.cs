using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class CodeCleanupTool
{
    [McpServerTool(Name = "code_cleanup"), Description(
        "ReSharper's Code Cleanup in one call: the steps already exist as separate tools, and this runs " +
        "them in the order they belong — simplify_code (style rewrites) → optimize_usings → remove_regions " +
        "→ reorder_members → format_code — over a document, a project or the whole solution. steps picks a " +
        "subset. Each step reports its own changes; the call stops at the first failure instead of leaving " +
        "a half-cleaned file. Dry run by default.")]
    public static Task<object> CodeCleanup(
        ICodeCleanupService svc,
        [Description("document | project | solution (default)")] string scope = "solution",
        [Description("For scope=document: the file to clean")] string? filePath = null,
        [Description("For scope=project: the project to clean")] string? projectName = null,
        [Description("Comma-separated subset of: style, usings, regions, reorder, format. Default: all, in that order.")] string? steps = null,
        [Description("Preview only (default true). Pass false to write the files.")] bool dryRun = true)
        => svc.Cleanup(scope, filePath, projectName, steps, dryRun);
}
