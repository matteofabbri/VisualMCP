using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.CSharp.Benchmarking;

namespace VisualMCP.Tools.CSharp.Benchmarking;

[McpServerToolType]
public static class QuickBenchmarkTool
{
    [McpServerTool(Name = "quick_benchmark"), Description(
        "Benchmark an arbitrary code snippet WITHOUT writing a [Benchmark] class: the tool generates a " +
        "temporary BenchmarkDotNet project (Release, MemoryDiagnoser), optionally referencing a solution " +
        "project so the snippet can use its types, runs it, and returns the summary (time + allocations). " +
        "Use this to check a change didn't regress performance. Quick mode (ShortRunJob) by default for speed.")]
    public static Task<object> QuickBenchmark(
        [Description("The C# statements to benchmark (the body of the measured method).")] string code,
        [Description("Optional: setup code run once before measuring (the [GlobalSetup] body), e.g. allocate inputs.")] string? setupCode = null,
        [Description("Optional: extra 'using' namespaces to import (e.g. ['System.Linq','MyApp.Core']).")] string[]? usings = null,
        [Description("Optional: a solution project to reference so the snippet can use its types (by name).")] string? referenceProject = null,
        [Description("Optional: target framework (e.g. 'net10.0'). Inferred from the referenced project, else net10.0.")] string? targetFramework = null,
        [Description("Quick mode: ShortRunJob for a fast (less precise) result (default: true). Set false for a full run.")] bool quick = true,
        [Description("Timeout in seconds (default: 600, max: 1800).")] int timeoutSeconds = 600)
        => QuickBenchmarkImpl.RunAsync(code, setupCode, usings, referenceProject, targetFramework, quick, timeoutSeconds);
}
