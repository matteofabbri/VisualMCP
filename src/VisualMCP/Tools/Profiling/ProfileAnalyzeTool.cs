using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Workspace;
using Snitch.Profiling;
using Snitch.Profiling.Analysis;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileAnalyzeTool
{
    [McpServerTool(Name = "profile_analyze"), Description(
        "Analyse a .nettrace collected by profile_collect (or by dotnet-trace) and turn it into an answer: " +
        "top CPU methods with sample counts, percentage and CPU ms (the sample stacks are extracted through a " +
        "temporary ETLX conversion, because EventPipe attaches stacks to the event record), the hottest source LINES " +
        "with their self CPU (from the sampled instruction pointer, when the PDB and the IL→native map are present), " +
        "top call stacks, " +
        "allocation sites (method + type + file:line, when the trace has allocation stacks), allocation totals by type, " +
        "a GC summary (gen0/1/2, pause time and share, LOH, promoted bytes, longest pauses), exceptions by type with " +
        "their throw site, lock contention, thread-pool backlog, JIT time and a per-second timeline. " +
        "Method names are mapped to file:line when a portable PDB is found next to " +
        "the module (or in the symbol cache — see profile_symbols). The result includes rule-based findings with " +
        "evidence and concrete next steps; writeReport=true (default) also writes a Markdown report under " +
        "<solution>/.visualMCP/profiling.")]
    public static async Task<object> Analyze(
        ITraceAnalysisService analyzer,
        ProfilingOptions options,
        IRoslynWorkspace workspace,
        [Description("Path to the .nettrace file to analyse.")] string tracePath,
        [Description("How many entries to return per section (1-200).")] int top = 25,
        [Description("Map method names to file:line using portable PDBs (default true).")] bool resolveSource = true,
        [Description("Extract CPU samples, allocation sites and exception throw sites (requires the ETLX conversion). Set false for a fast GC/exception-only summary.")] bool deep = true,
        [Description("Write a Markdown report under <solution>/.visualMCP/profiling.")] bool writeReport = true)
    {
        try
        {
            if (!File.Exists(tracePath))
                return new { error = $"Trace not found: {tracePath}" };

            var analysis = await analyzer.AnalyzeAsync(tracePath, top, resolveSource, deep);

            string? report = null;
            if (writeReport)
            {
                var directory = options.ResolveOutputDirectory(ProfilingToolHelpers.SolutionDirectory(workspace));
                var stem = Path.GetFileNameWithoutExtension(tracePath);
                var path = Path.Combine(directory, $"{stem}-analysis.md");
                report = await ProfilingReportWriter.WriteTraceReportAsync(analysis, path);
            }

            return new { analysis, report };
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
