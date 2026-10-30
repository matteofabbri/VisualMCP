using System.Diagnostics;
using System.Text;
using Snitch.Profiling.Model;
using Snitch.Profiling.Snapshots;

namespace Snitch.Profiling.Analysis;

public sealed record RetentionWalkResult(
    long TotalObjects,
    long TotalBytes,
    IReadOnlyList<HeapNode> Instances,
    IReadOnlyList<RetentionPath> Paths,
    IReadOnlyList<ReferrerGroup> Referrers,
    int VisitedNodes,
    bool InstancesTruncated,
    bool PathSearchComplete,
    bool ReferrersComplete,
    double ElapsedMs);

/// <summary>
/// Walks a heap graph to answer "who keeps this type alive?".
///
/// The search is a breadth-first walk from the GC roots: the first time an object is reached, the
/// edge that reached it is remembered, so a target's chain back to a root is reconstructed without
/// a reverse index (which would cost one more full heap in memory). Breadth-first also means the
/// reported path is the shortest one, which is the one that names the actual owner.
///
/// Everything is bounded: nodes visited, targets tracked, referrer objects per type and a wall-clock
/// budget shared by the passes. When a bound is hit the answer says so — "not found" is never
/// reported as "not reachable".
/// </summary>
public static class RetentionWalker
{
    /// <summary>Addresses kept for the focus type. A leak investigation needs the type, not a million instances.</summary>
    public const int MaxInstances = 50_000;

    /// <summary>Referrer objects remembered per type (the count is still exact).</summary>
    public const int MaxReferrerObjects = 20_000;

    public const int MaxReferrerTypes = 10;

    public static RetentionWalkResult Walk(
        IHeapGraph graph,
        string focusType,
        int maxPaths,
        int maxNodes,
        bool includeReferrers,
        TimeSpan budget,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var exact = new List<HeapNode>();
        var segment = new List<HeapNode>();
        var partial = new List<HeapNode>();
        var instancesTruncated = false;
        long totalObjects = 0, totalBytes = 0;

        foreach (var node in graph.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed > budget)
            {
                instancesTruncated = true;
                break;
            }

            totalObjects++;
            totalBytes += node.Bytes;

            // The best available match wins: the full CLR name, then a namespace/type segment
            // ("RetentionProbe" finds ProfilingTests+RetentionProbe but not List<RetentionProbe>),
            // then a plain substring. Without this, asking for a type also collected every list and
            // array that mentions it, and the reported instances were the containers, not the leaks.
            var quality = MatchQuality(node.TypeName, focusType);
            if (quality == 0)
                continue;

            var bucket = quality == 3 ? exact : quality == 2 ? segment : partial;
            if (bucket.Count >= MaxInstances)
                instancesTruncated = true;
            else
                bucket.Add(node);
        }

        var instances = exact.Count > 0 ? exact : segment.Count > 0 ? segment : partial;

        if (instances.Count == 0)
            return new RetentionWalkResult(
                totalObjects, totalBytes, instances, [], [], 0,
                instancesTruncated, PathSearchComplete: true, ReferrersComplete: true,
                stopwatch.Elapsed.TotalMilliseconds);

        var targets = instances.Select(i => i.Address).ToHashSet();
        var (visited, rootLabels, found, searchComplete) = Search(
            graph, targets, maxPaths, maxNodes, budget, stopwatch, cancellationToken);

        var paths = BuildPaths(graph, visited, rootLabels, found);

        IReadOnlyList<ReferrerGroup> referrers = [];
        var referrersComplete = true;
        if (includeReferrers)
            (referrers, referrersComplete) = FindReferrers(
                graph, targets, paths, budget, stopwatch, cancellationToken);

        return new RetentionWalkResult(
            totalObjects, totalBytes, instances, paths, referrers, visited.Count,
            instancesTruncated, searchComplete, referrersComplete,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Breadth-first from every root. <c>Parent == 0</c> marks a root; the edge label is the field
    /// of the parent that led to the child.
    /// </summary>
    private static (Dictionary<ulong, ParentEdge> Visited, Dictionary<ulong, string> Roots,
        List<ulong> Found, bool Complete) Search(
        IHeapGraph graph,
        HashSet<ulong> targets,
        int maxPaths,
        int maxNodes,
        TimeSpan budget,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var visited = new Dictionary<ulong, ParentEdge>();
        var rootLabels = new Dictionary<ulong, string>();
        var found = new List<ulong>();
        var queue = new Queue<ulong>();

        foreach (var root in graph.Roots.OrderBy(r => r.Priority))
        {
            // An interior root points inside an object, not at its head: following it would name an
            // invalid node. They are rare (ref locals/handles) and reported as unattributed.
            if (root.IsInterior)
                continue;

            if (!rootLabels.ContainsKey(root.Address))
                rootLabels[root.Address] = root.Label;

            if (visited.TryAdd(root.Address, new ParentEdge(0, null)))
                queue.Enqueue(root.Address);

            if (targets.Contains(root.Address))
            {
                found.Add(root.Address);
                if (found.Count >= maxPaths)
                    return (visited, rootLabels, found, true);
            }
        }

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visited.Count >= maxNodes || stopwatch.Elapsed > budget)
                return (visited, rootLabels, found, false);

            var address = queue.Dequeue();
            foreach (var edge in graph.References(address))
            {
                if (edge.To == 0 || edge.To == address || visited.ContainsKey(edge.To))
                    continue;

                visited[edge.To] = new ParentEdge(address, edge.Field);

                if (targets.Contains(edge.To))
                {
                    found.Add(edge.To);
                    if (found.Count >= maxPaths)
                        return (visited, rootLabels, found, true);
                }

                queue.Enqueue(edge.To);
            }
        }

        return (visited, rootLabels, found, true);
    }

    private static IReadOnlyList<RetentionPath> BuildPaths(
        IHeapGraph graph,
        Dictionary<ulong, ParentEdge> visited,
        Dictionary<ulong, string> rootLabels,
        List<ulong> found)
    {
        var paths = new List<RetentionPath>(found.Count);

        foreach (var target in found)
        {
            var chain = new List<ulong>();
            var current = target;
            while (true)
            {
                chain.Add(current);
                if (!visited.TryGetValue(current, out var edge) || edge.Parent == 0)
                    break;
                current = edge.Parent;
            }
            chain.Reverse();

            var frames = new List<RetentionFrame>(chain.Count);
            for (var i = 0; i < chain.Count; i++)
            {
                var node = graph.Get(chain[i]) ?? new HeapNode(chain[i], "<unknown>", 0);
                var field = i == 0 ? null : visited[chain[i]].Field;
                frames.Add(new RetentionFrame(node.TypeName, field, node.Bytes));
            }

            var targetNode = graph.Get(target) ?? new HeapNode(target, "<unknown>", 0);
            var root = rootLabels.TryGetValue(chain[0], out var label) ? label : "root";
            paths.Add(new RetentionPath(targetNode.TypeName, targetNode.Bytes, root, frames));
        }

        return paths.OrderBy(p => p.Frames.Count).ToList();
    }

    /// <summary>
    /// Second full pass: which objects point at a target, grouped by referrer type. This is the
    /// expensive half (one reference enumeration per object), which is why it is optional.
    /// </summary>
    private static (IReadOnlyList<ReferrerGroup> Groups, bool Complete) FindReferrers(
        IHeapGraph graph,
        HashSet<ulong> targets,
        IReadOnlyList<RetentionPath> paths,
        TimeSpan budget,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var groups = new Dictionary<string, (long References, HashSet<ulong> Objects)>(StringComparer.Ordinal);
        var complete = true;

        foreach (var node in graph.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed > budget)
            {
                complete = false;
                break;
            }

            foreach (var edge in graph.References(node.Address))
            {
                if (!targets.Contains(edge.To))
                    continue;

                var group = groups.TryGetValue(node.TypeName, out var existing)
                    ? existing
                    : (References: 0L, Objects: new HashSet<ulong>());
                if (group.Objects.Count < MaxReferrerObjects)
                    group.Objects.Add(node.Address);
                groups[node.TypeName] = (group.References + 1, group.Objects);
            }
        }

        var result = groups
            .Select(kv => new ReferrerGroup(
                kv.Key, kv.Value.References, kv.Value.Objects.Count, ExamplePath(kv.Key, paths)))
            .OrderByDescending(g => g.References)
            .Take(MaxReferrerTypes)
            .ToList();

        return (result, complete);
    }

    /// <summary>
    /// 3 = the full CLR name, 2 = one namespace/nested-type segment equals the focus
    /// ("ProfilingTests+RetentionProbe" for "RetentionProbe"), 1 = a plain substring.
    /// </summary>
    private static int MatchQuality(string typeName, string focus)
    {
        if (string.Equals(typeName, focus, StringComparison.Ordinal))
            return 3;

        foreach (var part in typeName.Split('.', '+'))
        {
            if (string.Equals(part, focus, StringComparison.OrdinalIgnoreCase))
                return 2;
        }

        return typeName.Contains(focus, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private static string? ExamplePath(string typeName, IReadOnlyList<RetentionPath> paths)
    {
        foreach (var path in paths)
        {
            if (path.Frames.Any(f => f.TypeName == typeName))
                return FormatPath(path);
        }
        return null;
    }

    /// <summary>
    /// Renders a chain the way it reads in the report: the root, then each object with the field of
    /// its parent that holds it, so the last "via" is the link to break.
    /// </summary>
    public static string FormatPath(RetentionPath path)
    {
        var sb = new StringBuilder(path.Root);
        foreach (var frame in path.Frames)
        {
            sb.Append(" → ").Append(FrameNames.ShortName(frame.TypeName));
            if (frame.Field is not null)
                sb.Append(" [").Append(MemberName(frame.Field)).Append(']');
        }
        return sb.ToString();
    }

    private static string MemberName(string field) =>
        field.StartsWith('[') || !field.Contains('.')
            ? field
            : field[(field.LastIndexOf('.') + 1)..];

    private readonly record struct ParentEdge(ulong Parent, string? Field);
}
