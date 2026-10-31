using Microsoft.Extensions.DependencyInjection.Extensions;
using Snitch.Profiling;
using Snitch.Profiling.Analysis;
using Snitch.Profiling.Instrumentation;
using Snitch.Profiling.Model;
using Snitch.Profiling.Monitor;
using Snitch.Profiling.Runtime;
using Snitch.Profiling.Snapshots;
using Snitch.Profiling.Symbols;
using Snitch.Profiling.Tooling;
using Snitch.Profiling.Verdicts;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the profiling library: the diagnostic-tool installer, the EventPipe collectors,
/// the ClrMD snapshots, the trace analyzer/advisor, the answer layer (slowdown, memory growth,
/// thread-pool starvation, change review), the event/async/database readers, the instrumented
/// profile, the symbol downloader and the monitor wrapper.
/// </summary>
public static class ProfilingServiceCollectionExtensions
{
    public static IServiceCollection AddVisualMcpProfiling(
        this IServiceCollection services, Action<ProfilingOptions>? configure = null)
    {
        var options = new ProfilingOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddSingleton<IDiagnosticToolService, DiagnosticToolService>();
        services.AddSingleton<IProfiledProcessService, ProfiledProcessService>();
        services.AddSingleton<ITraceCollectionService, TraceCollectionService>();
        services.AddSingleton<ICounterCollectionService, CounterCollectionService>();
        services.AddSingleton<IManagedStackService, ManagedStackService>();
        services.AddSingleton<IManagedHeapService, ManagedHeapService>();
        services.AddSingleton<IRetentionService, RetentionService>();
        services.AddSingleton<IDumpService, DumpService>();
        services.AddSingleton<ITraceStackReader, TraceStackReader>();
        services.AddSingleton<ITraceAnalysisService, TraceAnalysisService>();
        services.AddSingleton<IProfilingAdvisor, ProfilingAdvisor>();
        services.AddSingleton<IAllocationSiteService, AllocationSiteService>();
        services.AddSingleton<ICallTreeService, CallTreeService>();
        services.AddSingleton<IHotPathService, HotPathService>();
        services.AddSingleton<ILineCostService, LineCostService>();
        services.AddSingleton<IIoBlockService, IoBlockService>();
        services.AddSingleton<IProfilingComparisonService, ProfilingComparisonService>();
        services.AddSingleton<IProfileDiagnosisService, ProfileDiagnosisService>();
        services.AddSingleton<ISymbolService, SymbolService>();
        services.AddSingleton<IDotnetMonitorService, DotnetMonitorService>();

        // EventPipe as events only, and the three answers that need it: the Events Viewer, the async
        // timeline (TplEventSource) and the database commands (EF Core/SqlClient/... EventSource).
        services.AddSingleton<ITraceEventReader, EventPipeTraceReader>();
        services.AddSingleton<IEventInventoryService, EventInventoryService>();
        services.AddSingleton<IAsyncFlowService, AsyncFlowService>();
        services.AddSingleton<IDatabaseTraceService, DatabaseTraceService>();

        // The code-structure join: the host replaces this null-object with the metacode AST. Registered
        // as a TryAdd so a host that registers its own implementation wins (the last registration is
        // the one a singleton resolve returns).
        services.TryAddSingleton<ICodeStructureIndex, EmptyCodeStructureIndex>();
        services.AddSingleton<IRootCauseService, RootCauseService>();

        // Instrumented profiling: rewrite an assembly's IL, then read the exact enter/exit pairs.
        services.AddSingleton<IAssemblyInstrumenter, AssemblyInstrumenter>();
        services.AddSingleton<IInstrumentationAnalysisService, InstrumentationAnalysisService>();

        // The answer layer: each one orchestrates the observations above and closes with a verdict.
        services.AddSingleton<ISlowdownService, SlowdownService>();
        services.AddSingleton<IMemoryGrowthService, MemoryGrowthService>();
        services.AddSingleton<IThreadPoolStarvationService, ThreadPoolStarvationService>();
        services.AddSingleton<IChangeReviewService, ChangeReviewService>();

        return services;
    }

    /// <summary>
    /// The structure index a host that does not implement one gets: it answers "no declarations" for
    /// every file, so profile_root_cause still reports the measurements and says the AST is missing
    /// instead of failing to construct the service.
    /// </summary>
    private sealed class EmptyCodeStructureIndex : ICodeStructureIndex
    {
        public CodeFileStructure Describe(string file) =>
            new(file, [], "No code-structure index is registered in this host.");
    }
}
