using System.Text;
using Snitch.Profiling.Model;

namespace Snitch.Profiling.Analysis;

public interface ICallTreeService
{
    Task<CallTreeReport> BuildAsync(
        string tracePath, int top, int maxDepth, bool resolveSource, string? foldedPath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Aggregates CPU samples into a call tree with inclusive and exclusive time. This is the layer
/// above a flat hotspot list: a method can be cheap itself and still own most of the time through
/// a callee, and only an inclusive tree shows that. The same pass can emit a folded-stacks file
/// (FlameGraph / speedscope format) for a visual view.
/// </summary>
public sealed class CallTreeService : ICallTreeService
{
    private readonly ITraceStackReader _reader;

    public CallTreeService(ITraceStackReader reader) => _reader = reader;

    public Task<CallTreeReport> BuildAsync(
        string tracePath, int top, int maxDepth, bool resolveSource, string? foldedPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(tracePath))
            throw new FileNotFoundException($"Trace not found: {tracePath}", tracePath);

        top = Math.Clamp(top <= 0 ? 40 : top, 1, 500);
        maxDepth = Math.Clamp(maxDepth <= 0 ? 12 : maxDepth, 1, 200);

        return Task.Run(() => Build(tracePath, top, maxDepth, resolveSource, foldedPath), cancellationToken);
    }

    private CallTreeReport Build(string tracePath, int top, int maxDepth, bool resolveSource, string? foldedPath)
    {
        var model = _reader.Read(tracePath, resolveSource);
        var warnings = new List<string>(model.Warnings);

        var root = new Node("(root)", null, 0, string.Empty);
        double totalMs = 0;

        foreach (var sample in model.CpuSamples)
        {
            if (sample.Frames.Count == 0)
                continue;

            totalMs += sample.MetricMs;
            var node = root;
            for (var i = sample.Frames.Count - 1; i >= 0; i--)
            {
                var frame = sample.Frames[i];
                if (!node.Children.TryGetValue(frame.Method, out var child))
                {
                    child = new Node(
                        frame.Method, frame.Source, node.Depth + 1,
                        node.Depth == 0 ? frame.Method : node.Path + " -> " + frame.Method);
                    node.Children[frame.Method] = child;
                }

                child.InclusiveMs += sample.MetricMs;
                child.Samples++;
                if (i == 0)
                    child.ExclusiveMs += sample.MetricMs;
                node = child;
            }
        }

        if (model.CpuSamples.Count == 0)
            warnings.Add(
                "No CPU samples were found. The trace may not be a cpu/runtime profile, or the process was idle; " +
                "collect with profile='cpu' while it is under load.");

        var all = new List<Node>();
        Collect(root, all, maxDepth);

        var total = Math.Max(1.0, totalMs);
        var topInclusive = all
            .Where(n => n.Depth > 0)
            .OrderByDescending(n => n.InclusiveMs)
            .Take(top)
            .Select(n => new CallTreeNode(
                n.Method, n.Source, Math.Round(n.InclusiveMs, 1), Math.Round(n.ExclusiveMs, 1),
                n.Samples, n.Depth, n.Path))
            .ToList();

        var topExclusive = all
            .Where(n => n.Depth > 0)
            .OrderByDescending(n => n.ExclusiveMs)
            .Take(top)
            .Select(n => new CallTreeNode(
                n.Method, n.Source, Math.Round(n.InclusiveMs, 1), Math.Round(n.ExclusiveMs, 1),
                n.Samples, n.Depth, n.Path))
            .ToList();

        var roots = root.Children.Values
            .OrderByDescending(n => n.InclusiveMs)
            .Take(20)
            .Select(n => new CallTreeNode(
                n.Method, n.Source, Math.Round(n.InclusiveMs, 1), Math.Round(n.ExclusiveMs, 1),
                n.Samples, n.Depth, n.Path))
            .ToList();

        string? writtenFolded = null;
        long foldedStacks = 0;
        if (!string.IsNullOrWhiteSpace(foldedPath) && model.CpuSamples.Count > 0)
        {
            (writtenFolded, foldedStacks) = WriteFolded(model, foldedPath!, tracePath);
        }

        return new CallTreeReport(
            Path.GetFullPath(tracePath),
            Math.Round(totalMs, 1),
            topInclusive,
            topExclusive,
            roots,
            writtenFolded,
            foldedStacks,
            warnings);
    }

    private static (string Path, long Stacks) WriteFolded(
        TraceStackModel model, string foldedPath, string tracePath)
    {
        var path = ResolveFoldedPath(foldedPath, tracePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var stacks = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var sample in model.CpuSamples)
        {
            if (sample.Frames.Count == 0)
                continue;

            var frames = sample.Frames.Reverse().Select(f => Sanitize(f.Method));
            var key = string.Join(';', frames);
            stacks[key] = (stacks.TryGetValue(key, out var ms) ? ms : 0) + sample.MetricMs;
        }

        var builder = new StringBuilder();
        foreach (var stack in stacks.OrderByDescending(kv => kv.Value))
        {
            var weight = Math.Max(1, (int)Math.Round(stack.Value));
            builder.Append(stack.Key).Append(' ').AppendLine(weight.ToString());
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return (Path.GetFullPath(path), stacks.Count);
    }

    private static string ResolveFoldedPath(string foldedPath, string tracePath)
    {
        var full = Path.GetFullPath(foldedPath);
        if (Directory.Exists(full) || foldedPath.EndsWith(Path.DirectorySeparatorChar) ||
            foldedPath.EndsWith(Path.AltDirectorySeparatorChar))
        {
            var stem = Path.GetFileNameWithoutExtension(tracePath);
            return Path.Combine(full, stem + ".folded");
        }

        return full;
    }

    private static string Sanitize(string method) =>
        method.Replace(';', ',');

    private static void Collect(Node node, List<Node> result, int maxDepth)
    {
        foreach (var child in node.Children.Values)
        {
            result.Add(child);
            if (child.Depth < maxDepth)
                Collect(child, result, maxDepth);
        }
    }

    private sealed class Node(string method, string? source, int depth, string path)
    {
        public string Method { get; } = method;
        public string? Source { get; } = source;
        public int Depth { get; } = depth;
        public string Path { get; } = path;
        public double InclusiveMs { get; set; }
        public double ExclusiveMs { get; set; }
        public long Samples { get; set; }
        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
    }
}
