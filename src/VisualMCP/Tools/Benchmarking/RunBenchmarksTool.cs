using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Benchmarking;

namespace VisualMCP.Tools.Benchmarking;

[McpServerToolType]
public static class RunBenchmarksTool
{
    [McpServerTool(Name = "run_benchmarks"), Description(
        "Run BenchmarkDotNet benchmarks: builds the benchmark project in Release and runs it (dotnet run -c " +
        "Release -- --filter ...), returning the summary table. Auto-selects the project that references " +
        "BenchmarkDotNet, or use projectName. Benchmarks are slow, so this has a long timeout; narrow with the " +
        "filter (e.g. '*MyBench*') or pass '--job short' via extraArgs for a quick run. The solution auto-loads " +
        "on first use.")]
    public static Task<object> RunBenchmarks(
        [Description("Optional: the benchmark project name. If omitted, the single project referencing BenchmarkDotNet is used.")] string? projectName = null,
        [Description("BenchmarkDotNet filter glob (default: '*' = all). E.g. '*Serialize*'.")] string filter = "*",
        [Description("Optional: extra args passed to the benchmark runner, e.g. '--job short' for a fast run.")] string? extraArgs = null,
        [Description("Skip building before running (pass --no-build). Default: false.")] bool noBuild = false,
        [Description("Timeout in seconds before the run is stopped (default: 600, max: 1800).")] int timeoutSeconds = 600)
        => RunBenchmarksImpl.RunAsync(projectName, filter, extraArgs, noBuild, timeoutSeconds);
}
