using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Snitch.Profiling.Analysis;

/// <summary>
/// One resolved frame: the display method, <c>file:line</c> when a PDB was found, and — when the
/// trace carried an IL→native map — the exact source position of the sampled instruction. The
/// last two are separate on purpose: <see cref="Source"/> falls back to the method's first line
/// when the IL offset is unknown, while <see cref="File"/>/<see cref="Line"/> stay null so that a
/// per-line cost only ever counts samples it can really place.
/// </summary>
public sealed record TraceFrameInfo(string Method, string? Source, string? File = null, int? Line = null);

/// <summary>A CPU sample from the sample profiler, resolved to a call stack (leaf first).</summary>
public sealed record CpuStackSample(double TimeMs, double MetricMs, IReadOnlyList<TraceFrameInfo> Frames);

/// <summary>
/// An interval in which a thread was sampled outside managed code (External), attributed to the
/// managed stack at the blocking call. That is what an I/O wait, a lock or a sleep looks like in an
/// EventPipe trace with stack walks.
/// </summary>
public sealed record BlockedTraceSample(
    double TimeMs, double DurationMs, int ThreadId, IReadOnlyList<TraceFrameInfo> Frames);

/// <summary>An allocation tick carrying its allocation call stack (leaf first).</summary>
public sealed record AllocationSiteSample(
    double TimeMs, string TypeName, long Bytes, string Kind, IReadOnlyList<TraceFrameInfo> Frames);

/// <summary>An exception event carrying its throw call stack (leaf first).</summary>
public sealed record ExceptionSiteSample(
    double TimeMs, string Type, string? Message, IReadOnlyList<TraceFrameInfo> Frames);

/// <summary>
/// The deep view of a trace: CPU samples, allocation sites and exception throw sites, all with
/// their call stacks. Building it requires the EventPipe → ETLX conversion because that is where
/// per-event stack blocks become call stacks. The model is cached per file so call-tree,
/// allocation-site, comparison and diagnosis runs do not pay the conversion twice.
/// </summary>
public sealed record TraceStackModel(
    string Path,
    double DurationMs,
    long SampleEvents,
    IReadOnlyList<CpuStackSample> CpuSamples,
    IReadOnlyList<BlockedTraceSample> BlockedSamples,
    IReadOnlyList<AllocationSiteSample> Allocations,
    IReadOnlyList<ExceptionSiteSample> Exceptions,
    bool Truncated,
    IReadOnlyList<string> Warnings);

public interface ITraceStackReader
{
    TraceStackModel Read(string tracePath, bool resolveSource = true);
}

/// <summary>
/// Reads the deep view of a .nettrace.
///
/// CPU is taken from the sample profiler's Managed ThreadSample events, not from the
/// thread-time computer: that computer also treats allocation ticks and allocation samples as
/// sampling events, so in a Verbose trace the "CPU" landed on the allocating lambda instead of
/// the method burning CPU (and a thread sampled only at start/end contributed a phantom interval
/// equal to the whole trace). A ThreadSample carries the sampled thread's call stack; consecutive
/// samples of the same thread bound the CPU interval, and External/Error samples (thread not in
/// managed code) are not CPU at all. Gaps far beyond the sampling interval are not CPU either.
/// </summary>
public sealed class TraceStackReader : ITraceStackReader
{
    private const int MaxCpuSamples = 300_000;
    private const int MaxAllocationSamples = 300_000;
    private const int MaxExceptionSamples = 100_000;
    private const int MaxFramesPerStack = 32;
    private const int MaxDistinctStacks = 50_000;
    private const int CacheCapacity = 2;

    private readonly SourceMapResolver _sourceMap;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public TraceStackReader(ProfilingOptions options) => _sourceMap = new SourceMapResolver(options);

    public TraceStackModel Read(string tracePath, bool resolveSource = true)
    {
        if (!File.Exists(tracePath))
            throw new FileNotFoundException($"Trace not found: {tracePath}", tracePath);

        var key = $"{Path.GetFullPath(tracePath)}|{resolveSource}";
        var info = new FileInfo(tracePath);

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached) &&
                cached.Length == info.Length &&
                cached.LastWriteUtc == info.LastWriteTimeUtc)
                return cached.Model;
        }

        var model = ReadCore(tracePath, resolveSource);

        lock (_gate)
        {
            if (_cache.Count >= CacheCapacity)
            {
                var oldest = _cache.OrderBy(kv => kv.Value.CreatedUtc).First().Key;
                _cache.Remove(oldest);
            }
            _cache[key] = new CacheEntry(model, info.Length, info.LastWriteTimeUtc);
        }

        return model;
    }

    private TraceStackModel ReadCore(string tracePath, bool resolveSource)
    {
        var etlxPath = Path.Combine(Path.GetTempPath(), "visualmcp-profiling", $"{Guid.NewGuid():N}.etlx");
        Directory.CreateDirectory(Path.GetDirectoryName(etlxPath)!);

        try
        {
            TraceLog.CreateFromEventPipeDataFile(tracePath, etlxPath);
            using var log = TraceLog.OpenOrConvert(etlxPath);

            var warnings = new List<string>();
            var (rawSamples, allocations, exceptions, truncated) = ReadEvents(log, resolveSource);
            var cpuSamples = BuildCpuSamples(rawSamples, warnings);
            var blockedSamples = BuildBlockedSamples(rawSamples, warnings);

            return new TraceStackModel(
                Path.GetFullPath(tracePath),
                log.SessionEndTimeRelativeMSec,
                rawSamples.Count,
                cpuSamples,
                blockedSamples,
                allocations,
                exceptions,
                truncated,
                warnings);
        }
        finally
        {
            try { if (File.Exists(etlxPath)) File.Delete(etlxPath); } catch { /* best-effort */ }
        }
    }

    private (List<RawThreadSample> Samples, List<AllocationSiteSample> Allocations,
        List<ExceptionSiteSample> Exceptions, bool Truncated) ReadEvents(TraceLog log, bool resolveSource)
    {
        var samples = new List<RawThreadSample>();
        var allocations = new List<AllocationSiteSample>();
        var exceptions = new List<ExceptionSiteSample>();
        var stackCache = new Dictionary<CallStackIndex, IReadOnlyList<TraceFrameInfo>>();
        var truncated = false;

        foreach (var data in log.Events)
        {
            switch (data)
            {
                case ClrThreadSampleTraceData sample:
                {
                    if (samples.Count >= MaxCpuSamples)
                    {
                        truncated = true;
                        break;
                    }

                    var frames = ResolveStack(log, data.CallStack(), stackCache, resolveSource);
                    if (frames.Count > 0)
                        samples.Add(new RawThreadSample(data.TimeStampRelativeMSec, data.ThreadID, sample.Type, frames));
                    break;
                }

                case GCAllocationTickTraceData allocation:
                {
                    if (allocations.Count >= MaxAllocationSamples)
                    {
                        truncated = true;
                        break;
                    }

                    var bytes = allocation.AllocationAmount64 > 0 ? allocation.AllocationAmount64 : allocation.ObjectSize;
                    if (bytes <= 0)
                        break;

                    allocations.Add(new AllocationSiteSample(
                        data.TimeStampRelativeMSec,
                        string.IsNullOrWhiteSpace(allocation.TypeName) ? "<unknown>" : allocation.TypeName,
                        bytes,
                        allocation.AllocationKind.ToString(),
                        ResolveStack(log, allocation.CallStack(), stackCache, resolveSource)));
                    break;
                }

                case ExceptionTraceData exception:
                {
                    if (exceptions.Count >= MaxExceptionSamples)
                    {
                        truncated = true;
                        break;
                    }

                    exceptions.Add(new ExceptionSiteSample(
                        data.TimeStampRelativeMSec,
                        string.IsNullOrWhiteSpace(exception.ExceptionType) ? "<unknown>" : exception.ExceptionType,
                        string.IsNullOrWhiteSpace(exception.ExceptionMessage) ? null : exception.ExceptionMessage,
                        ResolveStack(log, exception.CallStack(), stackCache, resolveSource)));
                    break;
                }
            }
        }

        return (samples, allocations, exceptions, truncated);
    }

    /// <summary>
    /// Converts per-thread sample timestamps into CPU intervals: the time to the next sample on the
    /// same thread belongs to the current stack. A gap far beyond the thread's median interval
    /// means the thread was not running in between, so it is not CPU and is dropped.
    /// </summary>
    private static List<CpuStackSample> BuildCpuSamples(List<RawThreadSample> raw, List<string> warnings)
    {
        var result = new List<CpuStackSample>(raw.Count);
        long dropped = 0;

        foreach (var thread in raw.Where(s => s.Type == ClrThreadSampleType.Managed).GroupBy(s => s.ThreadId))
        {
            var ordered = thread.OrderBy(s => s.TimeMs).ToList();
            if (ordered.Count < 2)
                continue;

            var intervals = new List<double>(ordered.Count - 1);
            for (var i = 0; i < ordered.Count - 1; i++)
            {
                var delta = ordered[i + 1].TimeMs - ordered[i].TimeMs;
                if (delta > 0)
                    intervals.Add(delta);
            }
            if (intervals.Count == 0)
                continue;

            intervals.Sort();
            var median = intervals[intervals.Count / 2];
            var maxReasonable = Math.Max(50.0, median * 4);

            for (var i = 0; i < ordered.Count - 1; i++)
            {
                var delta = ordered[i + 1].TimeMs - ordered[i].TimeMs;
                if (delta <= 0)
                    continue;
                if (delta > maxReasonable)
                {
                    dropped++;
                    continue;
                }

                result.Add(new CpuStackSample(ordered[i].TimeMs, delta, ordered[i].Frames));
            }
        }

        if (dropped > 0)
            warnings.Add(
                $"{dropped:N0} CPU sample interval(s) were far longer than the sampling rate and were ignored " +
                "(the thread was not running in between; counting it would inflate CPU time).");

        return result;
    }

    /// <summary>
    /// Turns External samples into blocked intervals: a thread sampled outside managed code stays
    /// blocked until its next sample. The managed stack carried by the External sample is the call
    /// that is blocking (FileStream.Read, HttpClient.Send, Task.Wait, …). A thread suspended for
    /// seconds (GC, debugger) also shows up as External, so intervals far beyond the thread's
    /// sampling rate are dropped rather than reported as I/O.
    /// </summary>
    private static List<BlockedTraceSample> BuildBlockedSamples(
        List<RawThreadSample> raw, List<string> warnings)
    {
        var result = new List<BlockedTraceSample>();
        long dropped = 0;

        foreach (var thread in raw.GroupBy(s => s.ThreadId))
        {
            var ordered = thread.OrderBy(s => s.TimeMs).ToList();
            if (ordered.Count < 2)
                continue;

            var intervals = new List<double>(ordered.Count - 1);
            for (var i = 0; i < ordered.Count - 1; i++)
            {
                var delta = ordered[i + 1].TimeMs - ordered[i].TimeMs;
                if (delta > 0)
                    intervals.Add(delta);
            }
            if (intervals.Count == 0)
                continue;

            intervals.Sort();
            var median = intervals[intervals.Count / 2];
            var maxReasonable = Math.Max(500.0, median * 50);

            for (var i = 0; i < ordered.Count - 1; i++)
            {
                if (ordered[i].Type != ClrThreadSampleType.External)
                    continue;

                var delta = ordered[i + 1].TimeMs - ordered[i].TimeMs;
                if (delta <= 0)
                    continue;
                if (delta > maxReasonable)
                {
                    dropped++;
                    continue;
                }

                result.Add(new BlockedTraceSample(
                    ordered[i].TimeMs, delta, ordered[i].ThreadId, ordered[i].Frames));
            }
        }

        if (dropped > 0)
            warnings.Add(
                $"{dropped:N0} blocked interval(s) were far longer than the sampling rate and were ignored " +
                "(a thread suspended for that long was not an I/O wait).");

        return result;
    }

    private IReadOnlyList<TraceFrameInfo> ResolveStack(
        TraceLog log,
        TraceCallStack? stack,
        Dictionary<CallStackIndex, IReadOnlyList<TraceFrameInfo>> cache,
        bool resolveSource)
    {
        if (stack is null)
            return [];

        if (cache.TryGetValue(stack.CallStackIndex, out var cached))
            return cached;

        var frames = new List<TraceFrameInfo>(8);
        for (var node = stack; node is not null && frames.Count < MaxFramesPerStack; node = node.Caller)
        {
            try
            {
                var address = node.CodeAddress;
                var name = FrameNames.Normalize(address.FullMethodName);
                SourceResolution location = default;
                if (resolveSource)
                {
                    var token = address.Method is null ? 0 : address.Method.MethodToken;
                    location = _sourceMap.ResolveFrame(address.ModuleFilePath, token, address.ILOffset);
                }
                frames.Add(new TraceFrameInfo(name, location.Display, location.File, location.Line));
            }
            catch
            {
                frames.Add(new TraceFrameInfo("(unknown)", null));
            }
        }

        var result = frames.ToArray();
        if (cache.Count < MaxDistinctStacks)
            cache[stack.CallStackIndex] = result;
        return result;
    }

    private readonly record struct RawThreadSample(
        double TimeMs, int ThreadId, ClrThreadSampleType Type, IReadOnlyList<TraceFrameInfo> Frames);

    private sealed record CacheEntry(TraceStackModel Model, long Length, DateTime LastWriteUtc)
    {
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    }
}
