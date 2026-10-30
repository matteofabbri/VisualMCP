using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class FormatCodeTool
{
    [McpServerTool(Name = "format_code"), Description(
        "ReSharper's 'Reformat Code': runs the Roslyn formatter over a document, a project or the whole " +
        "solution using the project's own options (including .editorconfig) and keeps each file's " +
        "line-ending convention. It only changes whitespace and layout — it never rewrites code; use " +
        "simplify_code for that — and it is the last step of code_cleanup. dryRun reports the files " +
        "without writing.")]
    public static Task<object> FormatCode(
        IFormatCodeService svc,
        [Description("document | project | solution (default)")] string scope = "solution",
        [Description("For scope=document: the file to format")] string? filePath = null,
        [Description("For scope=project: the project to format")] string? projectName = null,
        [Description("Preview only (default true). Pass false to write the files.")] bool dryRun = true)
        => svc.Format(scope, filePath, projectName, dryRun);
}
