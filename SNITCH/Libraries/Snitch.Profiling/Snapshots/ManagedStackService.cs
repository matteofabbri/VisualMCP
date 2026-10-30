using Microsoft.Diagnostics.Runtime;
using Snitch.Profiling.Analysis;
using Snitch.Profiling.Model;

namespace Snitch.Profiling.Snapshots;

public interface IManagedStackService
{
    Task<StackSnapshot> CaptureAsync(int processId, int maxFrames, bool includeRuntimeFrames,
        CancellationToken cancellationToken = default);

    Task<StackSnapshot> CaptureDumpAsync(string dumpPath, int maxFrames, bool includeRuntimeFrames,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Native replacement for <c>dotnet-stack</c>: suspends the target just long enough to walk every
/// managed thread with ClrMD, resolves frames to source, and reports sync blocks that have threads
/// waiting on them (the holding thread is named, which is what you need for a hang).
/// </summary>
public sealed class ManagedStackService : IManagedStackService
{
    private const int MaxThreads = 300;
    private const int MaxContendedMonitors = 50;

    private readonly SourceMapResolver _sourceMap;

    public ManagedStackService(ProfilingOptions options) => _sourceMap = new SourceMapResolver(options);

    public Task<StackSnapshot> CaptureAsync(int processId, int maxFrames, bool includeRuntimeFrames,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
            throw new ArgumentException("A positive process id is required.", nameof(processId));

        return Task.Run(() =>
        {
            using var session = ClrSession.AttachSuspended(processId);
            return Capture(session, $"process {processId}", maxFrames, includeRuntimeFrames);
        }, cancellationToken);
    }

    public Task<StackSnapshot> CaptureDumpAsync(string dumpPath, int maxFrames, bool includeRuntimeFrames,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(dumpPath))
            throw new FileNotFoundException($"Dump not found: {dumpPath}", dumpPath);

        return Task.Run(() =>
        {
            using var session = ClrSession.LoadDump(dumpPath);
            return Capture(session, dumpPath, maxFrames, includeRuntimeFrames);
        }, cancellationToken);
    }

    private StackSnapshot Capture(ClrSession session, string source, int maxFrames, bool includeRuntimeFrames)
    {
        var runtime = session.Runtime;
        maxFrames = Math.Clamp(maxFrames <= 0 ? 40 : maxFrames, 1, 300);

        var threads = new List<ThreadStackInfo>();
        var alive = runtime.Threads.Where(t => t.IsAlive).ToList();

        foreach (var thread in alive.Take(MaxThreads))
        {
            var frames = new List<StackFrameInfo>();
            try
            {
                var depth = 0;
                foreach (var frame in thread.EnumerateStackTrace(includeContext: false, maxFrames: maxFrames))
                {
                    if (frame.Kind != ClrStackFrameKind.ManagedMethod || frame.Method is null)
                    {
                        if (!includeRuntimeFrames)
                            continue;
                        frames.Add(new StackFrameInfo(depth++, frame.FrameName ?? "<unknown>", null));
                        continue;
                    }

                    var method = frame.Method;
                    var methodName = DescribeMethod(method);
                    string? location = null;
                    try
                    {
                        var modulePath = method.Type?.Module?.Name;
                        var token = method.MetadataToken;
                        var ilOffset = method.GetILOffset(frame.InstructionPointer);
                        location = _sourceMap.Resolve(modulePath, token, ilOffset);
                    }
                    catch
                    {
                        // ReadyToRun / dynamic methods may not expose module+token+IL offset.
                    }

                    frames.Add(new StackFrameInfo(depth++, methodName, location));
                }
            }
            catch
            {
                // A thread that exits while we walk it: report the frames collected so far.
            }

            threads.Add(new ThreadStackInfo(
                thread.ManagedThreadId,
                thread.OSThreadId,
                thread.State.ToString(),
                thread.IsFinalizer,
                thread.IsGc,
                (int)thread.LockCount,
                SafeExceptionType(thread),
                frames));
        }

        return new StackSnapshot(
            source,
            session.RuntimeVersion,
            threads,
            FindContendedMonitors(runtime),
            Math.Max(0, alive.Count - MaxThreads));
    }

    private static IReadOnlyList<MonitorLock> FindContendedMonitors(ClrRuntime runtime)
    {
        var result = new List<MonitorLock>();
        try
        {
            var threadsByAddress = runtime.Threads
                .Where(t => t.IsAlive)
                .GroupBy(t => t.Address)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var sync in runtime.Heap.EnumerateSyncBlocks())
            {
                if (!sync.IsMonitorHeld || sync.WaitingThreadCount <= 0)
                    continue;

                var objectType = "<unknown>";
                try
                {
                    if (sync.Object != 0)
                        objectType = runtime.Heap.GetObject(sync.Object).Type?.Name ?? "<unknown>";
                }
                catch
                {
                    // The object may have been collected between the sync block read and the lookup.
                }

                var holder = "unknown";
                string? holderStack = null;
                if (threadsByAddress.TryGetValue(sync.HoldingThreadAddress, out var thread))
                {
                    holder = $"{thread.ManagedThreadId} (0x{sync.HoldingThreadAddress:x})";
                    try
                    {
                        var first = thread.EnumerateStackTrace(includeContext: false, maxFrames: 8)
                            .FirstOrDefault(f => f.Kind == ClrStackFrameKind.ManagedMethod && f.Method is not null);
                        holderStack = first is null ? null : DescribeMethod(first.Method!);
                    }
                    catch
                    {
                        // Best-effort: the thread may be running between two reads.
                    }
                }

                result.Add(new MonitorLock(
                    objectType, holder, sync.WaitingThreadCount, sync.RecursionCount, holderStack));

                if (result.Count >= MaxContendedMonitors)
                    break;
            }
        }
        catch
        {
            // Sync-block enumeration is not available on every dump; the stacks are still returned.
        }

        return result.OrderByDescending(m => m.WaitingThreads).ToList();
    }

    private static string DescribeMethod(ClrMethod method)
    {
        var name = method.Name ?? "<unknown>";
        try
        {
            var type = method.Type?.Name;
            return string.IsNullOrWhiteSpace(type) ? name : $"{type}.{name}";
        }
        catch
        {
            return name;
        }
    }

    private static string? SafeExceptionType(ClrThread thread)
    {
        try { return thread.CurrentException?.Type?.Name; }
        catch { return null; }
    }
}
