using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class FixDiagnosticsTool
{
    [McpServerTool(Name = "fix_diagnostics"), Description(
        "FIX the build, not just report it: apply the automatic rewrite for the mechanical compiler " +
        "diagnostics (CS####), which ship no CodeFixProvider and therefore no analyzer code fix. Covers eight ids: " +
        "CS0168 / CS0219 an unused local (removed, or kept as a discard assignment when its initializer can have a " +
        "side effect), CS0169 an unused private field, CS0067 an unused event, CS0105 a duplicated using, " +
        "CS0108 / CS0114 a member that hides an inherited one (marked 'new'), CS0109 a 'new' that hides nothing " +
        "(removed). Every fix is applied on the syntax tree and accepted only when the project compiles no worse; " +
        "the whole run is refused if the fixes together would add a compiler error. Nothing else is touched: " +
        "everything out of this set is returned as skipped with the reason. Compiled and verified in one call — " +
        "get_diagnostics lists, this fixes. dryRun defaults to true and returns the diffs; pass dryRun=false to " +
        "write (recorded in the edit history, revertible with revert_edit).")]
    public static Task<object> FixDiagnostics(
        IFixDiagnosticsService svc,
        [Description("Optional: only this project (default: the whole solution).")] string? projectName = null,
        [Description("Optional: only diagnostics whose source file is this one (absolute, or relative to the solution).")] string? filePath = null,
        [Description("Optional: comma-separated ids to fix (default: all supported). Anything else is refused with the reason.")] string? diagnosticIds = null,
        [Description("Maximum number of fixes in this run (default 200).")] int maxFixes = 200,
        [Description("true (default) previews only with the diffs; pass false to write the files.")] bool dryRun = true)
        => svc.FixAsync(projectName, filePath, diagnosticIds, maxFixes, dryRun);
}
