using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Snitch.Profiling.Model;

namespace Snitch.Profiling.Snapshots;

public interface IDumpService
{
    Task<DumpCollectResult> CollectAsync(
        int processId, string? outputPath, string dumpType, CancellationToken cancellationToken = default);

    Task<DumpAnalysis> AnalyzeAsync(
        string dumpPath, int top, string? focusType, bool includeStacks, int maxFrames,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Native replacement for <c>dotnet-dump collect</c> + the common SOS analysis. Collection goes
/// through DiagnosticsClient.WriteDump (the runtime writes the dump itself); analysis is ClrMD, so
/// the same code that inspects a live process answers the questions for a dump.
/// </summary>
public sealed class DumpService : IDumpService
{
    private readonly ProfilingOptions _options;
    private readonly IManagedHeapService _heap;
    private readonly IManagedStackService _stacks;

    public DumpService(ProfilingOptions options, IManagedHeapService heap, IManagedStackService stacks)
    {
        _options = options;
        _heap = heap;
        _stacks = stacks;
    }

    public async Task<DumpCollectResult> CollectAsync(
        int processId, string? outputPath, string dumpType, CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
            throw new ArgumentException("A positive process id is required.", nameof(processId));

        var kind = ParseDumpType(dumpType);
        var processName = SafeProcessName(processId);
        var path = ResolveOutputPath(outputPath, processName, processId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var client = new DiagnosticsClient(processId);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await client.WriteDumpAsync(kind, path, logDumpGeneration: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not collect a dump of process {processId} ({processName}): {ex.Message}. " +
                "The process must be alive and have diagnostics enabled; on Linux the runtime needs ptrace permission.",
                ex);
        }
        stopwatch.Stop();

        return new DumpCollectResult(
            path,
            SafeFileSize(path),
            kind.ToString(),
            processId,
            processName,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    public async Task<DumpAnalysis> AnalyzeAsync(
        string dumpPath, int top, string? focusType, bool includeStacks, int maxFrames,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(dumpPath))
            throw new FileNotFoundException($"Dump not found: {dumpPath}", dumpPath);

        var heap = await _heap.AnalyzeDumpAsync(dumpPath, top, focusType, includeDuplicateStrings: true, cancellationToken)
            .ConfigureAwait(false);

        var note = heap.ObjectCount == 0
            ? "The dump contains no managed heap data (a triage/minimal dump). Collect with dumpType='withheap' or 'full' to analyse the heap."
            : null;

        StackSnapshot? stacks = null;
        if (includeStacks)
        {
            try
            {
                stacks = await _stacks.CaptureDumpAsync(dumpPath, maxFrames, includeRuntimeFrames: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The heap is the primary answer; a dump whose stacks cannot be walked
                // (mismatched runtime/DAC) should still return what was readable.
                note = string.IsNullOrEmpty(note)
                    ? $"The stacks could not be read: {ex.Message}"
                    : note + $" The stacks could not be read: {ex.Message}";
            }
        }

        return new DumpAnalysis(dumpPath, SafeFileSize(dumpPath), heap, stacks, note);
    }

    private static DumpType ParseDumpType(string dumpType) => dumpType.Trim().ToLowerInvariant() switch
    {
        "normal" => DumpType.Normal,
        "withheap" or "heap" => DumpType.WithHeap,
        "triage" => DumpType.Triage,
        "full" => DumpType.Full,
        _ => throw new ArgumentException(
            $"Unknown dump type '{dumpType}'. Use normal, withheap, triage or full."),
    };

    private string ResolveOutputPath(string? outputPath, string processName, int processId)
    {
        if (!string.IsNullOrWhiteSpace(outputPath))
            return Path.GetFullPath(outputPath);

        var directory = _options.ResolveOutputDirectory(Environment.CurrentDirectory);
        var safeName = string.Concat(processName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        return Path.Combine(directory, $"dump-{safeName}-{processId}-{DateTime.Now:yyyyMMdd-HHmmss}.dmp");
    }

    private static string SafeProcessName(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return process.ProcessName; }
        catch { return "process"; }
    }

    private static long SafeFileSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}
