using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ReduceComplexityTool
{
    [McpServerTool(Name = "reduce_complexity"), Description(
        "LOWER a method's cyclomatic and cognitive complexity the way professional tooling does it: it plans the " +
        "refactorings that remove decisions from the method, applies them one at a time, re-measures after each, " +
        "and stops at the target. The moves are extract a nested block into its own private method, name a tangled " +
        "boolean in a private bool method, and flatten a tail 'if' into an early-return guard clause. Every step is " +
        "verified against the compiler and the whole run rolls back to the last state that compiled, so a move the " +
        "syntax allowed but the semantics rejected never reaches the file. It reports before/after metrics, the " +
        "steps it took, the moves it rejected and why, and the ones left for a human (like turning an if/else-if " +
        "ladder into a switch). Identify the method by filePath plus line or methodName. dryRun defaults to true and " +
        "returns a line diff; pass dryRun=false to write.")]
    public static Task<object> ReduceComplexity(
        IReduceComplexityService svc,
        [Description("File that declares the method (absolute or relative).")] string filePath,
        [Description("Any line inside the method (1-based). Use this or methodName.")] int? line = null,
        [Description("Method name, when you don't have a line.")] string? methodName = null,
        [Description("Stop when cyclomatic AND cognitive are at or below this (default: 10).")] int target = 10,
        [Description("Maximum refactorings to apply in one run (default: 6).")] int maxSteps = 6,
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
        => svc.ReduceComplexity(filePath, line, methodName, target, maxSteps, dryRun);
}
