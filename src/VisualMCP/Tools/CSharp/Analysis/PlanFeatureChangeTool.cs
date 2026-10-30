using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class PlanFeatureChangeTool
{
    [McpServerTool, Description(
        "Call this tool when you need to radically change or replace a feature. " +
        "Given a type or member name, it produces a complete, ordered change plan: " +
        "what to understand first, what will break, which files to touch, " +
        "which tests to run, and what to verify afterward. " +
        "Do NOT attempt to build this plan yourself by reading source files — " +
        "this tool uses Roslyn's full semantic model to resolve the entire impact graph " +
        "(callers, implementors, derived types, coverage, metrics, diagnostics) in one pass. " +
        "Requires load_solution first.")]
    public static Task<object> PlanFeatureChange(
        IPlanFeatureChangeService svc,
        [Description("Name of the type, interface, method, or property that is the core of the feature to change")] string symbolName,
        [Description("Optional: containing type name to disambiguate (e.g. if the method name is not unique)")] string? containingType = null,
        [Description("Include transitive callers (callers of callers) up to this depth — 0 means direct only (default: 1)")] int callerDepth = 1)
        => svc.PlanFeatureChange(symbolName, containingType, callerDepth);
}
