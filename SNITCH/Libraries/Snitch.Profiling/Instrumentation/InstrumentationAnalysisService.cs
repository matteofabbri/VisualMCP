using System.Text.RegularExpressions;
using Snitch.Profiling.Analysis;
using Snitch.Profiling.Model;

namespace Snitch.Profiling.Instrumentation;

public interface IInstrumentationAnalysisService
{
    /// <summary>Exact call counts and inclusive/exclusive times from an instrumented run.</summary>
    InstrumentationReport Analyze(string tracePath, string? mapPath);
}

/// <summary>
/// Reads the events an instrumented assembly produced: one enter and one exit per call, so the report
/// is not an estimate. Two numbers come out of each completed frame — the inclusive time (enter to
/// exit) and the exclusive time (that minus the child frames that ran inside it) — and the call count
/// is the number of frames that closed. Recursion is a stack of frames, so it is measured, not merged.
///
/// The names come from the map file the instrumenter wrote; without it the report still works and
/// names the methods by id, saying the map is missing.
/// </summary>
public sealed class InstrumentationAnalysisService : IInstrumentationAnalysisService
{
    private const string Provider = "Snitch-Profiling-Probe";
    private const int MaxEvents = 2_000_000;

    private static readonly Regex StateMachine = new(
        @"^(?<prefix>.*?)<(?<name>[^<>]+)>d__\d+(?:`\d+)?\.MoveNext$", RegexOptions.Compiled);

    private static readonly Regex Lambda = new(
        @"^(?<prefix>.*?)<(?<name>[^<>]+)>b__[\d_]+$", RegexOptions.Compiled);

    private readonly ITraceEventReader _reader;

    public InstrumentationAnalysisService(ITraceEventReader reader) => _reader = reader;

    public InstrumentationReport Analyze(string tracePath, string? mapPath)
    {
        var set = _reader.Read(tracePath, MaxEvents);
        return Analyze(set, mapPath);
    }

    public static InstrumentationReport Analyze(TraceEventSet set, string? mapPath = null)
    {
        var warnings = new List<string>();
        var events = set.Events
            .Where(record => record.Provider.Contains(Provider, StringComparison.OrdinalIgnoreCase))
            .OrderBy(record => record.TimeMs)
            .ToList();

        var resolvedMap = ResolveMap(set.Path, mapPath);
        var mapFile = resolvedMap.Map;
        var names = new Dictionary<int, string>();
        if (mapFile is not null)
        {
            foreach (var method in mapFile.Methods) names[method.Id] = Clean(method.Name);
        }
        else if (events.Count > 0)
        {
            warnings.Add(
                "No instrumentation map was found: the methods are reported by id. Pass mapPath (the " +
                "'.map.json' file the instrumenter wrote) to get their names.");
        }

        if (events.Count == 0)
        {
            var finding = new ProfilingFinding(
                "instrumentation-no-events", "warning",
                "The trace contains no instrumented call",
                $"No event from {Provider} is in the trace. Either the application that ran was not the " +
                "instrumented copy, or the probe provider was not enabled for the session.",
                "Instrument the assembly (profile_instrumentation action=instrument), run the instrumented copy " +
                "with the probe DLL beside it, and collect with profile_collect profile=instrumentation.",
                "profile_collect profile=instrumentation.",
                null);

            return new InstrumentationReport(
                set.Path, set.DurationMs, false, resolvedMap.Path,
                0, 0, 0, 0, [], [], [finding],
                ["profile_instrumentation action=instrument, then profile_collect profile=instrumentation."],
                warnings);
        }

        var stats = new Dictionary<int, Accumulator>();
        var stacks = new Dictionary<int, Stack<Frame>>();
        long calls = 0;
        double rootMs = 0;
        long unmatched = 0;

        foreach (var record in events)
        {
            var name = EventText.Normalize(record.Name);
            var idText = record.Value("methodId", "MethodId", "methodID", "MethodID");
            if (!int.TryParse(idText, out var id)) continue;

            if (!stacks.TryGetValue(record.ThreadId, out var stack))
                stacks[record.ThreadId] = stack = new Stack<Frame>();

            if (name is "enter")
            {
                stack.Push(new Frame(id, record.TimeMs, stack.Count));
                calls++;
                continue;
            }

            if (name is not "exit") continue;

            if (stack.Count == 0)
            {
                unmatched++;
                continue;
            }

            var frame = stack.Pop();
            if (frame.Id != id)
            {
                // An interleaved exception path can close a different frame: find ours, and count the
                // frames that were left open rather than attributing their time to the wrong method.
                var skipped = 0;
                while (frame.Id != id && stack.Count > 0)
                {
                    skipped++;
                    frame = stack.Pop();
                }

                unmatched += skipped + (frame.Id == id ? 0 : 1);
                if (frame.Id != id) continue;
            }

            var inclusive = record.TimeMs - frame.Start;
            if (inclusive < 0) inclusive = 0;

            if (!stats.TryGetValue(id, out var accumulator))
                stats[id] = accumulator = new Accumulator();

            accumulator.Calls++;
            accumulator.InclusiveMs += inclusive;
            accumulator.ExclusiveMs += Math.Max(0, inclusive - frame.ChildMs);
            accumulator.MaxInclusiveMs = Math.Max(accumulator.MaxInclusiveMs, inclusive);
            accumulator.MaxDepth = Math.Max(accumulator.MaxDepth, frame.Depth + 1);

            if (stack.Count > 0) stack.Peek().ChildMs += inclusive;
            else rootMs += inclusive;
        }

        var unfinished = stacks.Values.Sum(stack => stack.Count);
        if (unfinished > 0)
            warnings.Add(
                $"{unfinished:N0} frame(s) were still open when the trace ended: the run was cut short, or a " +
                "thread died inside an instrumented method.");

        var denominator = rootMs > 0 ? rootMs : Math.Max(1, stats.Values.Sum(value => value.InclusiveMs));
        var all = stats
            .Select(pair => new InstrumentedMethodStat(
                pair.Key,
                names.TryGetValue(pair.Key, out var name) ? name : $"#{pair.Key}",
                pair.Value.Calls,
                Math.Round(pair.Value.InclusiveMs, 2),
                Math.Round(pair.Value.ExclusiveMs, 2),
                Math.Round(pair.Value.MaxInclusiveMs, 2),
                Math.Round(pair.Value.InclusiveMs * 100.0 / denominator, 2),
                pair.Value.MaxDepth))
            .ToList();

        var byCost = all
            .OrderByDescending(method => method.InclusiveMs)
            .Take(25)
            .ToList();

        var byCalls = all
            .OrderByDescending(method => method.Calls)
            .Take(25)
            .ToList();

        var findings = BuildFindings(byCost, byCalls, calls, unfinished, unmatched, mapFile is not null);

        var nextSteps = findings
            .Select(finding => finding.SuggestedAction)
            .Where(step => !string.IsNullOrWhiteSpace(step))
            .Select(step => step!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        return new InstrumentationReport(
            set.Path,
            set.DurationMs,
            true,
            resolvedMap.Path,
            calls,
            all.Count,
            stacks.Count,
            unfinished,
            byCost,
            byCalls,
            findings,
            nextSteps,
            warnings);
    }

    private static IReadOnlyList<ProfilingFinding> BuildFindings(
        IReadOnlyList<InstrumentedMethodStat> byCost,
        IReadOnlyList<InstrumentedMethodStat> byCalls,
        long calls,
        long unfinished,
        long unmatched,
        bool hasMap)
    {
        var findings = new List<ProfilingFinding>();

        var top = byCost.FirstOrDefault();
        if (top is not null && top.Share >= 40 && top.InclusiveMs >= 50)
        {
            findings.Add(new ProfilingFinding(
                "instrumentation-hot-method", top.Share >= 70 ? "critical" : "warning",
                $"{top.Method} owns {top.Share:0.0}% of the measured time",
                $"{top.Calls:N0} call(s), {top.InclusiveMs:N0} ms inclusive, {top.ExclusiveMs:N0} ms exclusive, " +
                $"max {top.MaxInclusiveMs:N0} ms. Exact enter/leave pairs — not a sample.",
                "Open the method: the exclusive time is work it did itself; a large inclusive/exclusive gap means " +
                "the time is in what it calls, so follow the callees in the same table.",
                hasMap ? $"Open {top.Method}" : null,
                null));
        }

        var called = byCalls.FirstOrDefault();
        if (called is not null && called.Calls >= 1_000_000)
        {
            findings.Add(new ProfilingFinding(
                "instrumentation-hot-call-count", "info",
                $"{called.Method} was called {called.Calls:N0} times",
                $"{called.Calls:N0} calls, {called.InclusiveMs:N0} ms inclusive, {called.ExclusiveMs:N0} ms exclusive " +
                $"({called.InclusiveMs / Math.Max(1, called.Calls):0.000} ms per call).",
                "A call count in the millions is per-iteration code: hoist the call, cache its result, or batch the work.",
                null,
                null));
        }

        if (unfinished > 0)
        {
            findings.Add(new ProfilingFinding(
                "instrumentation-unfinished", "info",
                $"{unfinished:N0} call(s) never returned inside the window",
                "Those frames were still open when the trace ended: the operation outlived the capture, or the " +
                "thread that entered them stopped without leaving.",
                "Collect a longer window, or reduce the capture to the operation under study.",
                null,
                null));
        }

        if (unmatched > 0)
        {
            findings.Add(new ProfilingFinding(
                "instrumentation-unmatched", "info",
                $"{unmatched:N0} exit(s) could not be paired",
                "An exception path left a frame open, or the trace started in the middle of a call: their time is " +
                "not attributed to any method.",
                "Start the capture before the operation and keep it running to the end for exact pairs.",
                null,
                null));
        }

        if (calls > 0 && findings.Count == 0)
        {
            findings.Add(new ProfilingFinding(
                "instrumentation-measured", "info",
                "The instrumented run completed",
                $"{calls:N0} calls of {byCost.Count} method(s) were paired exactly.",
                "Sort by inclusive time to find the method that owns the work, by calls to find the hot loop.",
                null,
                null));
        }

        return findings;
    }

    /// <summary>The map the caller named, or the only <c>*.map.json</c> next to the trace.</summary>
    private static (InstrumentationMapFile? Map, string? Path) ResolveMap(string tracePath, string? mapPath)
    {
        if (!string.IsNullOrWhiteSpace(mapPath) && InstrumentationMapFile.Load(mapPath) is { } explicitMap)
            return (explicitMap, System.IO.Path.GetFullPath(mapPath));

        var directory = Path.GetDirectoryName(tracePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return (null, null);

        var candidates = Directory.GetFiles(directory, "*.map.json");
        if (candidates.Length == 0) return (null, null);

        var preferred = candidates.FirstOrDefault(path =>
            Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path))
                .Contains(Path.GetFileNameWithoutExtension(tracePath), StringComparison.OrdinalIgnoreCase))
            ?? candidates[0];

        return (InstrumentationMapFile.Load(preferred), preferred);
    }

    /// <summary>The readable name of a compiler-generated method: its state machine or lambda, not `&lt;Foo&gt;d__3.MoveNext`.</summary>
    private static string Clean(string name)
    {
        var stateMachine = StateMachine.Match(name);
        if (stateMachine.Success)
            return stateMachine.Groups["prefix"].Value + stateMachine.Groups["name"].Value + " (async)";

        var lambda = Lambda.Match(name);
        if (lambda.Success)
            return lambda.Groups["prefix"].Value + lambda.Groups["name"].Value + " (lambda)";

        return name;
    }

    private sealed class Frame(int id, double start, int depth)
    {
        public int Id { get; } = id;

        public double Start { get; } = start;

        public int Depth { get; } = depth;

        public double ChildMs { get; set; }
    }

    private sealed class Accumulator
    {
        public long Calls;

        public double InclusiveMs;

        public double ExclusiveMs;

        public double MaxInclusiveMs;

        public int MaxDepth;
    }
}
