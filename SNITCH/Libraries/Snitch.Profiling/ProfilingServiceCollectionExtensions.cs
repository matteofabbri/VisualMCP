using Snitch.Profiling;
using Snitch.Profiling.Analysis;
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
/// thread-pool starvation, change review), the symbol downloader and the monitor wrapper.
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

        // The answer layer: each one orchestrates the observations above and closes with a verdict.
        services.AddSingleton<ISlowdownService, SlowdownService>();
        services.AddSingleton<IMemoryGrowthService, MemoryGrowthService>();
        services.AddSingleton<IThreadPoolStarvationService, ThreadPoolStarvationService>();
        services.AddSingleton<IChangeReviewService, ChangeReviewService>();

        return services;
    }
}
