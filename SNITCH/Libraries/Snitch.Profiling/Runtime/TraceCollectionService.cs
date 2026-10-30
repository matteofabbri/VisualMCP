using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Snitch.Profiling.Model;

namespace Snitch.Profiling.Runtime;

public sealed record LiveTraceSessionInfo(
    string SessionId,
    int ProcessId,
    string ProcessName,
    string Path,
    DateTime StartedAt,
    string Profile,
    IReadOnlyList<string> Providers);

public interface ITraceCollectionService
{
    Task<TraceCollectResult> CollectAsync(
        int processId, string? outputPath, string profile, string? customProviders,
        int durationSeconds, int bufferMb, bool requestStackwalk = true, CancellationToken cancellationToken = default);

    LiveTraceSessionInfo Start(
        int processId, string? outputPath, string profile, string? customProviders, int bufferMb,
        bool requestStackwalk = true);

    Task<TraceCollectResult> StopAsync(string sessionId, CancellationToken cancellationToken = default);

    IReadOnlyList<LiveTraceSessionInfo> ListSessions();
}

/// <summary>
/// Starts an EventPipe session against a live process and writes a .nettrace file — the same
/// transport dotnet-trace uses, without spawning it. Two shapes are supported: a bounded
/// collection (start, wait, stop) and a background session the caller stops explicitly.
/// </summary>
public sealed class TraceCollectionService : ITraceCollectionService
{
    private readonly ProfilingOptions _options;
    private readonly ConcurrentDictionary<string, LiveSession> _sessions = new(StringComparer.Ordinal);

    public TraceCollectionService(ProfilingOptions options) => _options = options;

    public async Task<TraceCollectResult> CollectAsync(
        int processId, string? outputPath, string profile, string? customProviders,
        int durationSeconds, int bufferMb, bool requestStackwalk = true,
        CancellationToken cancellationToken = default)
    {
        var seconds = Math.Clamp(durationSeconds <= 0 ? _options.DefaultDurationSeconds : durationSeconds,
            1, _options.MaxDurationSeconds);

        var session = Start(processId, outputPath, profile, customProviders, bufferMb, requestStackwalk);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await StopCoreAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        }

        return new TraceCollectResult(
            session.ProcessId, session.ProcessName, session.Path,
            SafeFileSize(session.Path), session.Profile,
            seconds * 1000.0, session.Providers);
    }

    public LiveTraceSessionInfo Start(
        int processId, string? outputPath, string profile, string? customProviders, int bufferMb,
        bool requestStackwalk = true)
    {
        if (processId <= 0)
            throw new ArgumentException("A positive process id is required.", nameof(processId));

        var (providers, preset) = TraceProviderBuilder.Build(profile, customProviders, requestStackwalk);
        var processName = SafeProcessName(processId);
        var path = ResolveOutputPath(outputPath, processName, processId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var client = new DiagnosticsClient(processId);
        var requestStackwalkEffective = preset.RequestStackwalk ||
            providers.Any(p => string.Equals(p.Name, TraceProviderBuilder.SampleProfilerProvider, StringComparison.OrdinalIgnoreCase));
        var configuration = new EventPipeSessionConfiguration(
            providers,
            circularBufferSizeMB: Math.Clamp(bufferMb <= 0 ? 256 : bufferMb, 1, 4096),
            requestRundown: true,
            requestStackwalk: requestStackwalkEffective);

        EventPipeSession session;
        try
        {
            session = client.StartEventPipeSession(configuration);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start an EventPipe session on process {processId} ({processName}): {ex.Message}. " +
                "Check that the process is a .NET process, that it is still running, and that diagnostics are not disabled " +
                "(DOTNET_EnableDiagnostics=0).", ex);
        }

        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        var copyTask = CopyAsync(session, stream);

        var live = new LiveSession(
            SessionId: Guid.NewGuid().ToString("N")[..12],
            Client: client,
            Session: session,
            Stream: stream,
            CopyTask: copyTask,
            Info: new LiveTraceSessionInfo(
                SessionId: string.Empty,
                ProcessId: processId,
                ProcessName: processName,
                Path: path,
                StartedAt: DateTime.UtcNow,
                Profile: preset.Id,
                Providers: providers.Select(p => p.Name).Distinct().ToList()));

        var info = live.Info with { SessionId = live.SessionId };
        live = live with { Info = info };
        _sessions[live.SessionId] = live;

        return info;
    }

    public async Task<TraceCollectResult> StopAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var result = await StopCoreAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new TraceCollectResult(
            result.ProcessId, result.ProcessName, result.Path, SafeFileSize(result.Path),
            result.Profile, (DateTime.UtcNow - result.StartedAt).TotalMilliseconds, result.Providers);
    }

    public IReadOnlyList<LiveTraceSessionInfo> ListSessions() =>
        _sessions.Values.Select(s => s.Info).OrderBy(s => s.StartedAt).ToList();

    private async Task<LiveTraceSessionInfo> StopCoreAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryRemove(sessionId, out var live))
            throw new ArgumentException($"Unknown trace session '{sessionId}'. Call profile_collect with action='sessions' to list them.");

        try { live.Session.Stop(); } catch { /* the target may have exited */ }

        try
        {
            await live.CopyTask.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A stopped session can surface an ObjectDisposedException on the copy task; the
            // bytes written so far are still a valid prefix of the trace.
        }

        try { await live.Stream.FlushAsync(cancellationToken).ConfigureAwait(false); } catch { /* best-effort */ }
        try { await live.Stream.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        try { live.Session.Dispose(); } catch { /* best-effort */ }

        return live.Info;
    }

    private static async Task CopyAsync(EventPipeSession session, FileStream stream)
    {
        try
        {
            await session.EventStream.CopyToAsync(stream).ConfigureAwait(false);
        }
        finally
        {
            await stream.FlushAsync().ConfigureAwait(false);
        }
    }

    private string ResolveOutputPath(string? outputPath, string processName, int processId)
    {
        if (!string.IsNullOrWhiteSpace(outputPath))
            return Path.GetFullPath(outputPath);

        var directory = _options.ResolveOutputDirectory(Environment.CurrentDirectory);
        var safeName = string.Concat(processName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        return Path.Combine(directory,
            $"trace-{safeName}-{processId}-{DateTime.Now:yyyyMMdd-HHmmss}.nettrace");
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

    private sealed record LiveSession(
        string SessionId,
        DiagnosticsClient Client,
        EventPipeSession Session,
        FileStream Stream,
        Task CopyTask,
        LiveTraceSessionInfo Info);
}
