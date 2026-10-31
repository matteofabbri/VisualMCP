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
        "subset, or profile picks a named step list: the built-in full / reformat / style / usings / regions / " +
        "reorder / safe, plus the team's own profiles in .visualMCP/cleanup-profiles.json (saveProfileAs writes " +
        "one there). Each step reports its own changes; the call stops at the first failure instead of leaving " +
        "a half-cleaned file. Dry run by default.")]
    public static Task<object> CodeCleanup(
        ICodeCleanupService svc,
        [Description("document | project | solution (default)")] string scope = "solution",
        [Description("For scope=document: the file to clean")] string? filePath = null,
        [Description("For scope=project: the project to clean")] string? projectName = null,
        [Description("Comma-separated subset of: style, usings, regions, reorder, format. Default: all, in that order.")] string? steps = null,
        [Description("A named profile instead of steps: full, reformat, style, usings, regions, reorder, safe, or one from .visualMCP/cleanup-profiles.json. An unknown name returns the catalogue.")] string? profile = null,
        [Description("Save the effective step list as a custom profile under this name (written to .visualMCP/cleanup-profiles.json unless dryRun).")] string? saveProfileAs = null,
        [Description("Preview only (default true). Pass false to write the files.")] bool dryRun = true)
        => svc.Cleanup(scope, filePath, projectName, steps, dryRun, profile, saveProfileAs);
}
