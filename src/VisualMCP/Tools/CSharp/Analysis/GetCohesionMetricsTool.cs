using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class GetCohesionMetricsTool
{
    [McpServerTool(Name = "get_cohesion_metrics"), Description(
        "LCOM4: how many independent field clusters a class really has. Members that share a field are " +
        "one component; a class with more than one is two classes wearing one name. Use it with " +
        "get_complexity_hotspots: complexity says a method is hard, cohesion says the type is split " +
        "wrong. Stateless helper members are counted separately, not as extra components.")]
    public static Task<object> GetCohesionMetrics(
        IGetCohesionMetricsService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Only report types with at least this many components (default 2)")] int minComponents = 2,
        [Description("How many types to return, worst first (default 20)")] int top = 20)
        => svc.Measure(projectName, minComponents, top);
}
