using Microsoft.Diagnostics.Runtime;
using Snitch.Profiling.Model;

namespace Snitch.Profiling.Snapshots;

public interface IManagedHeapService
{
    Task<HeapSnapshot> CaptureAsync(
        int processId, int top, string? focusType, bool includeDuplicateStrings,
        CancellationToken cancellationToken = default);

    Task<HeapSnapshot> AnalyzeDumpAsync(
        string dumpPath, int top, string? focusType, bool includeDuplicateStrings,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Native replacement for <c>dotnet-gcdump</c>: it enumerates the managed heap with ClrMD and
/// produces the numbers you actually read a gcdump for — bytes per type, LOH and pinned bytes,
/// finalizable objects, the largest objects and duplicated strings — without writing a .gcdump.
/// The process is paused only while its memory is snapshotted.
/// </summary>
public sealed class ManagedHeapService : IManagedHeapService
{
    private const int DefaultMaxObjects = 2_000_000;
    private const int MaxStringSamples = 100_000;
    private const int MaxFocusSamples = 25;

    public Task<HeapSnapshot> CaptureAsync(
        int processId, int top, string? focusType, bool includeDuplicateStrings,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
            throw new ArgumentException("A positive process id is required.", nameof(processId));

        return Task.Run(() =>
        {
            using var session = ClrSession.AttachSnapshot(processId);
            return Analyze(session, $"process {processId}", top, focusType, includeDuplicateStrings);
        }, cancellationToken);
    }

    public Task<HeapSnapshot> AnalyzeDumpAsync(
        string dumpPath, int top, string? focusType, bool includeDuplicateStrings,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(dumpPath))
            throw new FileNotFoundException($"Dump not found: {dumpPath}", dumpPath);

        return Task.Run(() =>
        {
            using var session = ClrSession.LoadDump(dumpPath);
            return Analyze(session, dumpPath, top, focusType, includeDuplicateStrings);
        }, cancellationToken);
    }

    private static HeapSnapshot Analyze(
        ClrSession session, string source, int top, string? focusType, bool includeDuplicateStrings)
    {
        var runtime = session.Runtime;
        var heap = runtime.Heap;
        top = Math.Clamp(top <= 0 ? 25 : top, 1, 200);

        var types = new Dictionary<string, (long Count, long Bytes)>(StringComparer.Ordinal);
        var stringCounts = new Dictionary<string, (int Count, long Bytes)>(StringComparer.Ordinal);
        var samples = new List<HeapObjectSample>();
        var largest = new List<HeapObjectSample>();

        long totalBytes = 0, objectCount = 0, freeBytes = 0, lohBytes = 0, pinnedBytes = 0;
        var truncated = false;

        foreach (var segment in heap.Segments)
        {
            var isLarge = segment.Kind == GCSegmentKind.Large;
            var isPinned = segment.Kind == GCSegmentKind.Pinned;

            foreach (var obj in segment.EnumerateObjects())
            {
                if (!obj.IsValid)
                    continue;

                var size = (long)obj.Size;

                if (obj.IsFree || (obj.Type?.IsFree ?? false))
                {
                    freeBytes += size;
                    continue;
                }

                var typeName = SafeTypeName(obj);
                objectCount++;
                totalBytes += size;
                if (isLarge) lohBytes += size;
                if (isPinned) pinnedBytes += size;

                var current = types.TryGetValue(typeName, out var stats) ? stats : default;
                types[typeName] = (current.Count + 1, current.Bytes + size);

                if (largest.Count < 10 || size > largest[^1].Bytes)
                {
                    largest.Add(new HeapObjectSample($"0x{obj.Address:x}", typeName, size, IsFinalizable(obj)));
                    largest = largest.OrderByDescending(s => s.Bytes).Take(10).ToList();
                }

                if (!string.IsNullOrWhiteSpace(focusType) &&
                    typeName.Contains(focusType, StringComparison.OrdinalIgnoreCase) &&
                    samples.Count < MaxFocusSamples)
                {
                    samples.Add(new HeapObjectSample($"0x{obj.Address:x}", typeName, size, IsFinalizable(obj)));
                }

                if (includeDuplicateStrings &&
                    obj.Type?.IsString == true &&
                    stringCounts.Count < MaxStringSamples)
                {
                    try
                    {
                        var value = obj.AsString(120);
                        if (!string.IsNullOrEmpty(value) && value.Length >= 8)
                        {
                            var entry = stringCounts.TryGetValue(value, out var existing) ? existing : default;
                            stringCounts[value] = (entry.Count + 1, entry.Bytes + size);
                        }
                    }
                    catch
                    {
                        // A string that changed under us; skip it.
                    }
                }

                if (objectCount >= DefaultMaxObjects)
                {
                    truncated = true;
                    break;
                }
            }

            if (truncated)
                break;
        }

        var duplicates = includeDuplicateStrings
            ? stringCounts
                .Where(kv => kv.Value.Count > 1)
                .Select(kv => new DuplicateString(
                    kv.Key.Length > 120 ? kv.Key[..120] : kv.Key,
                    kv.Value.Count,
                    (kv.Value.Count - 1) * kv.Value.Bytes))
                .OrderByDescending(d => d.WastedBytes)
                .Take(15)
                .ToList()
            : [];

        var topTypes = types
            .Select(kv => new HeapTypeStat(
                kv.Key, kv.Value.Count, kv.Value.Bytes,
                totalBytes == 0 ? 0 : Math.Round(kv.Value.Bytes * 100.0 / totalBytes, 2)))
            .OrderByDescending(t => t.Bytes)
            .Take(top)
            .ToList();

        var finalizable = 0L;
        try { finalizable = heap.EnumerateFinalizableObjects().Take(200_000).LongCount(); }
        catch { /* the finalizer queue is not always readable */ }

        return new HeapSnapshot(
            source,
            session.RuntimeVersion,
            heap.IsServer,
            runtime.Threads.Length,
            totalBytes,
            objectCount,
            freeBytes,
            lohBytes,
            pinnedBytes,
            finalizable,
            truncated,
            topTypes,
            samples.Count > 0 ? samples : largest,
            duplicates);
    }

    private static string SafeTypeName(ClrObject obj)
    {
        try { return obj.Type?.Name ?? "<unknown>"; }
        catch { return "<unknown>"; }
    }

    private static bool IsFinalizable(ClrObject obj)
    {
        try { return obj.Type?.IsFinalizable ?? false; }
        catch { return false; }
    }
}
