using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Inspections;

namespace VisualMCP.Tools.CSharp.Inspections;

[McpServerToolType]
public static class InspectionBaselineTool
{
    [McpServerTool(Name = "inspection_baseline"), Description(
        "A baseline for inspect_code, so a change is judged by the findings it introduced and not by the " +
        "thousands the codebase already has. action=create stores the current findings (fingerprint = " +
        "inspection id + file + message; the line is excluded so unrelated edits do not resurface them); " +
        "action=check runs the packs and returns only what is new and hasRegression, ready for a CI gate; " +
        "show/clear manage the stored baseline. Call create once, check after every change.")]
    public static Task<object> InspectionBaseline(
        IInspectionBaselineService svc,
        [Description("create | check | show | clear (default check)")] string action = "check",
        [Description("Optional: baseline a single project instead of the whole solution")] string? projectName = null,
        [Description("Inspection packs, as in inspect_code. Default: house,roslynator,netanalyzers,asyncfixer")] string packs = "",
        [Description("Minimum severity: error, warning, info or hidden. Default: info")] string minSeverity = "info",
        [Description("Optional: only these inspection ids; supports '*' prefixes")] string? ids = null,
        [Description("Optional: skip these inspection ids; supports '*' prefixes")] string? excludeIds = null,
        [Description("Maximum findings to report (default 200; the baseline itself is stored up to 5000)")] int maxResults = 200,
        [Description("Inspection timeout in seconds (default 120)")] int timeoutSeconds = 120)
        => svc.Run(action, projectName, packs, minSeverity, ids, excludeIds, maxResults, timeoutSeconds);
}
