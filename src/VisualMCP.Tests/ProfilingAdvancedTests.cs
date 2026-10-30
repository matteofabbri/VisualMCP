using System.Text.Json;
using Snitch.Profiling;
using Snitch.Profiling.Analysis;
using Snitch.Profiling.Model;
using Snitch.Profiling.Runtime;
using Snitch.Profiling.Snapshots;
using Snitch.Profiling.Verdicts;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The answer layer: each service is given synthetic measurements (a trace model, a fake I/O report,
/// counter series, heap snapshots, stack snapshots) and must return a verdict with ranked evidence.
/// Nothing here needs a live process: the live overloads are exercised through fakes, so what is
/// tested is the orchestration and the decision logic, not EventPipe or ClrMD.
/// </summary>
public sealed class ProfilingAdvancedTests
{
    private static readonly ProfilingOptions Options = new();

    // -----------------------------------------------------------------------
    // Why is it slow?
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Slowdown_Names_Cpu_As_The_Dominant_Cause()
    {
        using var directory = new TempDirectory("vmcp-slow-cpu");
        var tracePath = directory.WriteFile("trace.nettrace", "x");

        var analysis = Analysis(
            durationMs: 1000,
            totalCpuMs: 800,
            cpuSamples: 800,
            cpu: [new CpuHotspot("MyApp.Work.Hot", "Work.cs:42", 700, 90, 700)]);
        var counters = Counters(Series("cpu-usage", 85, 88, 92));
        var io = Io(durationMs: 1000, totalBlockedMs: 30,
            categories: [new IoCategorySummary("Sleep", 30, 100, 1, 1)]);

        var report = await Slowdown(analysis, io, counters, Stacks(blocked: 1, total: 4))
            .AnalyzeTraceAsync(tracePath, counters, Stacks(blocked: 1, total: 4));

        Assert.Equal("cpu", report.Answer.Verdict);
        Assert.Equal("high", report.Answer.Confidence);
        Assert.Contains("MyApp.Work.Hot", report.Answer.Answer);
        Assert.Equal("slowdown-cpu", report.Answer.Evidence[0].Id);
        Assert.Contains(report.Answer.Evidence, e => e.Id == "cpu-hotspot");
        Assert.Contains(report.Causes, c => c.Id == "cpu" && c.Weight > 0.7);
        Assert.NotEmpty(report.Answer.NextSteps);
        Assert.All(report.Answer.Evidence, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Statement));
            Assert.False(string.IsNullOrWhiteSpace(e.Evidence));
            Assert.False(string.IsNullOrWhiteSpace(e.Recommendation));
        });
    }

    [Fact]
    public async Task Slowdown_Names_Io_Blocking_Over_Cpu()
    {
        using var directory = new TempDirectory("vmcp-slow-io");
        var tracePath = directory.WriteFile("trace.nettrace", "x");

        var analysis = Analysis(
            durationMs: 1000,
            totalCpuMs: 100,
            cpuSamples: 800,
            cpu: [new CpuHotspot("MyApp.Work.Warm", "Warm.cs:10", 40, 5, 100)]);
        var io = Io(durationMs: 1000, totalBlockedMs: 700,
            sites:
            [
                new IoBlockSite("Http", "MyApp.Download", "Download.cs:12", "System.Net.Http.HttpClient.Send",
                    700, 100, 2, 20, ["System.Net.Http.HttpClient.Send", "MyApp.Download"], "Use SendAsync."),
            ],
            categories: [new IoCategorySummary("Http", 700, 100, 1, 2)]);

        var report = await Slowdown(analysis, io, counters: null, stacks: null).AnalyzeTraceAsync(tracePath);

        Assert.Equal("io-wait", report.Answer.Verdict);
        Assert.Contains("Http", report.Answer.Answer);
        Assert.Equal("slowdown-io", report.Answer.Evidence[0].Id);
        Assert.Equal("Download.cs:12", report.Answer.Evidence[0].Source);
        Assert.Contains(report.Causes, c => c.Id == "cpu" && c.Weight < 0.2);
    }

    [Fact]
    public async Task Slowdown_Names_Sync_Over_Async_And_Points_At_The_Pool()
    {
        using var directory = new TempDirectory("vmcp-slow-sync");
        var tracePath = directory.WriteFile("trace.nettrace", "x");

        var analysis = Analysis(
            durationMs: 1000,
            totalCpuMs: 50,
            cpuSamples: 500,
            threadPool: new ThreadPoolSummary(8, 8, 5000, 1000, 4000, 40));
        var io = Io(durationMs: 1000, totalBlockedMs: 900,
            sites:
            [
                new IoBlockSite("SyncOverAsync", "MyApp.Handler.Run", "Handler.cs:20", "System.Threading.Tasks.Task.Wait",
                    900, 100, 1, 30, ["System.Threading.Tasks.Task.Wait", "MyApp.Handler.Run"], "Replace with await."),
            ],
            categories: [new IoCategorySummary("SyncOverAsync", 900, 100, 1, 1)]);

        var report = await Slowdown(analysis, io, counters: null, stacks: null).AnalyzeTraceAsync(tracePath);

        Assert.Equal("sync-over-async", report.Answer.Verdict);
        Assert.Equal("slowdown-sync-over-async", report.Answer.Evidence[0].Id);
        Assert.Contains("Handler.cs:20", report.Answer.Answer);
        Assert.Contains("thread-pool", report.Answer.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Handler.cs:20", report.Answer.Evidence[0].Source);
    }

    [Fact]
    public async Task Slowdown_Says_Not_Slow_When_Nothing_Dominates()
    {
        using var directory = new TempDirectory("vmcp-slow-none");
        var tracePath = directory.WriteFile("trace.nettrace", "x");

        var analysis = Analysis(durationMs: 1000, totalCpuMs: 30, cpuSamples: 400);
        var io = Io(durationMs: 1000, totalBlockedMs: 20,
            categories: [new IoCategorySummary("Sleep", 20, 100, 1, 1)]);

        var report = await Slowdown(analysis, io, counters: null, stacks: null)
            .AnalyzeTraceAsync(tracePath);

        Assert.Equal("not-slow", report.Answer.Verdict);
        Assert.Contains("No dominant cost", report.Answer.Answer);
        Assert.Equal("slowdown-none", report.Answer.Evidence[0].Id);
        Assert.Contains(report.Answer.Measurements, m => m.StartsWith("cpu:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Slowdown_On_An_Empty_Trace_Says_Nothing_Dominated()
    {
        using var directory = new TempDirectory("vmcp-slow-empty");
        var tracePath = directory.WriteFile("trace.nettrace", "x");

        var report = await Slowdown(Analysis(durationMs: 0), Io(durationMs: 0, totalBlockedMs: 0), counters: null, stacks: null)
            .AnalyzeTraceAsync(tracePath);

        Assert.Equal("not-slow", report.Answer.Verdict);
        Assert.Contains("No dominant cost", report.Answer.Answer);
        Assert.Contains(report.Answer.Warnings, w => w.Contains("no duration", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Slowdown_Live_Path_Collects_Counters_Trace_And_Stacks()
    {
        using var directory = new TempDirectory("vmcp-slow-live");
        var analysis = Analysis(
            durationMs: 2000,
            totalCpuMs: 1800,
            cpuSamples: 1800,
            cpu: [new CpuHotspot("MyApp.Work.Hot", "Work.cs:42", 1700, 94, 1700)]);
        var counters = new FakeCounters(Counters(Series("cpu-usage", 95, 96, 97)));
        var collector = new FakeTraceCollector();
        var stacks = new FakeStacks(Stacks(blocked: 0, total: 4));

        var service = new SlowdownService(
            counters, collector,
            new FakeAnalysis(_ => analysis),
            new FakeIo(Io(durationMs: 2000, totalBlockedMs: 0)),
            stacks,
            new ProfilingAdvisor(),
            Options);

        var report = await service.AnalyzeProcessAsync(
            processId: 4242, processName: "App", durationSeconds: 3, includeStacks: true,
            outputDirectory: directory.Path);

        Assert.Equal("cpu", report.Answer.Verdict);
        Assert.Equal("diagnose", collector.LastProfile);
        Assert.Equal(1, counters.Calls);
        Assert.Equal(4242, report.ProcessId);
        Assert.Equal("App", report.ProcessName);
    }

    // -----------------------------------------------------------------------
    // Why is memory growing?
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Memory_Growth_Names_The_Type_And_The_Allocation_Site()
    {
        using var directory = new TempDirectory("vmcp-mem-growth");
        const long mb = 1024 * 1024;

        var baselinePath = directory.WriteFile("baseline.json", JsonSerializer.Serialize(
            Heap(100 * mb, ("MyApp.Order", 20 * mb, 1000), ("System.String", 60 * mb, 5000))));
        var currentPath = directory.WriteFile("current.json", JsonSerializer.Serialize(
            Heap(220 * mb, ("MyApp.Order", 140 * mb, 9000), ("System.String", 60 * mb, 5000))));
        var tracePath = directory.WriteFile("alloc.nettrace", "x");

        var analysis = Analysis(allocations: [new AllocationHotspot("MyApp.Order", 45 * mb, 500, 60)],
            gc: new GcSummary(10, 4, 3, 200, 80, 10, 220 * mb, 40 * mb, 90 * mb, 400 * mb, 0, []));
        var allocationReport = new AllocationSiteReport(
            tracePath, 1, 45 * mb, 500, false,
            [new AllocationSiteHotspot("OrderService.Build", "Orders.cs:88", "MyApp.Order", 45 * mb, 500, 60, "Small")],
            [new AllocationHotspot("MyApp.Order", 45 * mb, 500, 60)],
            [], []);

        var service = Memory(analysis, allocationReport);

        var report = await service.CompareAsync(baselinePath, currentPath, tracePath);

        Assert.Equal("growth", report.Answer.Verdict);
        Assert.Equal("compare", report.Mode);
        Assert.Contains("OrderService.Build", report.Answer.Answer);
        Assert.Contains("Orders.cs:88", report.Answer.Answer);
        Assert.Equal("memory-growth-driver", report.Answer.Evidence[0].Id);
        Assert.Equal("Orders.cs:88", report.Answer.Evidence[0].Source);
        Assert.NotNull(report.Comparison);
        Assert.NotNull(report.Allocations);
        Assert.NotNull(report.Gc);
        Assert.Equal(3, report.Gc!.Gen2);
    }

    [Fact]
    public async Task Memory_Single_Snapshot_Names_The_Owner_But_Not_A_Trend()
    {
        using var directory = new TempDirectory("vmcp-mem-single");
        const long mb = 1024 * 1024;
        var snapshot = Heap(200 * mb, ("MyApp.Cache", 90 * mb, 1000), ("System.Byte[]", 80 * mb, 200));

        var service = new MemoryGrowthService(
            new FakeComparison(), new FakeHeap(snapshot), new FakeAnalysis(_ => Analysis()),
            new FakeAllocations(null), new ProfilingAdvisor());

        var report = await service.CaptureAsync(
            processId: 4242, processName: "App", baselineJsonPath: null, tracePath: null,
            outputDirectory: directory.Path);

        Assert.Equal("single-snapshot", report.Mode);
        Assert.Equal("single-snapshot", report.Answer.Verdict);
        Assert.Contains("Cache", report.Answer.Answer);
        Assert.Contains("cannot prove growth", report.Answer.Answer);
        Assert.Equal("low", report.Answer.Confidence);
        Assert.NotNull(report.CurrentPath);
        Assert.True(File.Exists(report.CurrentPath), "The captured snapshot must be written as JSON so it can be a baseline.");
    }

    [Fact]
    public async Task Memory_Is_Stable_Below_The_Growth_Floor()
    {
        using var directory = new TempDirectory("vmcp-mem-stable");
        const long mb = 1024 * 1024;

        var baselinePath = directory.WriteFile("baseline.json", JsonSerializer.Serialize(
            Heap(200 * mb, ("MyApp.Cache", 90 * mb, 1000))));
        var currentPath = directory.WriteFile("current.json", JsonSerializer.Serialize(
            Heap(205 * mb, ("MyApp.Cache", 93 * mb, 1100))));

        var service = new MemoryGrowthService(
            new ProfilingComparisonService(new FakeAnalysis(_ => Analysis()), new ProfilingAdvisor()),
            new FakeHeap(Heap(205 * mb)), new FakeAnalysis(_ => Analysis()), new FakeAllocations(null),
            new ProfilingAdvisor());

        var report = await service.CompareAsync(baselinePath, currentPath);

        Assert.Equal("stable", report.Answer.Verdict);
        Assert.Contains("not growing", report.Answer.Answer);
        Assert.Equal("memory-stable", report.Answer.Evidence[0].Id);
    }

    // -----------------------------------------------------------------------
    // Is the thread pool starved?
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Pool_Is_Starved_When_Queue_And_Blocked_Threads_Agree()
    {
        var queue = Series("threadpool-queue-length", 200, 1200, 2500, 2400);
        var workers = Series("threadpool-thread-count", 8, 8, 8, 8);
        var counters = Counters(queue, workers);
        var io = Io(durationMs: 2000, totalBlockedMs: 1200,
            sites:
            [
                new IoBlockSite("SyncOverAsync", "MyApp.Handler.Run", "Handler.cs:20", "System.Threading.Tasks.Task.Wait",
                    1200, 100, 1, 30, ["System.Threading.Tasks.Task.Wait"], "Replace with await."),
            ],
            categories: [new IoCategorySummary("SyncOverAsync", 1200, 100, 1, 1)]);

        var report = await Pool().AnalyzeAsync(counters, Stacks(blocked: 6, total: 8), io: io);

        Assert.Equal("starved", report.State);
        Assert.Contains("starved", report.Answer.Answer);
        Assert.Equal("pool-starvation", report.Answer.Evidence[0].Id);
        Assert.Equal("Handler.cs:20", report.Answer.Evidence[0].Source);
        Assert.NotEmpty(report.BlockingSites);
        Assert.Equal(6, report.BlockedThreads);
        Assert.Contains(report.Answer.Measurements, m => m.StartsWith("queue:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pool_Reports_A_Burst_When_The_Queue_Drains()
    {
        var counters = Counters(
            Series("threadpool-queue-length", 0, 1500, 400, 10),
            Series("threadpool-thread-count", 4, 16, 16, 16));

        var report = await Pool().AnalyzeAsync(counters, stacks: null);

        Assert.Equal("burst", report.State);
        Assert.Contains("burst", report.Answer.Answer);
        Assert.Equal("pool-burst", report.Answer.Evidence[0].Id);
    }

    [Fact]
    public async Task Pool_Is_Healthy_With_A_Quiet_Queue()
    {
        var counters = Counters(
            Series("threadpool-queue-length", 5, 20, 8, 5),
            Series("threadpool-thread-count", 4, 5, 5, 4));

        var report = await Pool().AnalyzeAsync(counters, Stacks(blocked: 1, total: 6));

        Assert.Equal("healthy", report.State);
        Assert.Contains("not starved", report.Answer.Answer);
        Assert.Equal("pool-healthy", report.Answer.Evidence[0].Id);
        Assert.Empty(report.BlockingSites);
    }

    [Fact]
    public async Task Pool_Is_Inconclusive_Without_A_Queue_Signal()
    {
        var report = await Pool().AnalyzeAsync(counters: null, stacks: Stacks(blocked: 0, total: 2));

        Assert.Equal("inconclusive", report.State);
        Assert.Equal("pool-inconclusive", report.Answer.Evidence[0].Id);
        Assert.NotEmpty(report.Answer.NextSteps);
    }

    // -----------------------------------------------------------------------
    // What changed since the last measurement?
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Change_Review_Flags_A_Cpu_Regression_Across_Different_Windows()
    {
        using var directory = new TempDirectory("vmcp-change-cpu");
        var baselinePath = directory.WriteFile("baseline.nettrace", "x");
        var currentPath = directory.WriteFile("current.nettrace", "x");

        var baseline = Analysis(durationMs: 5000, totalCpuMs: 500, cpuSamples: 500,
            cpu: [new CpuHotspot("MyApp.Work.Hot", "Work.cs:42", 500, 100, 500)]);
        var current = Analysis(durationMs: 10_000, totalCpuMs: 2000, cpuSamples: 1600,
            cpu: [new CpuHotspot("MyApp.Work.Hot", "Work.cs:42", 1600, 80, 1600)]);

        var comparison = new ProfilingComparisonService(
            new FakeAnalysis(path => path == baselinePath ? baseline : current),
            new ProfilingAdvisor());
        var review = new ChangeReviewService(comparison, new ProfilingAdvisor());

        var report = await review.ReviewAsync(baselinePath, currentPath);

        Assert.Equal("trace", report.Kind);
        Assert.True(report.Comparable);
        Assert.Equal("regression", report.Answer.Verdict);
        Assert.Contains("Regression", report.Answer.Answer);

        // The raw CPU totals are 500 ms and 2 000 ms, but the windows are 5 s and 10 s: the
        // normalized rate is what makes the comparison honest.
        var cpu = report.Normalized.Single(m => m.Name == "CPU");
        Assert.Equal(100, cpu.Baseline, precision: 1);
        Assert.Equal(200, cpu.Current, precision: 1);
        Assert.Equal(100, cpu.Percent, precision: 1);

        Assert.Contains(report.Answer.Evidence, e => e.Id == "cpu-regression");
        Assert.Contains(report.Answer.Evidence, e => e.Id == "cpu-total-regression");
    }

    [Fact]
    public async Task Change_Review_Refuses_To_Compare_Tiny_Windows()
    {
        using var directory = new TempDirectory("vmcp-change-tiny");
        var baselinePath = directory.WriteFile("baseline.nettrace", "x");
        var currentPath = directory.WriteFile("current.nettrace", "x");

        var baseline = Analysis(durationMs: 100, totalCpuMs: 10, cpuSamples: 10,
            cpu: [new CpuHotspot("MyApp.Work.Hot", "Work.cs:42", 10, 100, 10)]);
        var current = Analysis(durationMs: 120, totalCpuMs: 30, cpuSamples: 30,
            cpu: [new CpuHotspot("MyApp.Work.Hot", "Work.cs:42", 30, 100, 30)]);

        var review = new ChangeReviewService(
            new ProfilingComparisonService(
                new FakeAnalysis(path => path == baselinePath ? baseline : current),
                new ProfilingAdvisor()),
            new ProfilingAdvisor());

        var report = await review.ReviewAsync(baselinePath, currentPath);

        Assert.False(report.Comparable);
        Assert.Equal("inconclusive", report.Answer.Verdict);
        Assert.Contains("Not comparable", report.Answer.Answer);
        Assert.NotEmpty(report.ComparabilityNotes);
        Assert.All(report.Normalized, m => Assert.False(m.Comparable));
    }

    [Fact]
    public async Task Change_Review_Heap_Growth_Is_A_Regression()
    {
        using var directory = new TempDirectory("vmcp-change-heap");
        const long mb = 1024 * 1024;

        var baselinePath = directory.WriteFile("baseline.json", JsonSerializer.Serialize(
            Heap(100 * mb, ("MyApp.Cache", 50 * mb, 1000))));
        var currentPath = directory.WriteFile("current.json", JsonSerializer.Serialize(
            Heap(200 * mb, ("MyApp.Cache", 150 * mb, 9000))));

        var review = new ChangeReviewService(
            new ProfilingComparisonService(new FakeAnalysis(_ => Analysis()), new ProfilingAdvisor()),
            new ProfilingAdvisor());

        var report = await review.ReviewAsync(baselinePath, currentPath);

        Assert.Equal("heap", report.Kind);
        Assert.Equal("regression", report.Answer.Verdict);
        Assert.Contains("Cache", report.Answer.Answer);
        Assert.Equal("change-heap-growth", report.Answer.Evidence[0].Id);
        Assert.Contains(report.Normalized, m => m.Name == "Heap bytes" && m.Current > m.Baseline);
    }

    // -----------------------------------------------------------------------
    // Builders and fakes
    // -----------------------------------------------------------------------

    private static SlowdownService Slowdown(
        TraceAnalysis analysis, IoBlockReport io, CounterCollectionResult? counters, StackSnapshot? stacks) =>
        new(new FakeCounters(counters), new FakeTraceCollector(), new FakeAnalysis(_ => analysis),
            new FakeIo(io), new FakeStacks(stacks), new ProfilingAdvisor(), Options);

    private static MemoryGrowthService Memory(TraceAnalysis analysis, AllocationSiteReport? allocations) =>
        new(new ProfilingComparisonService(new FakeAnalysis(_ => analysis), new ProfilingAdvisor()),
            new FakeHeap(Heap(0)),
            new FakeAnalysis(_ => analysis),
            new FakeAllocations(allocations),
            new ProfilingAdvisor());

    private static ThreadPoolStarvationService Pool() =>
        new(new FakeCounters(null), new FakeStacks(null), new FakeTraceCollector(),
            new FakeAnalysis(_ => Analysis()), new FakeIo(Io(0, 0)), new ProfilingAdvisor(), Options);

    private static TraceAnalysis Analysis(
        double durationMs = 1000,
        double totalCpuMs = 0,
        long cpuSamples = 0,
        IReadOnlyList<CpuHotspot>? cpu = null,
        IReadOnlyList<AllocationHotspot>? allocations = null,
        GcSummary? gc = null,
        ContentionSummary? contention = null,
        ThreadPoolSummary? threadPool = null) =>
        new(
            new TraceInfo("trace.nettrace", 1024, durationMs, 1000, 0, cpuSamples, totalCpuMs),
            cpu ?? Array.Empty<CpuHotspot>(),
            [],
            [],
            allocations ?? Array.Empty<AllocationHotspot>(),
            gc ?? new GcSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []),
            [],
            contention ?? new ContentionSummary(0, 0, 0, []),
            threadPool ?? new ThreadPoolSummary(0, 0, 0, 0, 0, 0),
            new JitSummary(0, 0, []),
            [],
            [],
            [],
            []);

    private static IoBlockReport Io(
        double durationMs,
        double totalBlockedMs,
        IReadOnlyList<IoBlockSite>? sites = null,
        IReadOnlyList<IoCategorySummary>? categories = null) =>
        new(Path.Combine(Path.GetTempPath(), "trace.nettrace"), durationMs, totalBlockedMs, 0,
            durationMs > 0 ? totalBlockedMs / durationMs : 0,
            sites ?? Array.Empty<IoBlockSite>(), categories ?? Array.Empty<IoCategorySummary>(), [], []);

    private static CounterSeries Series(string name, params double[] points) =>
        new("System.Runtime", name, name, "", "Mean",
            points.Min(), points.Max(), points.Average(), points[^1], points.Length,
            points.Select((value, index) => new CounterPoint(index * 1000, value)).ToList());

    private static CounterCollectionResult Counters(params CounterSeries[] series) =>
        new(1, "App", 5000, 1, ["System.Runtime"], series, [], []);

    private static StackSnapshot Stacks(int blocked, int total, IReadOnlyList<MonitorLock>? monitors = null)
    {
        var threads = Enumerable.Range(1, total)
            .Select(i => new ThreadStackInfo(
                i, (uint)i, "Running", false, false, 0, null,
                i <= blocked
                    ? new[] { new StackFrameInfo(0, "System.Threading.Tasks.Task.Wait()", null) }
                    : new[] { new StackFrameInfo(0, "MyApp.Work.Run()", null) }))
            .ToList();
        return new StackSnapshot("process 1", "10.0", threads, monitors ?? Array.Empty<MonitorLock>(), 0);
    }

    private static HeapSnapshot Heap(long totalBytes, params (string Type, long Bytes, long Count)[] types) =>
        new("fake", "10.0", false, 8, totalBytes, types.Sum(t => t.Count), 0, 0, 0, 0, false,
            types.Select(t => new HeapTypeStat(
                    t.Type, t.Count, t.Bytes, totalBytes > 0 ? Math.Round(t.Bytes * 100.0 / totalBytes, 2) : 0))
                .ToList(),
            [], []);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteFile(string name, string content)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class FakeAnalysis(Func<string, TraceAnalysis> map) : ITraceAnalysisService
    {
        public Task<TraceAnalysis> AnalyzeAsync(
            string tracePath, int top, bool resolveSource, bool deep = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(map(tracePath));
    }

    private sealed class FakeIo(IoBlockReport report) : IIoBlockService
    {
        public Task<IoBlockReport> AnalyzeAsync(
            string tracePath, int top, double minPercent, bool resolveSource, string? userCodeRoot,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(report);
    }

    private sealed class FakeStacks(StackSnapshot? snapshot) : IManagedStackService
    {
        public Task<StackSnapshot> CaptureAsync(
            int processId, int maxFrames, bool includeRuntimeFrames, CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot ?? Stacks(0, 1));

        public Task<StackSnapshot> CaptureDumpAsync(
            string dumpPath, int maxFrames, bool includeRuntimeFrames, CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot ?? Stacks(0, 1));
    }

    private sealed class FakeCounters(CounterCollectionResult? result) : ICounterCollectionService
    {
        public int Calls { get; private set; }

        public IReadOnlyList<string> DefaultProviders => ["System.Runtime"];

        public Task<CounterCollectionResult> CollectAsync(
            int processId, IReadOnlyList<string>? counterFilter, int durationSeconds, int intervalSeconds,
            IReadOnlyList<string>? providerNames = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result ?? Counters());
        }
    }

    private sealed class FakeTraceCollector : ITraceCollectionService
    {
        public string? LastProfile { get; private set; }

        public Task<TraceCollectResult> CollectAsync(
            int processId, string? outputPath, string profile, string? customProviders, int durationSeconds,
            int bufferMb, bool requestStackwalk = true, CancellationToken cancellationToken = default)
        {
            LastProfile = profile;
            var path = outputPath ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fake.nettrace");
            return Task.FromResult(new TraceCollectResult(
                processId, "App", path, 1, profile, durationSeconds * 1000.0, ["Microsoft-Windows-DotNETRuntime"]));
        }

        public LiveTraceSessionInfo Start(
            int processId, string? outputPath, string profile, string? customProviders, int bufferMb,
            bool requestStackwalk = true) =>
            throw new NotSupportedException();

        public Task<TraceCollectResult> StopAsync(string sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IReadOnlyList<LiveTraceSessionInfo> ListSessions() => [];
    }

    private sealed class FakeHeap(HeapSnapshot snapshot) : IManagedHeapService
    {
        public Task<HeapSnapshot> CaptureAsync(
            int processId, int top, string? focusType, bool includeDuplicateStrings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);

        public Task<HeapSnapshot> AnalyzeDumpAsync(
            string dumpPath, int top, string? focusType, bool includeDuplicateStrings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class FakeAllocations(AllocationSiteReport? report) : IAllocationSiteService
    {
        public Task<AllocationSiteReport> AnalyzeAsync(
            string tracePath, int top, string? focusType, bool resolveSource,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(report ?? new AllocationSiteReport(tracePath, 0, 0, 0, false, [], [], [], []));
    }

    private sealed class FakeComparison : IProfilingComparisonService
    {
        public Task<ProfileComparison> CompareAsync(
            string leftPath, string rightPath, string kind, int top, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
