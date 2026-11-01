using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Analysis.DataFlow;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class TraceDataFlowTool
{
    [McpServerTool(Name = "trace_data_flow"), Description(
        "The taint half of security analysis: does request data reach a dangerous operation? Sources are " +
        "request query/form/header/cookie/body/route data, Console.ReadLine, environment variables and the " +
        "model-bound parameters of controller actions; sinks are SQL execution (EF `FromSqlRaw`/`ExecuteSqlRaw`, " +
        "Dapper, SqlCommand.CommandText), Process.Start and ProcessStartInfo, File/Directory/Path operations, " +
        "redirects, Html.Raw and Response.Write, and HttpClient/WebRequest URLs. Every flow carries its source, " +
        "the sink with its CWE, and the path of assignments and calls between them. `depth` follows taint into " +
        "the in-solution methods it is passed to, so a value that is validated in the action and used in a " +
        "repository is still one flow. Sanitizers (parsed numbers, GUIDs, Path.GetFileName, encoded strings) stop " +
        "a flow. A field is followed across methods and objects, keyed by its declaration rather than by the " +
        "instance, so writing one instance's field taints every instance's; a property, a database and a queue " +
        "are not followed, and the answer says so. Read-only.")]
    public static Task<object> TraceDataFlow(
        ITraceDataFlowService svc,
        [Description("Optional: only this project (exact name from the loaded solution).")] string? projectName = null,
        [Description("Optional: only this weakness, e.g. 'CWE-89' or '89'.")] string? cwe = null,
        [Description("How many call levels to follow a tainted argument into an in-solution method (default 2; 0 is one method at a time).")] int depth = 2,
        [Description("Maximum flows to return (default 200).")] int maxResults = 200)
        => svc.Trace(projectName, cwe, depth, maxResults);
}
