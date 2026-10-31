using System.Net;
using Snitch.Profiling;
using Snitch.Profiling.Model;
using Snitch.Profiling.Monitor;
using Snitch.Profiling.Runtime;
using Snitch.Profiling.Snapshots;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The native monitor is a real HTTP listener in the test process: these tests start it on port 0,
/// call it with HttpClient, and check that each route is answered by the injected service — the
/// point of the reimplementation is that no external dotnet-monitor process is involved.
/// </summary>
public sealed class ProfilingMonitorTests
{
    [Fact]
    public async Task Native_Monitor_Serves_The_Routes_And_Stops()
    {
        var monitor = CreateMonitor();
        var status = await monitor.StartAsync(Environment.CurrentDirectory, "http://127.0.0.1:0");

        try
        {
            Assert.True(status.Running);
            Assert.Equal("native", status.Mode);
            Assert.NotNull(status.Url);
            using var http = new HttpClient();

            var info = await http.GetStringAsync(status.Url + "/info");
            Assert.Contains("\"mode\":\"native\"", info, StringComparison.Ordinal);

            var processes = await http.GetStringAsync(status.Url + "/processes");
            Assert.Contains("FakeApp", processes, StringComparison.Ordinal);

            var metrics = await http.GetStringAsync(status.Url + "/metrics?pid=42&durationSeconds=1");
            Assert.Contains("cpu-usage", metrics, StringComparison.Ordinal);

            var prometheus = await http.GetStringAsync(
                status.Url + "/metrics?pid=42&durationSeconds=1&format=prometheus");
            Assert.Contains("# TYPE system_runtime_cpu_usage gauge", prometheus, StringComparison.Ordinal);

            var fileIo = await http.GetStringAsync(status.Url + "/fileio?pid=42&durationSeconds=1");
            Assert.Contains("app.log", fileIo, StringComparison.Ordinal);

            var probe = await monitor.ProbeAsync(null, "/info");
            Assert.Equal(200, probe.StatusCode);
            Assert.Contains("native", probe.BodyPreview, StringComparison.Ordinal);

            var missing = await http.GetAsync(status.Url + "/nope");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            var traversal = await http.GetAsync(
                status.Url + "/artifact?path=" + Uri.EscapeDataString("../secret.txt"));
            Assert.Equal(HttpStatusCode.Forbidden, traversal.StatusCode);
        }
        finally
        {
            var stopped = monitor.Stop();
            Assert.False(stopped.Running);
        }
    }

    [Fact]
    public async Task Native_Monitor_Is_Idempotent_And_Rejects_A_Non_Loopback_Url()
    {
        var monitor = CreateMonitor();

        var invalid = await monitor.StartAsync(Environment.CurrentDirectory, "http://example.com:52323");
        Assert.False(invalid.Running);
        Assert.Contains("loopback", invalid.Error, StringComparison.OrdinalIgnoreCase);

        var started = await monitor.StartAsync(Environment.CurrentDirectory, "http://127.0.0.1:0");
        Assert.True(started.Running);

        try
        {
            var again = await monitor.StartAsync(Environment.CurrentDirectory, null);
            Assert.True(again.Running);
            Assert.Contains("already running", again.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            monitor.Stop();
        }

        var stopped = monitor.Stop();
        Assert.Contains("not running", stopped.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static NativeMonitorService CreateMonitor() => new(
        new ProfilingOptions(),
        new FakeProcesses(),
        new FakeCounters(),
        new UnusedTraces(),
        new UnusedDumps(),
        new UnusedHeap(),
        new UnusedStacks(),
        new FakeFileIo());

    private sealed class FakeProcesses : IProfiledProcessService
    {
        private static readonly ProfiledProcess App =
            new(42, "FakeApp", @"C:\fake\FakeApp.exe", 12_345, null, false);

        public IReadOnlyList<ProfiledProcess> List(string? nameFilter = null) => [App];

        public (ProfiledProcess? Target, string? Error) Resolve(int? pid, string? name) => (App, null);
    }

    private sealed class FakeCounters : ICounterCollectionService
    {
        public IReadOnlyList<string> DefaultProviders => ["System.Runtime"];

        public Task<CounterCollectionResult> CollectAsync(
            int processId, IReadOnlyList<string>? counterFilter, int durationSeconds, int intervalSeconds,
            IReadOnlyList<string>? providerNames = null, CancellationToken cancellationToken = default)
        {
            var series = new CounterSeries(
                "System.Runtime", "cpu-usage", "CPU Usage", "%", "Mean",
                1, 2, 1.5, 2, 2,
                [new CounterPoint(0, 1), new CounterPoint(1000, 2)]);

            return Task.FromResult(new CounterCollectionResult(
                processId, "FakeApp", durationSeconds * 1000.0, intervalSeconds,
                ["System.Runtime"], [series], [], []));
        }
    }

    private sealed class FakeFileIo : IFileIoService
    {
        public Task<FileIoReport> CollectAsync(
            int? processId, int durationSeconds, string? pathFilter, int top,
            CancellationToken cancellationToken = default)
        {
            var aggregator = new FileIoAggregator(processId);
            aggregator.Add(FileIoOperation.Write, processId ?? 42, @"C:\logs\app.log", 4096);
            var report = aggregator.Build(
                processId is null ? "system" : $"process {processId}", 1000, top, _ => "FakeApp");
            return Task.FromResult(report);
        }
    }

    private sealed class UnusedTraces : ITraceCollectionService
    {
        public Task<TraceCollectResult> CollectAsync(
            int processId, string? outputPath, string profile, string? customProviders,
            int durationSeconds, int bufferMb, bool requestStackwalk = true,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public LiveTraceSessionInfo Start(
            int processId, string? outputPath, string profile, string? customProviders, int bufferMb,
            bool requestStackwalk = true) => throw new NotSupportedException();

        public Task<TraceCollectResult> StopAsync(string sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IReadOnlyList<LiveTraceSessionInfo> ListSessions() => [];
    }

    private sealed class UnusedDumps : IDumpService
    {
        public Task<DumpCollectResult> CollectAsync(
            int processId, string? outputPath, string dumpType, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DumpAnalysis> AnalyzeAsync(
            string dumpPath, int top, string? focusType, bool includeStacks, int maxFrames,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedHeap : IManagedHeapService
    {
        public Task<HeapSnapshot> CaptureAsync(
            int processId, int top, string? focusType, bool includeDuplicateStrings,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<HeapSnapshot> AnalyzeDumpAsync(
            string dumpPath, int top, string? focusType, bool includeDuplicateStrings,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedStacks : IManagedStackService
    {
        public Task<StackSnapshot> CaptureAsync(
            int processId, int maxFrames, bool includeRuntimeFrames,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StackSnapshot> CaptureDumpAsync(
            string dumpPath, int maxFrames, bool includeRuntimeFrames,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
