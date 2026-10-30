using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class FixComplexityTool
{
    [McpServerTool(Name = "fix_complexity"), Description(
        "Make the codebase simpler in ONE call: rank every method by cyclomatic and cognitive complexity, then " +
        "actually lower the worst ones (extract a block into a private method, name a tangled boolean, flatten a " +
        "tail if into a guard clause), re-measuring after each step and rolling back anything that would not " +
        "compile. Point it at the whole solution (default), one project, or one file. Returns the before/after " +
        "scores per method, the steps applied, the moves refused and why, and the ones only a human can do — so " +
        "this replaces calling complexity_report and reduce_complexity method by method. Runs a project compile " +
        "per method it touches. dryRun defaults to true and returns the diffs; pass dryRun=false to write.")]
    public static Task<object> FixComplexity(
        IFixComplexityService svc,
        [Description("Only this project (default: the whole solution).")] string? projectName = null,
        [Description("Only this file (default: the whole solution).")] string? filePath = null,
        [Description("How many of the worst methods to lower in this run (default: 5).")] int maxMethods = 5,
        [Description("Stop each method when cyclomatic AND cognitive are at or below this (default: 10).")] int target = 10,
        [Description("Maximum refactorings applied per method (default: 6).")] int maxStepsPerMethod = 6,
        [Description("true (default) previews only, with per-method diffs; pass false to write the files.")] bool dryRun = true)
        => svc.FixComplexity(projectName, filePath, maxMethods, target, maxStepsPerMethod, dryRun);
}
