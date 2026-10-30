using Snitch.Profiling;
using Snitch.Profiling.Analysis;
using Snitch.Profiling.Model;
using Snitch.Profiling.Runtime;
using Snitch.Profiling.Snapshots;
using Snitch.Profiling.Symbols;
using Snitch.Profiling.Tooling;
using Xunit;
using EventLevel = System.Diagnostics.Tracing.EventLevel;

namespace VisualMCP.Tests;

/// <summary>
/// Profiling library: the tool catalogue, the EventPipe presets, the rule engine that turns
/// measurements into findings, the PDB-to-source mapping, and two end-to-end checks that a
/// counter session and a trace session really work against this very process.
/// </summary>
public sealed class ProfilingTests
{
    // A real method in this file: the source-map test resolves its metadata token through the
    // PDB produced by this build, which exercises the whole portable-PDB path.
    private static int SampleMethod(int value) => value * 2;

    private static readonly TraceInfo TraceInfo =
        new("trace.nettrace", 1024, 10_000, 5_000, 0, 600, 600);

    [Fact]
    public void Catalog_Lists_The_Seven_Tools()
    {
        var ids = DiagnosticToolCatalog.All.Select(t => t.Id).ToList();

        Assert.Equal(7, ids.Count);
        Assert.Equal(
            ["dotnet-trace", "dotnet-counters", "dotnet-gcdump", "dotnet-dump", "dotnet-stack", "dotnet-monitor", "dotnet-symbol"],
            ids);
        Assert.All(DiagnosticToolCatalog.All, tool =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.PackageId));
            Assert.False(string.IsNullOrWhiteSpace(tool.NativeCoverage));
            Assert.StartsWith("https://", tool.DocsUrl);
            Assert.NotNull(DiagnosticToolCatalog.Find(tool.Command));
        });
    }

    [Fact]
    public void Cpu_Preset_Uses_The_Sample_Profiler_And_Stackwalk()
    {
        var (providers, preset) = TraceProviderBuilder.Build("cpu", customProviders: null);

        Assert.Equal(2, providers.Count);
        Assert.Contains(providers, p => p.Name == TraceProviderBuilder.SampleProfilerProvider);
        var runtime = Assert.Single(providers, p => p.Name == TraceProviderBuilder.RuntimeProvider);
        Assert.Equal(TracePresets.CpuSamplingKeywords, runtime.Keywords);
        Assert.True(preset.RequestStackwalk);

        var gc = TracePresets.Find("gc")!;
        Assert.Equal(EventLevel.Verbose, gc.Level);
        Assert.True(gc.RequestStackwalk);
        Assert.True(TracePresets.Find("exceptions")!.RequestStackwalk);
        Assert.True(TracePresets.Find("threading")!.RequestStackwalk);

        var (gcProviders, gcPreset) = TraceProviderBuilder.Build("gc", customProviders: null);
        Assert.Single(gcProviders);
        Assert.Equal(EventLevel.Verbose, gcPreset.Level);
    }

    [Fact]
    public void Custom_Providers_Are_Parsed()
    {
        var (providers, _) = TraceProviderBuilder.Build(
            "custom", "System.Runtime:0x1:Informational,My.Counters:0xFFFFFFFF:Verbose");

        Assert.Equal(2, providers.Count);
        Assert.Equal("System.Runtime", providers[0].Name);
        Assert.Equal(1, providers[0].Keywords);
        Assert.Equal("My.Counters", providers[1].Name);
        Assert.Equal(unchecked((long)0xFFFFFFFF), providers[1].Keywords);
    }

    [Fact]
    public void Unknown_Profile_Is_Rejected()
    {
        Assert.Throws<ArgumentException>(() => TraceProviderBuilder.Build("nope", null));
        Assert.Throws<ArgumentException>(() => TraceProviderBuilder.Build("custom", null));
    }

    [Fact]
    public void Advisor_Flags_Cpu_Gc_Allocation_Exceptions_Contention_Backlog_And_Jit()
    {
        var analysis = Trace(
            cpu: [new CpuHotspot("MyApp.Work.Hot", "Work.cs:42", 300, 50, 300)],
            allocations: [new AllocationHotspot("System.Byte[]", 60 * 1024 * 1024, 500, 55)],
            gc: new GcSummary(60, 10, 8, 2_500, 400, 25, 200 * 1024 * 1024, 60 * 1024 * 1024,
                120 * 1024 * 1024, 900 * 1024 * 1024, 15_000, []),
            exceptions: [new ExceptionHotspot("System.InvalidOperationException", 150, "boom", "Boom.cs:10")],
            contention: new ContentionSummary(120, 900, 250, [new ContentionLock("Managed", 120, 900, 250)]),
            threadPool: new ThreadPoolSummary(16, 4, 5_000, 1_000, 2_000, 20),
            jit: new JitSummary(500, 900, [new JitHotspot("MyApp.Startup", 200, 4_096)]));

        var ids = new ProfilingAdvisor().Analyze(analysis).Select(f => f.Id).ToHashSet();

        Assert.Contains("cpu-hotspot", ids);
        Assert.Contains("gc-pause", ids);
        Assert.Contains("gen2-pressure", ids);
        Assert.Contains("allocation-hotspot", ids);
        Assert.Contains("exception-storm", ids);
        Assert.Contains("lock-contention", ids);
        Assert.Contains("threadpool-starvation", ids);
        Assert.Contains("jit-time", ids);

        var hotspot = new ProfilingAdvisor().Analyze(analysis).Single(f => f.Id == "cpu-hotspot");
        Assert.Equal("Work.cs:42", hotspot.Source);
        Assert.Equal("warning", hotspot.Severity);
    }

    [Fact]
    public void Advisor_Flags_Heap_Problems()
    {
        var heap = new HeapSnapshot(
            "process 1", "10.0.0", false, 8,
            200 * 1024 * 1024, 500_000, 0,
            60 * 1024 * 1024, 0, 80_000, false,
            [new HeapTypeStat("MyApp.CachedDto", 100_000, 90 * 1024 * 1024, 45)],
            [],
            [new DuplicateString("duplicated-key", 2_000, 60L * 1024 * 1024)]);

        var ids = new ProfilingAdvisor().Analyze(heap).Select(f => f.Id).ToHashSet();

        Assert.Contains("heap-type-dominance", ids);
        Assert.Contains("heap-loh", ids);
        Assert.Contains("finalizer-queue", ids);
        Assert.Contains("duplicate-strings", ids);
    }

    [Fact]
    public void Advisor_Flags_Contended_Monitor_And_Blocked_Threads()
    {
        var blocked = Enumerable.Range(1, 9)
            .Select(i => new ThreadStackInfo(
                i, (uint)i, "Blocked", false, false, 0, null,
                [new StackFrameInfo(0, "System.Threading.Monitor.Wait(System.Object, System.Int32)", null)]))
            .ToList();

        var stacks = new StackSnapshot(
            "process 1", "10.0.0", blocked,
            [new MonitorLock("MyApp.Lock", "42 (0xabc)", 4, 1, "MyApp.Holder.Run()")],
            0);

        var ids = new ProfilingAdvisor().Analyze(stacks).Select(f => f.Id).ToHashSet();

        Assert.Contains("monitor-contention", ids);
        Assert.Contains("blocked-threads", ids);
    }

    [Fact]
    public void Report_Contains_The_Sections_And_The_Findings()
    {
        var analysis = Trace(
            cpu: [new CpuHotspot("MyApp.Work.Hot", "Work.cs:42", 300, 50, 300)],
            gc: new GcSummary(10, 2, 3, 1_500, 300, 15, 100, 10, 20, 30, 0, []));
        analysis = analysis with { Findings = new ProfilingAdvisor().Analyze(analysis) };

        var markdown = ProfilingReportWriter.BuildTraceMarkdown(analysis);

        Assert.Contains("## Findings", markdown);
        Assert.Contains("## CPU hotspots", markdown);
        Assert.Contains("MyApp.Work.Hot", markdown);
        Assert.Contains("### [warning] CPU is concentrated in Hot", markdown);
    }

    [Fact]
    public void SourceMap_Resolves_A_Method_To_Its_File_And_Line()
    {
        var resolver = new SourceMapResolver(new ProfilingOptions());
        var method = typeof(ProfilingTests).GetMethod(
            nameof(SampleMethod),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var location = resolver.Resolve(typeof(ProfilingTests).Assembly.Location, method.MetadataToken);

        Assert.NotNull(location);
        Assert.Contains("ProfilingTests.cs", location);
        Assert.Matches(@":\d+$", location);
    }

    [Fact]
    public void SymbolService_Reads_The_CodeView_Record()
    {
        var service = new SymbolService(new ProfilingOptions());
        var directory = Path.Combine(Path.GetTempPath(), "vmcp-symbols-" + Guid.NewGuid().ToString("N"));

        var info = Assert.Single(service.Inspect([typeof(ProfilingTests).Assembly.Location], directory));

        Assert.Equal("VisualMCP.Tests.pdb", info.PdbName);
        Assert.NotNull(info.PdbPath);
        Assert.False(info.Embedded);
        Assert.NotEqual(Guid.Empty, info.Guid);
    }

    [Fact]
    public async Task Counters_Are_Collected_From_The_Current_Process()
    {
        var service = new CounterCollectionService(new ProfilingOptions());

        var result = await service.CollectAsync(
            Environment.ProcessId,
            ["cpu-usage", "gc-heap-size", "threadpool-thread-count"],
            durationSeconds: 3,
            intervalSeconds: 1);

        Assert.Equal(Environment.ProcessId, result.ProcessId);
        Assert.True(result.Series.Count > 0,
            "No EventCounters were parsed. Warnings: " + string.Join(" | ", result.Warnings));
        Assert.All(result.Series, series =>
        {
            Assert.True(series.Points > 0);
            Assert.InRange(series.Mean, series.Min, series.Max);
        });
    }

    [Fact]
    public async Task Trace_Collect_And_Analyze_Roundtrip()
    {
        var options = new ProfilingOptions();
        var directory = Path.Combine(Path.GetTempPath(), "vmcp-trace-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "self.nettrace");

        using var stop = new CancellationTokenSource();
        var spinner = Task.Run(() =>
        {
            long total = 0;
            while (!stop.IsCancellationRequested)
                total += BurnCpu(1_000);
            return total;
        });

        try
        {
            var collected = await new TraceCollectionService(options).CollectAsync(
                Environment.ProcessId, path, "cpu", customProviders: null,
                durationSeconds: 2, bufferMb: 128);

            stop.Cancel();
            await spinner;

            Assert.True(collected.SizeBytes > 0, "The trace file is empty.");
            Assert.Equal("cpu", collected.Profile);

            var reader = new TraceStackReader(options);
            var analysis = await new TraceAnalysisService(options, new ProfilingAdvisor(), reader)
                .AnalyzeAsync(path, top: 10, resolveSource: false);

            Assert.True(analysis.Trace.EventCount > 0, "The trace contains no events.");
            Assert.True(analysis.Trace.DurationMs > 0, "The trace has no duration.");
            Assert.Equal(0, analysis.Trace.EventsLost);
            Assert.True(analysis.Trace.CpuSamples > 0,
                "The cpu profile produced no CPU samples. Warnings: " + string.Join(" | ", analysis.Warnings));
            Assert.True(analysis.Trace.TotalCpuMs > 0);
            Assert.NotEmpty(analysis.CpuHotspots);
            Assert.NotEmpty(analysis.CpuStacks);
            Assert.NotEmpty(analysis.Timeline);

            var folded = Path.Combine(directory, "self.folded");
            var callTree = await new CallTreeService(reader).BuildAsync(
                path, top: 40, maxDepth: 30, resolveSource: false, foldedPath: folded);

            Assert.True(callTree.TotalCpuMs > 0);
            Assert.NotEmpty(callTree.TopInclusive);
            Assert.NotEmpty(callTree.TopExclusive);
            Assert.Contains(callTree.TopExclusive, n => n.Method.Contains("BurnCpu", StringComparison.Ordinal));
            Assert.DoesNotContain(callTree.TopInclusive, n =>
                n.Method.Contains("g__", StringComparison.Ordinal) || n.Method.Contains("b__", StringComparison.Ordinal));
            Assert.True(File.Exists(folded));
            Assert.True(callTree.FoldedStacks > 0);
            Assert.Contains("BurnCpu", File.ReadAllText(folded));

            var hotPaths = await new HotPathService(reader).AnalyzeAsync(
                path, "cpu", top: 10, minPercent: 1.0, resolveSource: false,
                userCodeOnly: false, userCodeRoot: null);

            Assert.NotEmpty(hotPaths.HotMethods);
            Assert.Contains(hotPaths.HotMethods, m => m.Method.Contains("BurnCpu", StringComparison.Ordinal));
            Assert.NotEmpty(hotPaths.TopPaths);

            var io = await new IoBlockService(reader).AnalyzeAsync(
                path, top: 10, minPercent: 1.0, resolveSource: false, userCodeRoot: null);
            Assert.True(io.TotalBlockedMs >= 0);
            Assert.NotEmpty(io.Findings);
        }
        finally
        {
            stop.Cancel();
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Allocation_Sites_Are_Attributed_To_The_Allocating_Method()
    {
        var options = new ProfilingOptions();
        var directory = Path.Combine(Path.GetTempPath(), "vmcp-alloc-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "alloc.nettrace");

        using var stop = new CancellationTokenSource();
        var worker = Task.Run(() =>
        {
            var sink = new List<byte[]>();
            while (!stop.IsCancellationRequested)
            {
                sink.Add(new byte[2048]);
                if (sink.Count > 100) sink.Clear();
            }
        });

        try
        {
            // The gc preset is Verbose with stackwalk: allocation ticks exist only there and only
            // carry call stacks when the session requests stack walks.
            await new TraceCollectionService(options).CollectAsync(
                Environment.ProcessId, path, "gc", customProviders: null,
                durationSeconds: 1, bufferMb: 256);

            stop.Cancel();
            await worker;

            var report = await new AllocationSiteService(new TraceStackReader(options))
                .AnalyzeAsync(path, top: 10, focusType: null, resolveSource: true);

            Assert.True(report.SampledBytes > 0, "No allocation samples were collected.");
            Assert.NotEmpty(report.TopSites);
            Assert.NotEqual("(no allocation stack)", report.TopSites[0].Site);
            Assert.Contains(report.TopSites, s => s.Source?.Contains("ProfilingTests.cs", StringComparison.OrdinalIgnoreCase) == true);
            Assert.Contains(report.TopSites, s => s.TypeName.Contains("Byte[]", StringComparison.Ordinal));
        }
        finally
        {
            stop.Cancel();
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task CallTree_Computes_Inclusive_And_Exclusive_Time()
    {
        var model = new TraceStackModel(
            "fake.nettrace", 1000, 1,
            [
                new CpuStackSample(0, 100,
                [
                    new TraceFrameInfo("Leaf", "Leaf.cs:10"),
                    new TraceFrameInfo("Middle", "Middle.cs:20"),
                    new TraceFrameInfo("Root", null),
                ]),
            ],
            [], [], [], false, []);

        var directory = Path.Combine(Path.GetTempPath(), "vmcp-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var tracePath = Path.Combine(directory, "fake.nettrace");
        File.WriteAllText(tracePath, "x");

        var report = await new CallTreeService(new FakeReader(model))
            .BuildAsync(tracePath, top: 10, maxDepth: 10, resolveSource: true,
                foldedPath: Path.Combine(directory, "fake.folded"));

        try
        {
            Assert.Equal(100d, report.TotalCpuMs);
            Assert.Equal("Root", report.Roots.Single().Method);
            Assert.Equal(100d, report.Roots.Single().InclusiveMs);

            var leaf = report.TopExclusive.Single(n => n.Method == "Leaf");
            Assert.Equal(100d, leaf.ExclusiveMs);
            Assert.Equal(100d, leaf.InclusiveMs);
            Assert.Equal("Leaf.cs:10", leaf.Source);
            Assert.Equal("Root -> Middle -> Leaf", leaf.Path);

            var middle = report.TopInclusive.Single(n => n.Method == "Middle");
            Assert.Equal(100d, middle.InclusiveMs);
            Assert.Equal(0d, middle.ExclusiveMs);
            Assert.True(File.Exists(report.FoldedPath));
            Assert.Contains("Root;Middle;Leaf 100", File.ReadAllText(report.FoldedPath!));
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Allocation_Site_Report_Aggregates_And_Flags()
    {
        var model = new TraceStackModel(
            "fake.nettrace", 1000, 0, [], [],
            [
                new AllocationSiteSample(0, "System.Byte[]", 2 * 1024 * 1024, "Small",
                    [new TraceFrameInfo("Allocator.Run", "Allocator.cs:42")]),
                new AllocationSiteSample(10, "System.Byte[]", 2 * 1024 * 1024, "Small",
                    [new TraceFrameInfo("Allocator.Run", "Allocator.cs:42")]),
            ],
            [], false, []);

        var directory = Path.Combine(Path.GetTempPath(), "vmcp-alloc-site-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var tracePath = Path.Combine(directory, "fake.nettrace");
        File.WriteAllText(tracePath, "x");

        try
        {
            var report = await new AllocationSiteService(new FakeReader(model))
                .AnalyzeAsync(tracePath, top: 10, focusType: null, resolveSource: true);

            Assert.Equal(2, report.Samples);
            Assert.Equal(4L * 1024 * 1024, report.SampledBytes);
            var site = report.TopSites.Single();
            Assert.Equal("Allocator.Run", site.Site);
            Assert.Equal("Allocator.cs:42", site.Source);
            Assert.Equal(100d, site.Share);
            Assert.Contains(report.Findings, f => f.Id == "allocation-site");
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Counter_Advisor_Flags_Saturation_Gc_Queue_And_Contention()
    {
        var counters = new CounterCollectionResult(
            1, "App", 5000, 1, ["System.Runtime"],
            [
                Series("cpu-usage", 92, 97, "%"),
                Series("time-in-gc", 28, 35, "%"),
                Series("alloc-rate", 150 * 1024 * 1024, 180 * 1024 * 1024, "B/s"),
                Series("threadpool-queue-length", 2000, 4000, ""),
                Series("monitor-lock-contention-count", 40, 80, ""),
                Series("gen-2-gc-count", 6, 9, ""),
            ],
            [], []);

        var ids = new ProfilingAdvisor().Analyze(counters).Select(f => f.Id).ToHashSet();

        Assert.Contains("cpu-saturated", ids);
        Assert.Contains("gc-time-high", ids);
        Assert.Contains("allocation-rate-high", ids);
        Assert.Contains("threadpool-queue", ids);
        Assert.Contains("lock-contention-count", ids);
        Assert.Contains("gen2-rate", ids);
    }

    [Fact]
    public async Task Comparison_Flags_Cpu_And_Allocation_Regressions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vmcp-cmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var leftPath = Path.Combine(directory, "left.nettrace");
        var rightPath = Path.Combine(directory, "right.nettrace");
        File.WriteAllText(leftPath, "x");
        File.WriteAllText(rightPath, "x");

        try
        {
            var left = Analysis(100, "MyApp.Work", 50, "System.Byte[]", 1 * 1024 * 1024);
            var right = Analysis(500, "MyApp.Work", 200, "System.Byte[]", 20 * 1024 * 1024);

            var comparison = await new ProfilingComparisonService(
                    new FakeAnalysis(leftPath, left, rightPath, right), new ProfilingAdvisor())
                .CompareAsync(leftPath, rightPath, kind: "trace", top: 10);

            Assert.Contains(comparison.Findings, f => f.Id == "cpu-total-regression");
            Assert.Contains(comparison.Findings, f => f.Id == "cpu-regression");
            Assert.Contains(comparison.Findings, f => f.Id == "allocation-regression");
            Assert.Equal(400d, comparison.Metrics.Single(m => m.Name == "CPU ms").Delta);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Heap_Comparison_Flags_Type_Growth()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vmcp-heapcmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var leftPath = Path.Combine(directory, "left.json");
            var rightPath = Path.Combine(directory, "right.json");
            File.WriteAllText(leftPath, System.Text.Json.JsonSerializer.Serialize(
                Heap(100 * 1024 * 1024, "MyApp.Cache", 50 * 1024 * 1024)));
            File.WriteAllText(rightPath, System.Text.Json.JsonSerializer.Serialize(
                Heap(200 * 1024 * 1024, "MyApp.Cache", 150 * 1024 * 1024)));

            var comparison = await new ProfilingComparisonService(
                    new FakeAnalysis("a", Trace(), "b", Trace()), new ProfilingAdvisor())
                .CompareAsync(leftPath, rightPath, kind: "auto", top: 10);

            Assert.Equal("heap", comparison.Kind);
            Assert.Contains(comparison.Findings, f => f.Id == "heap-growth");
            Assert.Contains(comparison.Findings, f => f.Id == "heap-type-growth");
            var type = comparison.Heap.Single(t => t.TypeName == "MyApp.Cache");
            Assert.Equal(100L * 1024 * 1024, type.DeltaBytes);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task HotPaths_Ranks_Multiple_Fixes_And_Branches()
    {
        var root = Path.Combine(Path.GetTempPath(), "vmcp-hot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tracePath = Path.Combine(root, "fake.nettrace");
        File.WriteAllText(tracePath, "x");

        try
        {
            List<TraceFrameInfo> Frames(params string[] methods) =>
                methods.Select(m => new TraceFrameInfo(m, Path.Combine(root, m + ".cs"))).ToList();

            var model = new TraceStackModel(
                "fake.nettrace", 1000, 3,
                [
                    new CpuStackSample(0, 60, Frames("LeafC", "B", "A")),
                    new CpuStackSample(0, 30, Frames("LeafD", "B", "A")),
                    new CpuStackSample(0, 10, Frames("LeafE", "A")),
                ],
                [], [], [], false, []);

            var report = await new HotPathService(new FakeReader(model))
                .AnalyzeAsync(tracePath, "cpu", top: 10, minPercent: 1.0,
                    resolveSource: false, userCodeOnly: true, userCodeRoot: root);

            Assert.Equal("cpu", report.Metric);
            Assert.Equal("ms", report.Unit);
            Assert.Equal(100d, report.TotalCost);
            Assert.Equal(3, report.HotMethods.Count);
            Assert.Equal("LeafC", report.HotMethods[0].Method);
            Assert.Equal(60d, report.HotMethods[0].Share);

            Assert.Contains(report.Branches, b => b.Frame == "A" && b.HotChildren == 2);
            Assert.Contains(report.Branches, b => b.Frame == "B" && b.HotChildren == 2);
            Assert.Contains(report.Findings, f => f.Id == "hot-path");

            Assert.Equal(3, report.TopPaths.Count);
            Assert.Contains(report.TopPaths, p => p.Path.SequenceEqual(["A", "B", "LeafC"]));
            Assert.Contains(report.TopPaths, p => p.Leaf == "LeafC" && p.Share == 60);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task HotPaths_Allocation_Metric_Reports_Sites_And_Bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "vmcp-hotalloc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tracePath = Path.Combine(root, "fake.nettrace");
        File.WriteAllText(tracePath, "x");

        try
        {
            List<TraceFrameInfo> Frames(params string[] methods) =>
                methods.Select(m => new TraceFrameInfo(m, Path.Combine(root, m + ".cs"))).ToList();

            var model = new TraceStackModel(
                "fake.nettrace", 1000, 0, [], [],
                [new AllocationSiteSample(0, "System.Byte[]", 4 * 1024 * 1024, "Small", Frames("Allocate", "Main"))],
                [], false, []);

            var report = await new HotPathService(new FakeReader(model))
                .AnalyzeAsync(tracePath, "allocations", top: 10, minPercent: 1.0,
                    resolveSource: false, userCodeOnly: true, userCodeRoot: root);

            Assert.Equal("bytes", report.Unit);
            Assert.Equal(4d * 1024 * 1024, report.TotalCost);
            var hotspot = report.HotMethods.Single();
            Assert.Equal("Allocate", hotspot.Method);
            Assert.Equal(100d, hotspot.Share);
            Assert.Contains(report.Findings, f => f.Id == "hot-path");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task HotPaths_Mixed_Keeps_Cpu_And_Allocation_Lists()
    {
        var root = Path.Combine(Path.GetTempPath(), "vmcp-hotmix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tracePath = Path.Combine(root, "fake.nettrace");
        File.WriteAllText(tracePath, "x");

        try
        {
            List<TraceFrameInfo> Frames(params string[] methods) =>
                methods.Select(m => new TraceFrameInfo(m, Path.Combine(root, m + ".cs"))).ToList();

            var model = new TraceStackModel(
                "fake.nettrace", 1000, 1,
                [new CpuStackSample(0, 100, Frames("Spin", "Main"))],
                [],
                [new AllocationSiteSample(0, "System.Byte[]", 1024 * 1024, "Small", Frames("Allocate", "Main"))],
                [], false, []);

            var report = await new HotPathService(new FakeReader(model))
                .AnalyzeAsync(tracePath, "mixed", top: 10, minPercent: 1.0,
                    resolveSource: false, userCodeOnly: true, userCodeRoot: root);

            Assert.Equal("mixed", report.Metric);
            Assert.Equal("share", report.Unit);
            Assert.Contains(report.HotMethods, m => m.Metric == "cpu" && m.Method == "Spin");
            Assert.Contains(report.HotMethods, m => m.Metric == "allocations" && m.Method == "Allocate");
            Assert.Contains(report.Findings, f => f.Id == "hot-path-cpu-and-alloc");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task IoBlocks_Classify_And_Rank_Blocking_Sites()
    {
        var root = Path.Combine(Path.GetTempPath(), "vmcp-io-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tracePath = Path.Combine(root, "fake.nettrace");
        File.WriteAllText(tracePath, "x");

        try
        {
            List<TraceFrameInfo> Frames(params string[] methods) =>
                methods.Select(m => new TraceFrameInfo(m,
                    m.StartsWith("System.", StringComparison.Ordinal) || m.StartsWith("Microsoft.", StringComparison.Ordinal)
                        ? null
                        : Path.Combine(root, m + ".cs"))).ToList();

            var model = new TraceStackModel(
                "fake.nettrace", 2000, 4, [],
                [
                    new BlockedTraceSample(0, 800, 1, Frames("System.Net.Http.HttpClient.Send", "MyApp.Download")),
                    new BlockedTraceSample(0, 200, 2, Frames("System.IO.FileStream.Read", "MyApp.Save")),
                ],
                [], [], false, []);

            var report = await new IoBlockService(new FakeReader(model))
                .AnalyzeAsync(tracePath, top: 10, minPercent: 1.0, resolveSource: false, userCodeRoot: root);

            Assert.Equal(1000d, report.TotalBlockedMs);
            Assert.Equal(2, report.Sites.Count);

            var top = report.Sites[0];
            Assert.Equal("Http", top.Category);
            Assert.Equal("MyApp.Download", top.CallSite);
            Assert.Contains("Download.cs", top.Source);
            Assert.Contains("HttpClient.Send", top.BlockingMethod);
            Assert.Equal(800d, top.BlockedMs);
            Assert.Equal(80d, top.Share);
            Assert.Equal(1, top.Threads);

            Assert.Contains(report.Categories, c => c.Category == "Http" && c.BlockedMs == 800);
            Assert.Contains(report.Categories, c => c.Category == "File" && c.BlockedMs == 200);
            Assert.Contains(report.Findings, f => f.Id == "io-block");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task IoBlocks_Detects_SyncOverAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "vmcp-iosync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tracePath = Path.Combine(root, "fake.nettrace");
        File.WriteAllText(tracePath, "x");

        try
        {
            List<TraceFrameInfo> Frames(params string[] methods) =>
                methods.Select(m => new TraceFrameInfo(m,
                    m.StartsWith("System.", StringComparison.Ordinal) || m.StartsWith("Microsoft.", StringComparison.Ordinal)
                        ? null
                        : Path.Combine(root, m + ".cs"))).ToList();

            var model = new TraceStackModel(
                "fake.nettrace", 2000, 2, [],
                [new BlockedTraceSample(0, 1200, 7, Frames("System.Threading.Tasks.Task.Wait", "MyApp.Handler.Run"))],
                [], [], false, []);

            var report = await new IoBlockService(new FakeReader(model))
                .AnalyzeAsync(tracePath, top: 10, minPercent: 1.0, resolveSource: false, userCodeRoot: root);

            var site = report.Sites.Single();
            Assert.Equal("SyncOverAsync", site.Category);
            Assert.Equal("MyApp.Handler.Run", site.CallSite);
            Assert.Contains("Task.Wait", site.BlockingMethod);
            Assert.Contains(report.Findings, f => f.Id == "sync-over-async");
            Assert.Contains(report.Findings, f => f.Id == "io-block");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task IoBlocks_With_No_Intervals_Reports_No_Blocking()
    {
        var root = Path.Combine(Path.GetTempPath(), "vmcp-ionone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tracePath = Path.Combine(root, "fake.nettrace");
        File.WriteAllText(tracePath, "x");

        try
        {
            var model = new TraceStackModel("fake.nettrace", 1000, 0, [], [], [], [], false, []);
            var report = await new IoBlockService(new FakeReader(model))
                .AnalyzeAsync(tracePath, top: 10, minPercent: 1.0, resolveSource: false, userCodeRoot: root);

            Assert.Equal(0d, report.TotalBlockedMs);
            Assert.Empty(report.Sites);
            Assert.Contains(report.Findings, f => f.Id == "no-io-blocking");
            Assert.NotEmpty(report.Warnings);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task LineCost_Ranks_Lines_And_Scopes_To_A_Method()
    {
        var root = Path.Combine(Path.GetTempPath(), "vmcp-lines-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tracePath = Path.Combine(root, "fake.nettrace");
        var sourcePath = Path.Combine(root, "Work.cs");
        File.WriteAllText(tracePath, "x");
        File.WriteAllLines(sourcePath, Enumerable.Range(1, 60).Select(i =>
            i == 42 ? "        total += ExpensiveCall();" : $"// line {i}"));

        try
        {
            var model = new TraceStackModel(
                "fake.nettrace", 1000, 5,
                [
                    new CpuStackSample(0, 100, [new TraceFrameInfo("MyApp.Work.Run", $"{sourcePath}:42", sourcePath, 42)]),
                    new CpuStackSample(1, 100, [new TraceFrameInfo("MyApp.Work.Run", $"{sourcePath}:42", sourcePath, 42)]),
                    new CpuStackSample(2, 100, [new TraceFrameInfo("MyApp.Work.Run", $"{sourcePath}:43", sourcePath, 43)]),
                    new CpuStackSample(3, 100, [new TraceFrameInfo("MyApp.Other.Run", $"{sourcePath}:10", sourcePath, 10)]),
                    // No PDB/IL map: the frame has a method but no line, so it cannot be attributed.
                    new CpuStackSample(4, 100, [new TraceFrameInfo("MyApp.Work.Run", $"{sourcePath}:42")]),
                ],
                [], [], [], false, []);

            var service = new LineCostService(new FakeReader(model));

            var scoped = await service.AnalyzeAsync(
                tracePath, method: "Work.Run", top: 10, minPercent: 0.0,
                resolveSource: true, includeText: true);

            Assert.Equal("method Work.Run", scoped.Scope);
            Assert.Equal(500d, scoped.TraceCpuMs);
            Assert.Equal(400d, scoped.ScopeCpuMs);   // three placed samples + the unattributed one
            Assert.Equal(4, scoped.Samples);
            Assert.Equal(3, scoped.AttributedSamples);
            Assert.Equal(2, scoped.Lines.Count);

            var hottest = scoped.Lines[0];
            Assert.Equal(42, hottest.Line);
            Assert.Equal(200d, hottest.EstimatedCpuMs);
            Assert.Equal(50d, hottest.Share);
            Assert.Contains("ExpensiveCall", hottest.Text);
            Assert.Contains("cpu-hot-line", scoped.Findings.Select(f => f.Id));
            Assert.NotEmpty(scoped.NextSteps);

            var second = scoped.Lines[1];
            Assert.Equal(43, second.Line);
            Assert.Equal(25d, second.Share);

            var whole = await service.AnalyzeAsync(
                tracePath, method: null, top: 10, minPercent: 0.0,
                resolveSource: true, includeText: false);

            Assert.Equal("trace", whole.Scope);
            Assert.Equal(500d, whole.ScopeCpuMs);
            Assert.Equal(3, whole.Lines.Count);
            Assert.Equal(40d, whole.Lines[0].Share); // 200 ms of 500
            Assert.All(whole.Lines, line => Assert.Null(line.Text));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task LineCost_Without_Symbols_Explains_Instead_Of_Guessing()
    {
        var root = Path.Combine(Path.GetTempPath(), "vmcp-nolines-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tracePath = Path.Combine(root, "fake.nettrace");
        File.WriteAllText(tracePath, "x");

        try
        {
            var model = new TraceStackModel(
                "fake.nettrace", 1000, 3,
                [
                    new CpuStackSample(0, 100, [new TraceFrameInfo("System.String.Concat", null)]),
                    new CpuStackSample(1, 100, [new TraceFrameInfo("System.String.Concat", null)]),
                ],
                [], [], [], false, []);

            var report = await new LineCostService(new FakeReader(model))
                .AnalyzeAsync(tracePath, method: null, top: 10, minPercent: 0.0,
                    resolveSource: true, includeText: true);

            Assert.Empty(report.Lines);
            Assert.Equal(2, report.Samples);
            Assert.Equal(0, report.AttributedSamples);
            Assert.Contains(report.Findings, f => f.Id == "line-no-symbols");
            Assert.Contains(report.Warnings, w => w.Contains("PDB", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(report.NextSteps, s => s.Contains("profile_symbols", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The empirical gate for line-level attribution: a real trace of this process, while BurnCpu
    /// spins on a loop whose body is on its own line. If the runtime's IL→native map (JIT keyword)
    /// or the PDB sequence points were missing, every sample would land on the method's first line
    /// and this test would see a single line.
    /// </summary>
    [Fact]
    public async Task Line_Level_Cpu_Is_Attributed_Inside_A_Real_Method()
    {
        var options = new ProfilingOptions();
        var directory = Path.Combine(Path.GetTempPath(), "vmcp-hotlines-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "lines.nettrace");

        using var stop = new CancellationTokenSource();
        var spinner = Task.Run(() =>
        {
            long total = 0;
            while (!stop.IsCancellationRequested)
                total += BurnCpu(1_000);
            return total;
        });

        try
        {
            await new TraceCollectionService(options).CollectAsync(
                Environment.ProcessId, path, "cpu", customProviders: null,
                durationSeconds: 2, bufferMb: 128);

            stop.Cancel();
            await spinner;

            var reader = new TraceStackReader(options);
            var report = await new LineCostService(reader)
                .AnalyzeAsync(path, method: "BurnCpu", top: 10, minPercent: 0.0,
                    resolveSource: true, includeText: true);

            Assert.True(report.Samples > 0,
                "No CPU samples matched BurnCpu. Warnings: " + string.Join(" | ", report.Warnings));
            Assert.True(report.AttributedSamples > 0,
                "No sample resolved to a source line. The trace has no IL→native map or the PDB is missing. " +
                "Warnings: " + string.Join(" | ", report.Warnings));
            Assert.True(report.Lines.Count >= 2,
                $"Expected several lines inside BurnCpu, got {report.Lines.Count}: " +
                string.Join(", ", report.Lines.Select(l => $"{l.Line} ({l.Share:0.0}%)")));

            Assert.All(report.Lines, line =>
            {
                Assert.EndsWith("ProfilingTests.cs", line.File);
                Assert.Contains("BurnCpu", line.Method);
            });

            // The signature line is not where the CPU goes: the loop body must be the hot line.
            Assert.NotNull(report.Lines[0].Text);
            Assert.DoesNotContain("BurnCpu", report.Lines[0].Text);

            var analysis = await new TraceAnalysisService(options, new ProfilingAdvisor(), reader)
                .AnalyzeAsync(path, top: 10, resolveSource: true);

            Assert.NotEmpty(analysis.HotLines);
            Assert.Contains(analysis.HotLines, line => line.Method.Contains("BurnCpu", StringComparison.Ordinal));
        }
        finally
        {
            stop.Cancel();
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public void Retention_Finds_The_Shortest_Root_Chain()
    {
        var graph = RetentionGraph();

        var walk = RetentionWalker.Walk(
            graph, "LeakProbe", maxPaths: 3, maxNodes: 1_000, includeReferrers: true,
            budget: TimeSpan.FromSeconds(5));

        Assert.Equal(2, walk.Instances.Count);
        Assert.Equal(2, walk.Paths.Count);
        Assert.True(walk.PathSearchComplete);

        var path = walk.Paths[0]; // shortest first
        Assert.Equal("static MyApp.Cache.Root", path.Root);
        Assert.Equal(["MyApp.Cache", "System.Collections.Generic.Dictionary`2", "MyApp.LeakProbe"],
            path.Frames.Select(f => f.TypeName));
        Assert.Null(path.Frames[0].Field);
        Assert.Equal("MyApp.Cache._entries", path.Frames[1].Field);
        Assert.Equal("[element]", path.Frames[2].Field);

        var rendered = RetentionWalker.FormatPath(path);
        Assert.Contains("static MyApp.Cache.Root", rendered);
        Assert.Contains("_entries", rendered);
        Assert.Contains("[element]", rendered);

        Assert.Equal(2, walk.Referrers.Count);
        Assert.Contains(walk.Referrers, r => r.TypeName == "System.Collections.Generic.Dictionary`2" && r.References == 1);
        Assert.Contains(walk.Referrers, r => r.TypeName == "MyApp.LeakProbe" && r.References == 1);
        Assert.Contains(walk.Referrers, r => r.ExamplePath is not null && r.ExamplePath.Contains("_entries"));
    }

    [Fact]
    public void Retention_Stops_At_The_Node_Budget_And_Says_So()
    {
        var graph = RetentionGraph();

        var walk = RetentionWalker.Walk(
            graph, "LeakProbe", maxPaths: 3, maxNodes: 2, includeReferrers: false,
            budget: TimeSpan.FromSeconds(5));

        Assert.Equal(2, walk.Instances.Count);
        Assert.Empty(walk.Paths);
        Assert.False(walk.PathSearchComplete);
        Assert.Empty(walk.Referrers);
    }

    [Fact]
    public void Retention_With_No_Instances_Is_Explicit()
    {
        var walk = RetentionWalker.Walk(
            RetentionGraph(), "NoSuchType", maxPaths: 3, maxNodes: 1_000, includeReferrers: true,
            budget: TimeSpan.FromSeconds(5));

        Assert.Empty(walk.Instances);
        Assert.Empty(walk.Paths);
        Assert.True(walk.PathSearchComplete);
    }

    [Fact]
    public void Advisor_Explains_A_Static_Retention_And_The_Referrer()
    {
        var path = new RetentionPath(
            "MyApp.LeakProbe", 50, "static MyApp.Cache.Root",
            [
                new RetentionFrame("MyApp.Cache", null, 100),
                new RetentionFrame("MyApp.LeakProbe", "MyApp.Cache._entries", 50),
            ]);

        var report = new RetentionReport(
            "process 1", "MyApp.LeakProbe", false, 10, 500, 1_000, 10_000,
            [new ReferrerGroup("MyApp.Cache", 10, 1, "static MyApp.Cache.Root → MyApp.Cache → MyApp.LeakProbe [_entries]")],
            [path], 100, false, true, [], [], []);

        var findings = new ProfilingAdvisor().Analyze(report);

        var retention = findings.Single(f => f.Id == "retention-path");
        Assert.Contains("static MyApp.Cache.Root", retention.Evidence);
        Assert.Contains("static field", retention.Recommendation);
        Assert.Contains("MyApp.Cache.Root", retention.SuggestedAction);
        Assert.Contains(findings, f => f.Id == "retention-referrer");
    }

    /// <summary>
    /// End-to-end wiring check for ClrMD: dump this process, load the dump and find the static list
    /// that holds the probes. Nothing here is faked — roots, field names and the chain all come from
    /// the DAC.
    /// </summary>
    [Fact]
    public async Task Retention_Finds_A_Static_Root_In_A_Dump_Of_This_Process()
    {
        // Writing a dump of the test host can be refused with 0x80004005 while the whole suite is
        // running in parallel (the runtime is alive, the write is not): retry after it settles. The
        // third failure is still a failure, so the coverage does not weaken.
        static async Task<T> RetryAsync<T>(Func<Task<T>> action)
        {
            Exception? last = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try { return await action(); }
                catch (Exception ex) when (attempt < 3)
                {
                    last = ex;
                    await Task.Delay(1500);
                }
            }

            throw last!;
        }

        var options = new ProfilingOptions();
        var directory = Path.Combine(Path.GetTempPath(), "vmcp-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dumpPath = Path.Combine(directory, "self.dmp");

        SeedRetentionProbes(3);

        try
        {
            var dumps = new DumpService(options, new ManagedHeapService(), new ManagedStackService(options));
            var dump = await RetryAsync(() => dumps.CollectAsync(Environment.ProcessId, dumpPath, "withheap"));
            Assert.True(dump.SizeBytes > 0, "The dump is empty.");

            var report = await new RetentionService(new ProfilingAdvisor())
                .AnalyzeAsync(
                    processId: null, dumpPath, focusType: nameof(RetentionProbe),
                    maxPaths: 3, maxNodes: 100_000, includeReferrers: false);

            Assert.True(report.InstanceCount > 0,
                "No RetentionProbe instances were found in the dump. Warnings: " + string.Join(" | ", report.Warnings));

            // The probes only live in a static list, so the chain must start at a static root and
            // pass through the list and its backing array. The exact static FIELD cannot be named
            // with ClrMD 4.2 (GetAddress returns 0 on a CoreCLR dump), which is why the root is
            // labelled by kind and the first frame carries the list's type.
            Assert.Contains(report.Paths, p =>
                p.Root.StartsWith("static", StringComparison.Ordinal) &&
                p.Frames.Any(f => f.TypeName.Contains("List", StringComparison.Ordinal)) &&
                p.Frames.Count >= 3);
            Assert.Contains(report.Findings, f => f.Id == "retention-path");
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// A tiny heap: one static root reaches the cache, the cache's dictionary entry array holds the
    /// first probe, and the probe holds the second through its own field.
    /// </summary>
    private static FakeHeapGraph RetentionGraph()
    {
        var graph = new FakeHeapGraph();
        graph.Root(1, "static MyApp.Cache.Root");
        graph.Add(1, "MyApp.Cache", 100, (2, "MyApp.Cache._entries"));
        graph.Add(2, "System.Collections.Generic.Dictionary`2", 80, (3, "[element]"));
        graph.Add(3, "MyApp.LeakProbe", 50, (4, "MyApp.LeakProbe.Next"));
        graph.Add(4, "MyApp.LeakProbe", 50);
        graph.Add(5, "MyApp.Other", 40);
        return graph;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long BurnCpu(int iterations)
    {
        long total = 0;
        for (var i = 0; i < iterations; i++)
            total += (i * 31L) ^ (total >> 3);
        return total;
    }

    private static CounterSeries Series(string name, double mean, double max, string unit) =>
        new("System.Runtime", name, name, unit, "Mean", 0, max, mean, mean, 2,
            [new CounterPoint(0, mean), new CounterPoint(1000, max)]);

    private static TraceAnalysis Analysis(
        double totalCpuMs, string method, long methodMs, string allocType, long allocBytes)
    {
        var info = new TraceInfo("x.nettrace", 1, 5000, 100, 0, 10, totalCpuMs);
        return new TraceAnalysis(
            info,
            [new CpuHotspot(method, "Work.cs:10", 10, 50, methodMs)],
            [],
            [],
            [new AllocationHotspot(allocType, allocBytes, 10, 50)],
            new GcSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, allocBytes, 0, []),
            [],
            new ContentionSummary(0, 0, 0, []),
            new ThreadPoolSummary(0, 0, 0, 0, 0, 0),
            new JitSummary(0, 0, []),
            [],
            [],
            [],
            []);
    }

    private static HeapSnapshot Heap(long totalBytes, string typeName, long typeBytes) =>
        new("fake", "10.0", false, 8, totalBytes, 1000, 0, 0, 0, 0, false,
            [new HeapTypeStat(typeName, 100, typeBytes, 50)], [], []);

    // ---- retention graph --------------------------------------------------

    private static readonly List<RetentionProbe> RetentionProbes = [];

    private static void SeedRetentionProbes(int count)
    {
        RetentionProbes.Clear();
        for (var i = 0; i < count; i++)
            RetentionProbes.Add(new RetentionProbe());
    }

    private sealed class RetentionProbe
    {
        public RetentionProbe? Next { get; set; }
    }

    /// <summary>An in-memory heap for the retention walk: same interface ClrMD implements.</summary>
    private sealed class FakeHeapGraph : IHeapGraph
    {
        private readonly Dictionary<ulong, HeapNode> _nodes = new();
        private readonly Dictionary<ulong, List<HeapEdge>> _edges = new();
        private readonly List<HeapRoot> _roots = [];

        public bool CanWalkHeap => true;

        public IReadOnlyList<HeapRoot> Roots => _roots;

        public IEnumerable<HeapNode> Objects => _nodes.Values;

        public HeapNode? Get(ulong address) => _nodes.TryGetValue(address, out var node) ? node : null;

        public IEnumerable<HeapEdge> References(ulong address) =>
            _edges.TryGetValue(address, out var edges) ? edges : [];

        public void Root(ulong address, string label) => _roots.Add(new HeapRoot(address, label));

        public void Add(ulong address, string typeName, long bytes, params (ulong To, string? Field)[] edges)
        {
            _nodes[address] = new HeapNode(address, typeName, bytes);
            _edges[address] = edges.Select(e => new HeapEdge(e.To, e.Field)).ToList();
        }
    }

    private sealed class FakeReader(TraceStackModel model) : ITraceStackReader
    {
        public TraceStackModel Read(string tracePath, bool resolveSource = true) => model;
    }

    private sealed class FakeAnalysis(string leftPath, TraceAnalysis left, string rightPath, TraceAnalysis right)
        : ITraceAnalysisService
    {
        public Task<TraceAnalysis> AnalyzeAsync(
            string tracePath, int top, bool resolveSource, bool deep = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(tracePath == leftPath ? left : right);
    }

    private static TraceAnalysis Trace(
        IReadOnlyList<CpuHotspot>? cpu = null,
        IReadOnlyList<AllocationHotspot>? allocations = null,
        GcSummary? gc = null,
        IReadOnlyList<ExceptionHotspot>? exceptions = null,
        ContentionSummary? contention = null,
        ThreadPoolSummary? threadPool = null,
        JitSummary? jit = null) =>
        new(
            TraceInfo,
            cpu ?? [],
            [],
            [],
            allocations ?? [],
            gc ?? new GcSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []),
            exceptions ?? [],
            contention ?? new ContentionSummary(0, 0, 0, []),
            threadPool ?? new ThreadPoolSummary(0, 0, 0, 0, 0, 0),
            jit ?? new JitSummary(0, 0, []),
            [],
            [],
            [],
            []);
}
