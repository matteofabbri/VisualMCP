using Snitch.Profiling.Analysis;
using Snitch.Profiling.Model;

namespace Snitch.Profiling.Snapshots;

public interface IRetentionService
{
    Task<RetentionReport> AnalyzeAsync(
        int? processId, string? dumpPath, string? focusType, int maxPaths, int maxNodes,
        bool includeReferrers, CancellationToken cancellationToken = default);
}

/// <summary>
/// "Why is this type still in memory?" — attaches to a live process (snapshot attach, so it is
/// paused only while the memory is copied) or opens a dump, and reports the root chains that reach
/// the focus instances plus the types that hold references to them.
///
/// The focus is the CLR type name: an exact match wins, a case-insensitive substring is the
/// fallback, and when it is omitted the largest type in the heap is analysed (the report says so).
/// </summary>
public sealed class RetentionService : IRetentionService
{
    /// <summary>Shared by every pass: a whole-heap search must end with an answer, not a timeout.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private readonly IProfilingAdvisor _advisor;

    public RetentionService(IProfilingAdvisor advisor) => _advisor = advisor;

    public Task<RetentionReport> AnalyzeAsync(
        int? processId, string? dumpPath, string? focusType, int maxPaths, int maxNodes,
        bool includeReferrers, CancellationToken cancellationToken = default)
    {
        if (processId is null && string.IsNullOrWhiteSpace(dumpPath))
            throw new ArgumentException("Provide processId (with profile_processes) or dumpPath.");
        if (dumpPath is not null && !File.Exists(dumpPath))
            throw new FileNotFoundException($"Dump not found: {dumpPath}", dumpPath);

        var paths = Math.Clamp(maxPaths <= 0 ? 3 : maxPaths, 1, 20);
        var nodes = Math.Clamp(maxNodes <= 0 ? 250_000 : maxNodes, 1_000, 2_000_000);

        return Task.Run(
            () => Analyze(processId, dumpPath, focusType, paths, nodes, includeReferrers, cancellationToken),
            cancellationToken);
    }

    private RetentionReport Analyze(
        int? processId, string? dumpPath, string? focusType, int maxPaths, int maxNodes,
        bool includeReferrers, CancellationToken cancellationToken)
    {
        using var session = dumpPath is not null
            ? ClrSession.LoadDump(dumpPath)
            : ClrSession.AttachSnapshot(processId!.Value);

        var graph = new ClrHeapGraph(session);
        var source = dumpPath is not null ? Path.GetFullPath(dumpPath) : $"process {processId}";
        var warnings = new List<string>();

        if (!graph.CanWalkHeap)
            throw new InvalidOperationException(
                $"The managed heap of {source} cannot be walked. A triage/minimal dump has no heap " +
                "(collect with dumpType='withheap'), and a dump taken with another runtime needs the matching DAC on this machine.");

        string focus;
        var auto = string.IsNullOrWhiteSpace(focusType);
        if (auto)
        {
            focus = DominantType(graph, cancellationToken)
                ?? throw new InvalidOperationException("The heap is empty: there is no type to analyse.");
            warnings.Add(
                $"No focusType was given, so the largest type in the heap ('{focus}') is analysed. " +
                "Pass focusType (a substring of the CLR type name) to ask about another one.");
        }
        else
        {
            focus = focusType!.Trim();
        }

        var walk = RetentionWalker.Walk(
            graph, focus, maxPaths, maxNodes, includeReferrers, Budget, cancellationToken);

        var report = new RetentionReport(
            source,
            focus,
            auto,
            walk.Instances.Count,
            walk.Instances.Sum(i => i.Bytes),
            walk.TotalObjects,
            walk.TotalBytes,
            walk.Referrers,
            walk.Paths,
            walk.VisitedNodes,
            walk.InstancesTruncated || !walk.ReferrersComplete,
            walk.PathSearchComplete,
            [],
            [],
            warnings);

        var findings = _advisor.Analyze(report);
        return report with { Findings = findings, NextSteps = _advisor.NextSteps(findings) };
    }

    /// <summary>
    /// The fallback when no focus type is given: one header-only pass to find the type that owns
    /// the most bytes. It is the same pass profile_heap does, and the only extra cost of the
    /// no-focus call.
    /// </summary>
    private static string? DominantType(IHeapGraph graph, CancellationToken cancellationToken)
    {
        var types = new Dictionary<string, long>(StringComparer.Ordinal);
        const int maxObjects = 2_000_000;
        var visited = 0;

        foreach (var node in graph.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > maxObjects)
                break;

            types[node.TypeName] = types.TryGetValue(node.TypeName, out var bytes)
                ? bytes + node.Bytes
                : node.Bytes;
        }

        return types.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
    }
}
