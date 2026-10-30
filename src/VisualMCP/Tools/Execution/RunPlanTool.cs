using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Execution;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class RunPlanTool
{
    [McpServerTool(Name = "run_plan"), Description(
        "Run a whole workflow inside the server, with no model in the loop: a dependency graph of steps " +
        "executed natively, concurrently where the graph allows it, in ONE round trip instead of one call " +
        "per step. A step is either { kind: 'tool', tool: '<any tool name>', arguments: { ... } }, which " +
        "invokes that VisualMCP tool in-process with the same argument names its schema uses, or " +
        "{ kind: 'command', command, shell } for a shell command. Per step you may also set id, dependsOn " +
        "(ids), paths (the files/dirs it writes: two steps whose paths overlap never run at the same time, " +
        "which is what makes parallel edits safe), forEach + as (fan one step out over a list, with '${item}' " +
        "substituted), timeoutSeconds and continueOnError. " +
        "The plan is validated before anything runs: a duplicate id, an unknown tool, an unknown dependency " +
        "or a cycle rejects the whole plan and nothing executes. A failed step skips the steps that depend " +
        "on it; onFailure='stop' (default) also stops starting unrelated ones, 'continue' runs them anyway. " +
        "The report aggregates every step's status, timing, exit code, error and output (per-step budget: " +
        "outputChars, default 4000). " +
        "Use this INSTEAD of issuing the same tool call N times by hand, or handing a mechanical fan-out to " +
        "subagents — one simplify_code step per document, one inspect_code step per project, then a build " +
        "that depends on all of them, all in one call. Do NOT use it for steps that need judgement between " +
        "them: that is still a job for the model.")]
    public static Task<object> RunPlan(
        IPlanExecutionService plans,
        [Description("The plan: { name, steps: [...], maxConcurrency, onFailure, outputChars }.")] PlanSpec plan,
        CancellationToken cancellationToken)
        => plans.RunAsync(plan, cancellationToken);
}
