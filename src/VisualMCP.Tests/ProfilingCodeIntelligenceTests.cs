using System.Reflection;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Snitch.Profiling.Analysis;
using Snitch.Profiling.Instrumentation;
using Snitch.Profiling.Model;
using VisualMCP.Profiling;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The code-intelligence layer of the profiler: the metacode join (profile_root_cause), the event
/// readers (events/async/database) and the instrumented profile. The trace side is synthetic — a
/// TraceStackModel or a list of TraceEventRecord — so what is tested is the analysis, not EventPipe;
/// the instrumenter is tested end to end by rewriting a compiled fixture and running it.
/// </summary>
public sealed class ProfilingCodeIntelligenceTests
{
    // -----------------------------------------------------------------------
    // profile_root_cause: a measured site joined with the metacode AST
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RootCause_Names_The_Nested_Loops_And_The_Allocation_Inside_Them()
    {
        using var directory = new TempDirectory("vmcp-root-cause");
        var tracePath = directory.WriteFile("trace.nettrace", "x");
        var file = directory.WriteSource("Work.cs", "// placeholder");

        var declaration = new CodeDeclarationInfo(
            "Run", "method", file, 1, 30, Cyclomatic: 6, Cognitive: 9, MaxNesting: 2, Statements: 8,
            Loops:
            [
                new CodeLoopInfo("for", 2, 20, 1, "for (var i = 0; i < n; i++)"),
                new CodeLoopInfo("foreach", 4, 18, 2, "foreach (var order in orders)"),
            ],
            Constructs:
            [
                new CodeConstructInfo("loop", "for (var i = 0; i < n; i++)", 2, 20, 1, "for (var i = 0; i < n; i++)"),
                new CodeConstructInfo("loop", "foreach over order", 4, 18, 2, "foreach (var order in orders)"),
                new CodeConstructInfo("allocation", "new List<Order>", 6, 6, 2, "foreach (var order in orders)"),
                new CodeConstructInfo("linq", "ToList() materialises order.Items", 7, 7, 2, "foreach (var order in orders)"),
            ]);

        var model = new TraceStackModel(
            tracePath, 1000, 1,
            [new CpuStackSample(0, 100, [Frame("MyApp.Work.Run", file, 6)])],
            [],
            [new AllocationSiteSample(0, "System.Collections.Generic.List<Order>", 4 * 1024 * 1024, "Small",
                [Frame("MyApp.Work.Run", file, 6)])],
            [],
            false, []);

        var report = await new RootCauseService(
                new FakeReader(model),
                new FakeStructure(new CodeFileStructure(file, [declaration])))
            .AnalyzeAsync(tracePath, top: 10, minShare: 0, userCodeRoot: null, resolveSource: true);

        Assert.True(report.StructureAvailable);
        Assert.Equal(2, report.Sites.Count);

        var cpu = report.Sites.Single(site => site.Metric == "cpu");
        Assert.Equal("MyApp.Work.Run", cpu.Method);
        Assert.Equal(6, cpu.Line);
        Assert.NotNull(cpu.Declaration);
        Assert.Contains(cpu.Constructs, construct => construct.Kind == "loop" && construct.LoopDepth == 2);
        Assert.Contains("2 nested loop", cpu.Explanation);
        Assert.Contains("new List<Order>", cpu.Explanation);
        Assert.Contains("ToList() materialises", cpu.Explanation);

        var allocation = report.Sites.Single(site => site.Metric == "allocations");
        Assert.Equal("System.Collections.Generic.List<Order>", allocation.Subject);
        Assert.Equal(4 * 1024 * 1024, allocation.Cost);

        Assert.Contains(report.Findings, finding => finding.Id == "root-cause-allocation-in-loop");
        Assert.Contains(report.Findings, finding => finding.Id == "root-cause-nested-loop-work");
        Assert.Contains(report.Findings, finding => finding.Id == "root-cause-linq-in-loop");
        Assert.All(report.Sites, site => Assert.False(string.IsNullOrWhiteSpace(site.Explanation)));
    }

    [Fact]
    public async Task RootCause_Reports_The_Measurement_When_No_Ast_Is_Available()
    {
        using var directory = new TempDirectory("vmcp-root-cause-no-ast");
        var tracePath = directory.WriteFile("trace.nettrace", "x");
        var file = directory.WriteSource("Work.cs", "// placeholder");

        var model = new TraceStackModel(
            tracePath, 1000, 1,
            [new CpuStackSample(0, 100, [Frame("MyApp.Work.Run", file, 12)])],
            [], [], [], false, []);

        var report = await new RootCauseService(
                new FakeReader(model),
                new FakeStructure(new CodeFileStructure(file, [], "no model")))
            .AnalyzeAsync(tracePath, top: 10, minShare: 0, userCodeRoot: null, resolveSource: true);

        Assert.False(report.StructureAvailable);
        Assert.Single(report.Sites);
        Assert.Null(report.Sites[0].Declaration);
        Assert.Contains(report.Findings, finding => finding.Id == "root-cause-no-code-model");
        Assert.Contains(report.Warnings, warning => warning.Contains("metacode AST", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RootCause_Flags_A_Synchronous_Wait_On_A_Blocked_Site()
    {
        using var directory = new TempDirectory("vmcp-root-cause-sync");
        var tracePath = directory.WriteFile("trace.nettrace", "x");
        var file = directory.WriteSource("Work.cs", "// placeholder");

        var declaration = new CodeDeclarationInfo(
            "Run", "method", file, 1, 10, 2, 2, 1, 3,
            [new CodeLoopInfo("foreach", 2, 8, 1, "foreach (var id in ids)")],
            [
                new CodeConstructInfo("loop", "foreach over id", 2, 8, 1, "foreach (var id in ids)"),
                new CodeConstructInfo("sync-over-async", "client.GetAsync(id).Result", 4, 4, 1, "foreach (var id in ids)"),
            ]);

        var model = new TraceStackModel(
            tracePath, 1000, 0, [],
            [new BlockedTraceSample(0, 600, 1, [Frame("MyApp.Work.Run", file, 4)])],
            [], [], false, []);

        var report = await new RootCauseService(
                new FakeReader(model),
                new FakeStructure(new CodeFileStructure(file, [declaration])))
            .AnalyzeAsync(tracePath, top: 10, minShare: 0, userCodeRoot: null, resolveSource: true);

        var blocked = Assert.Single(report.Sites);
        Assert.Equal("blocked", blocked.Metric);
        Assert.Contains(blocked.Constructs, construct => construct.Kind == "sync-over-async");
        Assert.Contains(report.Findings, finding => finding.Id == "root-cause-sync-over-async");
        Assert.Contains("blocked", blocked.Explanation);
    }

    // -----------------------------------------------------------------------
    // The metacode adapter: the AST really produces those constructs
    // -----------------------------------------------------------------------

    [Fact]
    public void MetacodeStructure_Describes_Loops_Linq_Allocations_And_Sync_Waits()
    {
        using var directory = new TempDirectory("vmcp-metacode-structure");
        var file = directory.WriteSource("Work.cs", """
            using System.Collections.Generic;
            using System.Linq;
            using System.Threading.Tasks;

            namespace Sample;

            public sealed class Work
            {
                private readonly List<Order> _orders = new();

                public void Run(List<Order> orders, Task<int> pending)
                {
                    for (var i = 0; i < orders.Count; i++)
                    {
                        foreach (var order in orders)
                        {
                            var copy = new List<Order>();
                            copy.Add(order);
                            var names = orders.Where(o => o.Id > 0).ToList();
                            var total = pending.Result;
                            _orders.Add(new Order(names.Count + total));
                        }
                    }
                }
            }

            public sealed record Order(int Id);
            """);

        var structure = new MetacodeCodeStructureIndex().Describe(file);

        Assert.Null(structure.Error);
        var method = Assert.Single(structure.Declarations, declaration => declaration.Name == "Run");

        // Two loops, the inner at depth 2.
        Assert.Contains(method.Loops, loop => loop.Depth == 1);
        Assert.Contains(method.Loops, loop =>
            loop.Depth == 2 && loop.Header?.Contains("foreach", StringComparison.Ordinal) == true);

        // The LINQ materialiser, the object creations, and the .Result wait.
        Assert.Contains(method.Constructs, construct =>
            construct.Kind == "linq" && construct.Detail.Contains("materialises", StringComparison.Ordinal));
        Assert.Contains(method.Constructs, construct =>
            construct.Kind == "allocation" && construct.Detail.StartsWith("new List<Order>", StringComparison.Ordinal));
        Assert.Contains(method.Constructs, construct => construct.Kind == "sync-over-async");

        // Constructs inside the loops know it: that is what turns a line into "once per element".
        Assert.Contains(method.Constructs, construct =>
            construct.Kind == "allocation" && construct.LoopDepth == 2);
        Assert.True(method.Cyclomatic >= 3);
        Assert.True(method.MaxNesting >= 1);
    }

    // -----------------------------------------------------------------------
    // profile_async
    // -----------------------------------------------------------------------

    [Fact]
    public void Async_Pairs_Each_Await_And_Reports_Incomplete_State_Machines()
    {
        const string tpl = "System.Threading.Tasks.TplEventSource";

        var set = Set(
            Event(tpl, "TaskScheduled/Send", 10, 1, ("TaskID", "7"), ("CreatingTaskID", "1")),
            Event(tpl, "TaskExecute/Start", 12, 2, ("TaskID", "7")),
            Event(tpl, "TaskWait/Send", 20, 3, ("OriginatingTaskID", "7"), ("TaskID", "9"), ("Behavior", "Wait")),
            Event(tpl, "TaskWait/Stop", 770, 3, ("OriginatingTaskID", "7"), ("TaskID", "9")),
            Event(tpl, "TaskExecute/Stop", 900, 2, ("TaskID", "7"), ("IsExceptional", "True")),
            Event(tpl, "IncompleteAsyncMethod", 950, 4, ("stateMachineDescription", "MyApp.Handler+<RunAsync>d__4")));

        var report = AsyncFlowService.Analyze(set);

        Assert.Equal(1, report.ScheduledTasks);
        Assert.Equal(1, report.StartedTasks);
        Assert.Equal(1, report.CompletedTasks);
        Assert.Equal(1, report.ExceptionalTasks);
        Assert.Equal(1, report.AwaitCount);
        Assert.Equal(750, report.LongestAwaitMs);
        Assert.Single(report.IncompleteAsyncMethods);
        Assert.Contains("RunAsync", report.IncompleteAsyncMethods[0].Description);
        Assert.Contains(report.Findings, finding => finding.Id == "async-long-await");
        Assert.Contains(report.Findings, finding => finding.Id == "async-incomplete-method");
        Assert.Contains(report.Findings, finding => finding.Id == "async-exceptional-tasks");
    }

    [Fact]
    public void Async_Without_Task_Events_Says_How_To_Collect()
    {
        var report = AsyncFlowService.Analyze(Set(Event("Microsoft-Windows-DotNETRuntime", "GCStart", 1, 1)));

        Assert.Equal(0, report.AwaitCount);
        var finding = Assert.Single(report.Findings);
        Assert.Equal("async-no-events", finding.Id);
        Assert.Contains("profile_collect profile=async", finding.SuggestedAction);
    }

    // -----------------------------------------------------------------------
    // profile_database
    // -----------------------------------------------------------------------

    [Fact]
    public void Database_Pairs_Start_And_Stop_And_Detects_The_N_Plus_One_Shape()
    {
        const string ef = "Microsoft.EntityFrameworkCore";
        var events = new List<TraceEventRecord>();
        var time = 0.0;

        for (var i = 0; i < 6; i++)
        {
            events.Add(Event(ef, "CommandExecuting", time, 1,
                ("CommandId", $"c{i}"), ("ConnectionId", "conn"), ("CommandText", $"SELECT * FROM Orders WHERE Id = {i}")));
            events.Add(Event(ef, "CommandExecuted", time + 40, 1,
                ("CommandId", $"c{i}"), ("Duration", "00:00:00.040")));
            time += 100;
        }

        events.Add(Event(ef, "ConnectionOpened", 0, 1, ("ConnectionId", "conn")));

        var report = DatabaseTraceService.Analyze(Set(events.ToArray()));

        Assert.True(report.Captured);
        Assert.Equal(6, report.CommandCount);
        Assert.Equal(240, report.TotalCommandMs, 1);
        Assert.Single(report.TopShapes);
        Assert.Equal(6, report.TopShapes[0].Count);
        Assert.Equal(1, report.ConnectionsOpened);
        Assert.Contains(report.Findings, finding => finding.Id == "db-n-plus-one");
    }

    [Fact]
    public void Database_Without_Events_Names_The_Providers_The_Trace_Had()
    {
        var report = DatabaseTraceService.Analyze(Set(
            Event("Microsoft-Windows-DotNETRuntime", "GCStart", 1, 1),
            Event("System.Threading.Tasks.TplEventSource", "TaskScheduled/Send", 2, 1)));

        Assert.False(report.Captured);
        Assert.Equal(0, report.CommandCount);
        Assert.Contains("Microsoft-Windows-DotNETRuntime", report.ProvidersSeen);
        Assert.Contains(report.Findings, finding => finding.Id == "db-no-commands");
    }

    // -----------------------------------------------------------------------
    // profile_events
    // -----------------------------------------------------------------------

    [Fact]
    public void Events_Groups_By_Provider_And_Event_And_Samples_The_Payload()
    {
        var set = Set(
            Event("A", "One", 5, 1, ("Value", "1")),
            Event("A", "One", 15, 1, ("Value", "2")),
            Event("B", "Two", 20, 2, ("Size", "99")));

        var report = EventInventoryService.Analyze(set, provider: null, eventName: null, top: 10);

        Assert.Equal(3, report.Events);
        Assert.Equal(2, report.Providers);
        Assert.Equal(2, report.EventTypes);

        var one = report.Rows.Single(row => row.Name == "One");
        Assert.Equal(2, one.Count);
        Assert.Equal("1", one.Sample["Value"]);
        Assert.Contains("Value", one.PayloadFields);

        var filtered = EventInventoryService.Analyze(set, provider: "B", eventName: null, top: 10);
        var only = Assert.Single(filtered.Rows);
        Assert.Equal("Two", only.Name);
    }

    [Fact]
    public async Task Events_Reader_Reads_A_Real_Trace_With_Payload_Names()
    {
        var options = new Snitch.Profiling.ProfilingOptions();
        var path = Path.Combine(Path.GetTempPath(), $"vmcp-events-{Guid.NewGuid():N}.nettrace");

        try
        {
            var collected = await new Snitch.Profiling.Runtime.TraceCollectionService(options)
                .CollectAsync(Environment.ProcessId, path, "gc", customProviders: null,
                    durationSeconds: 1, bufferMb: 64, requestStackwalk: true);

            Assert.True(File.Exists(collected.Path));

            var set = new EventPipeTraceReader().Read(collected.Path);
            var report = EventInventoryService.Analyze(set);

            Assert.True(report.Events > 0, "The trace holds no event at all.");
            Assert.Contains(report.Rows, row => row.Provider.Contains("DotNETRuntime", StringComparison.OrdinalIgnoreCase));

            // The payload names are the point: EventPipe metadata must give the reader the provider's
            // own fields without a provider-specific parser.
            Assert.Contains(report.Rows, row => row.PayloadFields.Count > 0);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        }
    }

    // -----------------------------------------------------------------------
    // profile_instrumentation (analysis)
    // -----------------------------------------------------------------------

    [Fact]
    public void Instrumentation_Computes_Exact_Inclusive_And_Exclusive_Times()
    {
        using var directory = new TempDirectory("vmcp-instrument-analyze");
        var mapPath = Path.Combine(directory.Path, "sample.map.json");
        new InstrumentationMapFile("Snitch-Profiling-Probe", "sample.dll", "sample.instrumented.dll",
        [
            new InstrumentationMethodMap(1, "Sample.Calc.Run", 0),
            new InstrumentationMethodMap(2, "Sample.Calc.Inner", 0),
        ]).Save(mapPath);

        var set = Set(
            Event("Snitch-Profiling-Probe", "Enter", 0, 7, ("methodId", "1")),
            Event("Snitch-Profiling-Probe", "Enter", 2, 7, ("methodId", "2")),
            Event("Snitch-Profiling-Probe", "Exit", 5, 7, ("methodId", "2")),
            Event("Snitch-Profiling-Probe", "Exit", 10, 7, ("methodId", "1")));

        var report = InstrumentationAnalysisService.Analyze(set, mapPath);

        Assert.True(report.Captured);
        Assert.Equal(2, report.Calls);
        Assert.Equal(2, report.Methods);
        Assert.Equal(1, report.Threads);

        var outer = report.MethodsByCost.Single(method => method.Method == "Sample.Calc.Run");
        Assert.Equal(1, outer.Calls);
        Assert.Equal(10, outer.InclusiveMs);
        Assert.Equal(7, outer.ExclusiveMs);
        Assert.Equal(100, outer.Share);

        var inner = report.MethodsByCost.Single(method => method.Method == "Sample.Calc.Inner");
        Assert.Equal(3, inner.InclusiveMs);
        Assert.Equal(2, inner.MaxDepth);
    }

    // -----------------------------------------------------------------------
    // profile_instrumentation (the IL rewrite, end to end)
    // -----------------------------------------------------------------------

    [Fact]
    public void Instrumenter_Rewrites_A_Real_Assembly_And_It_Still_Runs()
    {
        using var directory = new TempDirectory("vmcp-instrument");
        var assemblyPath = directory.CompileAssembly("Sample", """
            namespace Sample;

            public static class Calc
            {
                public static int Sum(int n)
                {
                    var total = 0;
                    for (var i = 1; i <= n; i++)
                    {
                        total = Add(total, i);
                    }

                    return total;
                }

                private static int Add(int a, int b) => a + b;

                public static void Boom() => throw new System.InvalidOperationException("boom");
            }
            """);

        var result = new AssemblyInstrumenter().Instrument(assemblyPath, null, ["Sample.*"], null);

        Assert.True(result.Methods >= 3, $"instrumented {result.Methods} methods");
        Assert.True(File.Exists(result.OutputPath));
        Assert.True(File.Exists(result.MapPath));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(result.OutputPath)!, "Snitch.Profiling.Probe.dll")));

        var map = InstrumentationMapFile.Load(result.MapPath);
        Assert.NotNull(map);
        Assert.Contains(map!.Methods, method => method.Name == "Sample.Calc.Sum");

        // Load the rewritten assembly and run it: this JITs the injected IL, so an invalid rewrite
        // throws here instead of passing a metadata-only check.
        var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(result.OutputPath);
        var calc = assembly.GetType("Sample.Calc", throwOnError: true)!;

        Assert.Equal(55, (int)calc.GetMethod("Sum")!.Invoke(null, [10])!);

        var boom = calc.GetMethod("Boom")!;
        var thrown = Assert.Throws<TargetInvocationException>(() => boom.Invoke(null, null));
        Assert.IsType<InvalidOperationException>(thrown.InnerException);
    }

    // -----------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------

    private static TraceFrameInfo Frame(string method, string file, int line) =>
        new(method, $"{file}:{line}", file, line);

    private static TraceEventRecord Event(
        string provider, string name, double timeMs, int thread, params (string Key, string Value)[] payload) =>
        new(provider, name, timeMs, thread, Guid.Empty, Guid.Empty, "Informational",
            payload.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.OrdinalIgnoreCase));

    private static TraceEventSet Set(params TraceEventRecord[] events) =>
        new("fake.nettrace", events.Length == 0 ? 0 : events.Max(e => e.TimeMs), 0, false, events);

    private sealed class FakeReader(TraceStackModel model) : ITraceStackReader
    {
        public TraceStackModel Read(string tracePath, bool resolveSource = true) => model;
    }

    private sealed class FakeStructure(CodeFileStructure structure) : ICodeStructureIndex
    {
        public CodeFileStructure Describe(string file) => structure;
    }

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

        public string WriteSource(string name, string content) => WriteFile(name, content);

        /// <summary>Compiles a small assembly in the temp folder and returns its path.</summary>
        public string CompileAssembly(string name, string source)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            };

            var compilation = CSharpCompilation.Create(
                name,
                [tree],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var path = System.IO.Path.Combine(Path, name + ".dll");
            var emitted = compilation.Emit(path);
            Assert.True(
                emitted.Success,
                string.Join(Environment.NewLine, emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
