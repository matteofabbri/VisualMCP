using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class LayoutCodeTool
{
    [McpServerTool(Name = "layout_code"), Description(
        "CodeMaid's Format Document rules that Roslyn's formatter does not implement, applied to one file " +
        "or a whole scope: rules=comments reflows blocks of full-line // comments to maxCommentWidth; " +
        "rules=blank_lines removes runs of blank lines, a blank line after an opening brace or before a " +
        "closing one, and the trailing blanks at end of file; rules=join joins fromLine..toLine into one " +
        "statement, accepted only when the token stream is unchanged (a comment or a multi-line string is " +
        "refused). Comments and blank lines apply to C# and Visual Basic; joining lines is C# only. " +
        "Compiler-gated, previewed unless dryRun=false, and recorded in the edit history.")]
    public static Task<object> LayoutCode(
        ILayoutCodeService svc,
        [Description("document | project | solution")] string scope = "document",
        [Description("For scope=document: the file to lay out")] string? filePath = null,
        [Description("For scope=project: the project to lay out")] string? projectName = null,
        [Description("Comma-separated subset of: comments, join, blank_lines. Default: comments,blank_lines.")] string? rules = null,
        [Description("Wrap comments at this column (default 120, clamped to 40..400).")] int maxCommentWidth = 120,
        [Description("For rules=join: first line of the range (1-based).")] int? fromLine = null,
        [Description("For rules=join: last line of the range (1-based).")] int? toLine = null,
        [Description("Preview only (default true). Pass false to write the files.")] bool dryRun = true)
        => svc.Apply(scope, filePath, projectName, rules, maxCommentWidth, fromLine, toLine, dryRun);
}
