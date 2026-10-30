using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Inspections;

namespace VisualMCP.Tools.CSharp.Inspections;

[McpServerToolType]
public static class ApplyCodeFixTool
{
    [McpServerTool(Name = "apply_code_fix"), Description(
        "Apply an inspection's CODE FIX in-process. Run inspect_code (or read the editor) first, then call this with the " +
        "reported id + file + line: the same analyzer packs the server already ships are re-run, and the fix comes from the " +
        "analyzer's own CodeFixProvider (Roslynator RCS, StyleCop SA, Meziantou MA). Rules that ship no fix (most compiler " +
        "and CA diagnostics) are refused with an explicit message instead of a guessed edit. DRY RUN BY DEFAULT: the " +
        "proposed change is returned as a line diff and nothing is written until dryRun=false. A fix that would touch more " +
        "than maxChangedFiles files is refused. Pass fixAllScope (document/project/solution) to repair EVERY occurrence of " +
        "the id with one call, when the provider supports FixAll. After writing, the in-memory solution is updated; " +
        "re-run inspect_code to see the remaining findings.")]
    public static Task<object> ApplyCodeFix(
        ICodeFixService fixes,
        [Description("Diagnostic id to fix, e.g. RCS1060 or SA1200.")] string diagnosticId,
        [Description("File containing the diagnostic (absolute or relative).")] string filePath,
        [Description("1-based line, as reported by inspect_code / get_diagnostics.")] int line,
        [Description("1-based column, when several diagnostics sit on the same line.")] int? column = null,
        [Description("Optional substring of the fix title, when the provider offers more than one fix.")] string? fixTitle = null,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true,
        [Description("Refuse to change more than this many files (default 5).")] int maxChangedFiles = 5,
        [Description("Optional FixAll scope: 'document', 'project' or 'solution'. Omit to fix only the occurrence at that line.")] string? fixAllScope = null)
        => fixes.ApplyAsync(new CodeFixRequest(diagnosticId, filePath, line, column, fixTitle, dryRun, maxChangedFiles, fixAllScope));
}
