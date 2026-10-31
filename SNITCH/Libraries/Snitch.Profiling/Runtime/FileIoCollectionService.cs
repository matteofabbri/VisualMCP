using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Snitch.Profiling.Model;

namespace Snitch.Profiling.Runtime;

public interface IFileIoService
{
    /// <summary>
    /// Collects per-file read/write activity for the window and returns who wrote where and how.
    /// <paramref name="processId"/> null captures every process on the machine; the aggregator
    /// applies the filter, because a kernel session cannot be filtered by pid.
    /// </summary>
    Task<FileIoReport> CollectAsync(
        int? processId, int durationSeconds, string? pathFilter, int top,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Visual Studio's File I/O tool, natively: a real-time ETW session with the Windows kernel file
/// provider (<c>Microsoft-Windows-Kernel-File</c>), which is the only place that knows which
/// process wrote which file and how many bytes. This is not EventPipe — kernel providers are not
/// EventPipe providers — so it is Windows-only and needs elevation (or the Performance Log Users
/// group), exactly like the Visual Studio tool.
///
/// <c>FileIOInit</c> is enabled together with <c>FileIO</c> because the read/write events carry a
/// file key, not a name; the init stream is what maps the key to the path (TraceEvent keeps that
/// map in the parser state).
/// </summary>
public sealed class FileIoCollectionService : IFileIoService
{
    public const int DefaultDurationSeconds = 10;
    public const int MaxDurationSeconds = 120;

    public async Task<FileIoReport> CollectAsync(
        int? processId, int durationSeconds, string? pathFilter, int top,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "File I/O attribution needs the Windows kernel ETW provider (Microsoft-Windows-Kernel-File). " +
                "EventPipe has no file-write event, so the same answer on Linux would require eBPF or perf. " +
                "On this machine use profile_io (where threads block in I/O) or profile_allocations (which code writes).");
        }

        var seconds = Math.Clamp(
            durationSeconds <= 0 ? DefaultDurationSeconds : durationSeconds, 1, MaxDurationSeconds);

        var aggregator = new FileIoAggregator(processId, pathFilter);
        var stopwatch = Stopwatch.StartNew();

        using var session = new TraceEventSession(
            $"VisualMCP-FileIO-{Environment.ProcessId}-{Guid.NewGuid():N}");

        try
        {
            session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.FileIOInit);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or Win32Exception or NotSupportedException)
        {
            throw new InvalidOperationException(
                "The kernel file provider could not be enabled. Run the server elevated (Run as administrator) " +
                "or as a member of the 'Performance Log Users' group: a kernel ETW session is a privileged " +
                $"resource. {ex.Message}", ex);
        }

        var source = session.Source;

        // Read/write carry the size; the namespace operations carry the name and the open/close
        // counters. The FileIOInit "name" events are consumed by TraceEvent itself (FileKey →
        // FileName), so they are not subscribed to here.
        source.Kernel.FileIORead += data =>
            aggregator.Add(FileIoOperation.Read, data.ProcessID, data.FileName, data.IoSize);
        source.Kernel.FileIOWrite += data =>
            aggregator.Add(FileIoOperation.Write, data.ProcessID, data.FileName, data.IoSize);
        source.Kernel.FileIOCreate += data =>
            aggregator.Add(FileIoOperation.Open, data.ProcessID, data.FileName);
        source.Kernel.FileIOClose += data =>
            aggregator.Add(FileIoOperation.Close, data.ProcessID, data.FileName);
        source.Kernel.FileIODelete += data =>
            aggregator.Add(FileIoOperation.Delete, data.ProcessID, data.FileName);
        source.Kernel.FileIORename += data =>
            aggregator.Add(FileIoOperation.Rename, data.ProcessID, data.FileName);
        source.Kernel.FileIODirEnum += data =>
            aggregator.Add(FileIoOperation.Enumerate, data.ProcessID, data.FileName);

        var processing = Task.Run(() => source.Process(), CancellationToken.None);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { session.Stop(); } catch { /* the target may have exited, and a stopped stream throws */ }
            try { await processing.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); } catch { /* stopped */ }
        }

        stopwatch.Stop();

        var target = processId is null
            ? "system (all processes)"
            : $"process {processId}";

        return aggregator.Build(target, stopwatch.Elapsed.TotalMilliseconds, top, ResolveProcessName);
    }

    private static string? ResolveProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return $"pid {processId}";
        }
    }
}
