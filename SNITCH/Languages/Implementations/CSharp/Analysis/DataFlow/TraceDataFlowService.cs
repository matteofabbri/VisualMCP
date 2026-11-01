using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis.DataFlow;

/// <summary>
/// Answers "does request data reach something dangerous?" over the loaded solution: the flows of
/// <see cref="TaintAnalyzer"/> with the paths made relative to the solution root.
/// </summary>
public interface ITraceDataFlowService
{
    Task<object> Trace(
        string? projectName, string? cwe, int depth, int maxResults, CancellationToken cancellationToken = default);
}

public sealed class TraceDataFlowService : ITraceDataFlowService
{
    private readonly IRoslynWorkspace _workspace;

    public TraceDataFlowService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> Trace(
        string? projectName, string? cwe, int depth, int maxResults, CancellationToken cancellationToken = default)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new
            {
                error = "No C# solution could be auto-located from the working directory. Call load_solution first.",
            };

        var scan = await TaintAnalyzer.AnalyzeAsync(
            solution, projectName, depth, Math.Clamp(maxResults, 1, 5_000), cancellationToken);

        var root = Path.GetDirectoryName(_workspace.LoadedSolutionPath ?? string.Empty) ?? string.Empty;

        var flows = scan.Flows
            .Select(flow => new
            {
                source = new
                {
                    kind = flow.Source.Kind,
                    expression = flow.Source.Expression,
                    file = Relative(root, flow.Source.File),
                    line = flow.Source.Line,
                },
                sink = new
                {
                    kind = flow.Sink.Kind,
                    cwe = flow.Sink.Cwe,
                    severity = flow.Sink.Severity,
                    operation = flow.Sink.Expression,
                    file = Relative(root, flow.Sink.File),
                    line = flow.Sink.Line,
                },
                hops = flow.Hops,
                steps = flow.Steps.Select(step => new
                {
                    file = Relative(root, step.File),
                    line = step.Line,
                    what = step.What,
                }).ToList(),
            })
            .Where(flow => cwe is null ||
                           string.Equals(flow.sink.cwe, NormalizeCwe(cwe), StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new
        {
            tool = "trace_data_flow",
            projectFilter = projectName ?? "all",
            depth,
            methodsAnalyzed = scan.MethodsAnalyzed,
            sourceParameters = scan.SourceParameters,
            interproceduralCalls = scan.InterproceduralCalls,
            flowCount = flows.Count,
            byWeakness = flows.GroupBy(flow => flow.sink.cwe)
                .OrderByDescending(group => group.Count())
                .ToDictionary(group => group.Key, group => group.Count()),
            byKind = flows.GroupBy(flow => flow.sink.kind)
                .OrderByDescending(group => group.Count())
                .ToDictionary(group => group.Key, group => group.Count()),
            bySeverity = flows.GroupBy(flow => flow.sink.severity)
                .OrderByDescending(group => group.Count())
                .ToDictionary(group => group.Key, group => group.Count()),
            flows,
            note = "A static taint walk over locals, parameters and call arguments, up to `depth` call levels, " +
                   "plus a field written in one method and read in another, on any object. Sources and sinks are " +
                   "the tables in TaintCatalog: request query/form/header/body/route data, Console and environment " +
                   "input, and model-bound action parameters; sinks are SQL, process, file, redirect, HTML and " +
                   "HTTP-client calls. A field is keyed by its declaration, not by the instance it hangs off, so " +
                   "writing one instance's field taints every instance's; a property, a database and a queue are " +
                   "not followed. A sanitizer list (parsed numbers, GUIDs, Path.GetFileName, encoded strings) is " +
                   "what marks a value safe. A finding is a candidate to check, not a proof.",
        };
    }

    private static string NormalizeCwe(string cwe)
    {
        var trimmed = cwe.Trim();
        return trimmed.StartsWith("CWE-", StringComparison.OrdinalIgnoreCase) ? trimmed.ToUpperInvariant() : $"CWE-{trimmed}";
    }

    private static string Relative(string root, string file)
    {
        if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(root)) return file;
        try { return Path.GetRelativePath(root, file).Replace('\\', '/'); }
        catch (ArgumentException) { return file; }
    }
}
