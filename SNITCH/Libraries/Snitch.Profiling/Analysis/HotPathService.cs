using Snitch.Profiling.Model;

namespace Snitch.Profiling.Analysis;

public interface IHotPathService
{
    Task<HotPathReport> AnalyzeAsync(
        string tracePath, string metric, int top, double minPercent, bool resolveSource,
        bool userCodeOnly, string? userCodeRoot, CancellationToken cancellationToken = default);
}

/// <summary>
/// Finds hot paths the way Visual Studio's Hot Path view does — but returns several at once:
///
/// * <c>HotMethods</c> is the fix list: one entry per user method/line, with the cost summed across
///   every stack that goes through it, so two fixes can be planned without a recompile between them.
/// * <c>TopPaths</c> is the concrete root-to-leaf path for each hot leaf, with the deepest user
///   frame to open in the editor.
/// * <c>Branches</c> are the frames where the cost splits between several hot children: that is
///   where the choice is "optimise the caller" instead of "optimise one leaf".
///
/// It works on CPU, sampled allocations or exceptions (and <c>mixed</c> keeps the two lists side by
/// side), because the same stack tree answers all three questions.
/// </summary>
public sealed class HotPathService : IHotPathService
{
    private static readonly string[] Metrics = ["cpu", "allocations", "exceptions", "mixed"];

    private readonly ITraceStackReader _reader;

    public HotPathService(ITraceStackReader reader) => _reader = reader;

    public Task<HotPathReport> AnalyzeAsync(
        string tracePath, string metric, int top, double minPercent, bool resolveSource,
        bool userCodeOnly, string? userCodeRoot, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(tracePath))
            throw new FileNotFoundException($"Trace not found: {tracePath}", tracePath);

        var normalized = (metric ?? "cpu").Trim().ToLowerInvariant();
        if (!Metrics.Contains(normalized))
            throw new ArgumentException(
                $"Unknown metric '{metric}'. Use cpu, allocations, exceptions or mixed.");

        top = Math.Clamp(top <= 0 ? 10 : top, 1, 100);
        minPercent = Math.Clamp(minPercent <= 0 ? 1 : minPercent, 0.1, 50);

        return Task.Run(() => Analyze(tracePath, normalized, top, minPercent, resolveSource, userCodeOnly, userCodeRoot),
            cancellationToken);
    }

    private HotPathReport Analyze(
        string tracePath, string metric, int top, double minPercent, bool resolveSource,
        bool userCodeOnly, string? userCodeRoot)
    {
        var model = _reader.Read(tracePath, resolveSource);
        var warnings = new List<string>(model.Warnings);
        var threshold = minPercent / 100.0;

        var sections = new List<Section>();
        if (metric is "cpu" or "mixed")
            sections.Add(BuildSection(
                "cpu", "ms", model.CpuSamples.Select(s => (s.MetricMs, s.Frames)),
                top, threshold, userCodeRoot, userCodeOnly, warnings));

        if (metric is "allocations" or "mixed")
            sections.Add(BuildSection(
                "allocations", "bytes", model.Allocations.Select(a => ((double)a.Bytes, a.Frames)),
                top, threshold, userCodeRoot, userCodeOnly, warnings));

        if (metric is "exceptions")
            sections.Add(BuildSection(
                "exceptions", "count", model.Exceptions.Select(e => (1.0, e.Frames)),
                top, threshold, userCodeRoot, userCodeOnly, warnings));

        if (sections.All(s => s.Total <= 0))
        {
            warnings.Add(metric switch
            {
                "allocations" => "The trace contains no allocation samples with stacks. Collect with profile='gc' and requestStackwalk=true.",
                "exceptions" => "The trace contains no exception events with stacks.",
                "mixed" => "No CPU or allocation samples with stacks were found.",
                _ => "The trace contains no CPU samples. Collect with profile='cpu' or 'diagnose' while the process is under load.",
            });
        }

        var hotMethods = sections
            .SelectMany(s => s.Methods)
            .OrderByDescending(m => m.Share)
            .Take(top)
            .ToList();

        var topPaths = sections
            .SelectMany(s => s.Paths)
            .OrderByDescending(p => p.Share)
            .Take(top)
            .ToList();

        var branches = sections
            .SelectMany(s => s.Branches)
            .OrderByDescending(b => b.Share)
            .Take(top)
            .ToList();

        var findings = BuildFindings(metric, hotMethods, branches, warnings);
        var total = metric == "mixed" ? 0 : sections.Sum(s => s.Total);
        var unit = metric == "mixed" ? "share" : sections.FirstOrDefault()?.Unit ?? "ms";

        return new HotPathReport(
            Path.GetFullPath(tracePath), metric, unit,
            Math.Round(total, 1), Math.Round(model.DurationMs, 1),
            hotMethods, topPaths, branches, findings, warnings);
    }

    private static Section BuildSection(
        string metric, string unit, IEnumerable<(double Cost, IReadOnlyList<TraceFrameInfo> Frames)> samples,
        int top, double threshold, string? userCodeRoot, bool userCodeOnly, List<string> warnings)
    {
        var root = new Node("(root)", null, -1, null);
        long accepted = 0;

        foreach (var (cost, frames) in samples)
        {
            if (cost <= 0 || frames.Count == 0)
                continue;

            var ordered = frames
                .Reverse()
                .Where(f => !IsSyntheticFrame(f.Method))
                .ToList();
            if (ordered.Count == 0)
                continue;

            accepted++;
            var node = root;
            root.Inclusive += cost;
            root.Samples++;
            foreach (var frame in ordered)
            {
                if (!node.Children.TryGetValue(frame.Method, out var child))
                {
                    child = new Node(frame.Method, frame.Source, node.Depth + 1, node);
                    node.Children[frame.Method] = child;
                }
                child.Inclusive += cost;
                child.Samples++;
                node = child;
            }
            node.Self += cost;
        }

        var total = root.Inclusive;
        if (total <= 0)
            return new Section(metric, unit, 0, [], [], []);

        var nodes = Enumerate(root).ToList();
        var paths = AggregatePaths(nodes, root, total, threshold, userCodeRoot, userCodeOnly, unit, metric);
        var methods = AggregateMethods(paths, total, threshold, metric);
        var branches = AggregateBranches(nodes, root, total, threshold, userCodeRoot, metric);

        if (accepted == 0)
            warnings.Add($"No {metric} samples with stacks were found.");

        return new Section(
            metric, unit, total,
            methods.OrderByDescending(m => m.Cost).Take(top).ToList(),
            paths.OrderByDescending(p => p.Cost).Take(top).ToList(),
            branches.OrderByDescending(b => b.InclusiveCost).Take(top).ToList());
    }

    private static List<HotPathEntry> AggregatePaths(
        IReadOnlyList<Node> nodes, Node root, double total, double threshold,
        string? userCodeRoot, bool userCodeOnly, string unit, string metric)
    {
        var aggregates = new Dictionary<string, PathAggregate>(StringComparer.Ordinal);

        foreach (var leaf in nodes.Where(n => n.Self > 0))
        {
            var frames = new List<Node>();
            for (var node = leaf; node is not null && node.Parent is not null; node = node.Parent)
                frames.Add(node);
            frames.Reverse();

            var path = frames.Select(f => f.Method).ToArray();
            var key = string.Join('\u001f', path);
            var actionable = frames.LastOrDefault(f => IsUnderRoot(f.Source, userCodeRoot))
                             ?? frames.LastOrDefault(f => IsNonFramework(f.Source));
            var hasAnySource = frames.Any(f => !string.IsNullOrWhiteSpace(f.Source));
            if (actionable is null && !hasAnySource)
            {
                // No PDB was resolved (resolveSource=false): a non-framework name is the best
                // available signal for "code the user can change".
                actionable = frames.LastOrDefault(f => IsLikelyUserMethod(f.Method));
            }
            if (actionable is null && !userCodeOnly)
                actionable = frames.LastOrDefault(f => !string.IsNullOrWhiteSpace(f.Source)) ?? frames[^1];

            if (aggregates.TryGetValue(key, out var aggregate))
            {
                aggregate.Self += leaf.Self;
                aggregate.Variants++;
                continue;
            }

            aggregates[key] = new PathAggregate
            {
                Frames = frames,
                Leaf = leaf,
                Self = leaf.Self,
                Variants = 1,
                Actionable = actionable,
                ActionableNode = actionable,
            };
        }

        var result = new List<HotPathEntry>(aggregates.Count);
        foreach (var aggregate in aggregates.Values)
        {
            var leaf = aggregate.Leaf;
            var actionable = aggregate.Actionable;
            var inclusiveNode = aggregate.ActionableNode ?? leaf;
            var selfShare = aggregate.Self / total;
            var inclusiveShare = inclusiveNode.Inclusive / total;
            if (selfShare < threshold / 2)
                continue;

            var recommendation = Recommend(leaf, actionable, selfShare, inclusiveShare, unit);
            result.Add(new HotPathEntry(
                metric,
                leaf.Method,
                leaf.Source,
                actionable?.Method,
                actionable?.Source,
                Math.Round(aggregate.Self, 2),
                Math.Round(selfShare * 100, 2),
                Math.Round(selfShare * 100, 2),
                Math.Round(inclusiveShare * 100, 2),
                aggregate.Variants,
                aggregate.Frames.Select(f => f.Method).ToList(),
                recommendation));
        }

        return result;
    }

    private static List<HotMethodEntry> AggregateMethods(
        IReadOnlyList<HotPathEntry> paths, double total, double threshold, string metric)
    {
        var groups = new Dictionary<(string Method, string? Source), MethodAggregate>();

        foreach (var path in paths)
        {
            if (path.ActionableMethod is null)
                continue;

            var key = (path.ActionableMethod, path.ActionableSource);
            if (!groups.TryGetValue(key, out var aggregate))
            {
                aggregate = new MethodAggregate
                {
                    Method = path.ActionableMethod,
                    Source = path.ActionableSource,
                    Leaf = path.Leaf,
                    Variants = 0,
                    Cost = 0,
                    InclusiveShare = 0,
                };
            }

            aggregate.Cost += path.Cost;
            aggregate.Variants += path.Variants;
            aggregate.InclusiveShare = Math.Max(aggregate.InclusiveShare, path.InclusiveShare);
            groups[key] = aggregate;
        }

        var result = new List<HotMethodEntry>(groups.Count);
        foreach (var aggregate in groups.Values)
        {
            var share = aggregate.Cost / total;
            if (share < threshold)
                continue;

            var recommendation = aggregate.InclusiveShare > share * 100 * 1.5
                ? $"The cost is spread through its callees ({aggregate.Variants} path(s)); reduce how often it is called, batch, or fix the hot leaf {aggregate.Leaf}."
                : $"Self time dominates ({share * 100:0.0}%); optimise this method/line.";

            if (aggregate.Variants >= 5 && share >= 0.10)
                recommendation =
                    $"The same frame appears in {aggregate.Variants} different paths: the cost is caller-side. " +
                    "Reduce the number of calls, batch, or cache the result instead of optimising the leaf alone.";

            result.Add(new HotMethodEntry(
                metric,
                aggregate.Method,
                aggregate.Source,
                Math.Round(aggregate.Cost, 2),
                Math.Round(share * 100, 2),
                Math.Round(aggregate.InclusiveShare, 2),
                aggregate.Variants,
                aggregate.Leaf,
                recommendation));
        }

        return result;
    }

    private static List<HotBranchEntry> AggregateBranches(
        IReadOnlyList<Node> nodes, Node root, double total, double threshold,
        string? userCodeRoot, string metric)
    {
        var result = new List<HotBranchEntry>();

        foreach (var node in nodes)
        {
            if (node.Parent is null)
                continue;

            var share = node.Inclusive / total;
            if (share < threshold)
                continue;

            var hotChildren = node.Children.Values
                .Where(c => c.Inclusive / total >= threshold)
                .OrderByDescending(c => c.Inclusive)
                .ToList();
            if (hotChildren.Count < 2)
                continue;

            var topChild = hotChildren[0];
            var source = node.Source;
            if (!IsUnderRoot(source, userCodeRoot) && !IsNonFramework(source))
            {
                for (var parent = node.Parent; parent is not null; parent = parent.Parent)
                {
                    if (IsUnderRoot(parent.Source, userCodeRoot) || IsNonFramework(parent.Source))
                    {
                        source = parent.Source;
                        break;
                    }
                }
            }

            result.Add(new HotBranchEntry(
                metric,
                node.Method,
                source,
                Math.Round(node.Inclusive, 2),
                Math.Round(share * 100, 2),
                hotChildren.Count,
                topChild.Method,
                Math.Round(topChild.Inclusive * 100.0 / total, 2),
                $"The cost splits here between {hotChildren.Count} hot children. " +
                $"If they share work, hoist/cache it in {node.Method}; otherwise start with {topChild.Method} ({topChild.Inclusive * 100.0 / total:0.0}%)."));
        }

        return result;
    }

    private static IReadOnlyList<ProfilingFinding> BuildFindings(
        string metric, IReadOnlyList<HotMethodEntry> methods, IReadOnlyList<HotBranchEntry> branches,
        List<string> warnings)
    {
        var findings = new List<ProfilingFinding>();
        var top = methods.FirstOrDefault();

        if (top is not null && top.Share >= 25)
            findings.Add(new ProfilingFinding(
                "hot-path", top.Share >= 50 ? "critical" : "warning",
                $"Hot path: {ShortName(top.Method)} owns {top.Share:0.0}% of {metric}",
                $"{top.Method} accounts for {top.Share:0.0}% across {top.Variants} path(s), inclusive {top.InclusiveShare:0.0}%" +
                (top.Source is null ? "." : $" (source: {top.Source})."),
                top.Recommendation,
                top.Source is null ? $"read_symbol {ShortName(top.Method)}" : $"Open {top.Source}",
                top.Source));

        if (top is not null && top.Variants >= 5 && top.Share >= 10)
            findings.Add(new ProfilingFinding(
                "hot-path-fragmented", "info",
                $"The hot cost is spread over {top.Variants} paths through {ShortName(top.Method)}",
                $"{top.Method} appears in {top.Variants} distinct stacks and still owns {top.Share:0.0}%.",
                "Optimising one leaf will move a fraction of the cost; treat the common caller as the target (batch/cache/reduce calls).",
                "profile_calltree on the same trace shows the subtree below it.",
                top.Source));

        var branch = branches.FirstOrDefault(b => b.HotChildren >= 3 && b.Share >= 10);
        if (branch is not null)
            findings.Add(new ProfilingFinding(
                "hot-branch", "info",
                $"The cost splits at {ShortName(branch.Frame)}",
                $"{branch.HotChildren} children each carry at least the threshold, together {branch.Share:0.0}% of {metric}; " +
                $"the largest is {ShortName(branch.TopChild)} ({branch.TopChildShare:0.0}%).",
                branch.Recommendation,
                "profile_calltree for the full subtree.",
                branch.Source));

        if (methods.Count == 0)
            findings.Add(new ProfilingFinding(
                "hot-path-no-user-code", "info",
                "The hot path has no user frame",
                "Every hot leaf is inside a framework/library (or has no PDB), so there is no source line to change.",
                "The actionable change is at the call site: cache results, batch calls, or use a cheaper API. Run profile_symbols if a user PDB is missing.",
                "profile_symbols to download the missing PDB.",
                null));

        if (metric == "mixed")
        {
            var cpu = methods.FirstOrDefault(m => m.Metric == "cpu");
            var alloc = methods.FirstOrDefault(m => m.Metric == "allocations");
            if (cpu is not null && alloc is not null && cpu.Share >= 20 && alloc.Share >= 20)
                findings.Add(new ProfilingFinding(
                    "hot-path-cpu-and-alloc", "info",
                    "CPU and allocations point at the same area",
                    $"CPU: {ShortName(cpu.Method)} ({cpu.Share:0.0}%); allocations: {ShortName(alloc.Method)} ({alloc.Share:0.0}%).",
                    "A single change (cache/reuse the object) can improve both signals; treat this as the first target.",
                    null,
                    cpu.Source ?? alloc.Source));
        }

        return findings;
    }

    private static string Recommend(
        Node leaf, Node? actionable, double selfShare, double inclusiveShare, string unit)
    {
        var what = unit switch
        {
            "bytes" => "allocation",
            "count" => "exceptions",
            _ => "CPU",
        };

        if (actionable is null)
            return $"No user frame in this path: the {what} is inside {leaf.Method}. Act at the call site (cache/batch) or download the user PDB.";

        if (actionable == leaf)
            return $"Self {what} dominates ({selfShare * 100:0.0}%); optimise this method/line.";

        if (leaf.Source is null)
            return $"The cost is inside {leaf.Method} below {actionable.Method}; reduce the calls, cache the result, or use a cheaper API.";

        return inclusiveShare > selfShare * 2
            ? $"Most of the cost is below {actionable.Method}; fix the hot leaf {leaf.Method} or reduce how often {actionable.Method} runs."
            : $"Self cost collects in {leaf.Method} under {actionable.Method}; optimise the leaf and check the caller loop.";
    }

    private static bool IsSyntheticFrame(string method) => UserCode.IsSyntheticFrame(method);

    private static bool IsUnderRoot(string? source, string? userCodeRoot) =>
        UserCode.IsUnderRoot(source, userCodeRoot);

    private static bool IsNonFramework(string? source) => UserCode.IsNonFramework(source);

    private static bool IsLikelyUserMethod(string method) => UserCode.IsLikelyUserMethod(method);

    private static string ShortName(string fullName) => FrameNames.ShortName(fullName);

    private static IEnumerable<Node> Enumerate(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in node.Children.Values)
                stack.Push(child);
        }
    }

    private sealed record Section(
        string Metric, string Unit, double Total,
        List<HotMethodEntry> Methods, List<HotPathEntry> Paths, List<HotBranchEntry> Branches);

    private sealed class Node(string method, string? source, int depth, Node? parent)
    {
        public string Method { get; } = method;
        public string? Source { get; } = source;
        public int Depth { get; } = depth;
        public Node? Parent { get; } = parent;
        public double Inclusive { get; set; }
        public double Self { get; set; }
        public long Samples { get; set; }
        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
    }

    private sealed class PathAggregate
    {
        public List<Node> Frames { get; init; } = [];
        public Node Leaf { get; init; } = null!;
        public double Self { get; set; }
        public int Variants { get; set; }
        public Node? Actionable { get; init; }
        public Node? ActionableNode { get; init; }
    }

    private sealed class MethodAggregate
    {
        public string Method { get; init; } = "";
        public string? Source { get; init; }
        public string Leaf { get; set; } = "";
        public double Cost { get; set; }
        public double InclusiveShare { get; set; }
        public int Variants { get; set; }
    }
}
