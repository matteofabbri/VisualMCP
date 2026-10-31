using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Profiling.Instrumentation;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileInstrumentationTool
{
    [McpServerTool(Name = "profile_instrumentation"), Description(
        "INSTRUMENTED profiling, the exact counterpart of sampling: rewrite a compiled assembly's IL so every method " +
        "reports enter and exit to a probe, then read the events back as EXACT call counts and inclusive/exclusive " +
        "times (no sampling, so a method that runs twice is reported with two calls). Two steps, one tool: " +
        "action=instrument rewrites the assembly (IL via Mono.Cecil, an injected EventSource probe, a .map.json with " +
        "the method names, the probe DLL copied beside the output) — pass include/exclude wildcards on the full method " +
        "name to bound it; then run the instrumented copy while collecting with profile_collect " +
        "profile=instrumentation; then action=analyze on the trace (mapPath defaults to the .map.json beside the " +
        "trace). Methods whose IL cannot be wrapped safely are skipped and named, never silently broken.")]
    public static async Task<object> Instrumentation(
        IAssemblyInstrumenter instrumenter,
        IInstrumentationAnalysisService analysis,
        [Description("'instrument' to rewrite an assembly, 'analyze' to read an instrumented trace. Omit to infer from the arguments (assemblyPath -> instrument, tracePath -> analyze).")] string? action = null,
        [Description("For action=instrument: path to the compiled .dll to rewrite.")] string? assemblyPath = null,
        [Description("For action=instrument: where to write the instrumented copy (a file or a folder). Default: beside the input as '<name>.instrumented.dll'.")] string? outputPath = null,
        [Description("For action=instrument: comma-separated wildcard patterns on the full method name to include, e.g. 'MyApp.Services.*,*OrderService*'. Omit for every method.")] string? include = null,
        [Description("For action=instrument: comma-separated wildcard patterns to exclude (applied before include).")] string? exclude = null,
        [Description("For action=analyze: path to the .nettrace collected with profile_collect profile=instrumentation.")] string? tracePath = null,
        [Description("For action=analyze: path to the .map.json the instrumenter wrote. Omitted: the only *.map.json beside the trace is used.")] string? mapPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var mode = string.IsNullOrWhiteSpace(action)
                ? (!string.IsNullOrWhiteSpace(assemblyPath) ? "instrument" :
                   !string.IsNullOrWhiteSpace(tracePath) ? "analyze" : null)
                : action.Trim().ToLowerInvariant();

            return mode switch
            {
                "instrument" or "instrumented" => await Task.Run(
                    () => instrumenter.Instrument(
                        assemblyPath ?? throw new ArgumentException("action=instrument needs assemblyPath."),
                        outputPath,
                        Split(include),
                        Split(exclude)),
                    cancellationToken),

                "analyze" or "analysis" or "read" => await Task.Run(
                    () => analysis.Analyze(
                        tracePath ?? throw new ArgumentException("action=analyze needs tracePath."),
                        mapPath),
                    cancellationToken),

                _ => ProfilingToolHelpers.Error(new ArgumentException(
                    "profile_instrumentation needs action=instrument (with assemblyPath) or action=analyze (with tracePath).")),
            };
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }

    private static IReadOnlyList<string>? Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
