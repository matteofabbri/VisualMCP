namespace Snitch.Profiling.Model;

// ---------------------------------------------------------------------------
// Tools
// ---------------------------------------------------------------------------

/// <summary>Installation state of one dotnet diagnostic tool, plus what VisualMCP covers natively.</summary>
public sealed record DiagnosticToolStatus(
    string Id,
    string Command,
    string PackageId,
    bool Installed,
    string? Version,
    string? LatestVersion,
    string Source,
    string? Path,
    string Purpose,
    string NativeCoverage,
    string DelegatedOperations,
    string DocsUrl);

public sealed record ToolInstallResult(
    string Tool,
    string Action,
    bool Ok,
    int ExitCode,
    string Output);

// ---------------------------------------------------------------------------
// Processes
// ---------------------------------------------------------------------------

public sealed record ProfiledProcess(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    long WorkingSetBytes,
    DateTime? StartTime,
    bool IsSelf);

// ---------------------------------------------------------------------------
// Trace collection (.nettrace via EventPipe)
// ---------------------------------------------------------------------------

public sealed record TraceCollectResult(
    int ProcessId,
    string ProcessName,
    string Path,
    long SizeBytes,
    string Profile,
    double DurationMs,
    IReadOnlyList<string> Providers);

// ---------------------------------------------------------------------------
// Counters (dotnet-counters reimplementation)
// ---------------------------------------------------------------------------

public sealed record CounterPoint(double TimeMs, double Value);

public sealed record CounterSeries(
    string Provider,
    string Name,
    string? DisplayName,
    string? Unit,
    string? CounterType,
    double Min,
    double Max,
    double Mean,
    double Last,
    int Points,
    IReadOnlyList<CounterPoint> Samples);

public sealed record CounterCollectionResult(
    int ProcessId,
    string ProcessName,
    double DurationMs,
    double IntervalSeconds,
    IReadOnlyList<string> Providers,
    IReadOnlyList<CounterSeries> Series,
    IReadOnlyList<string> ProvidersWithoutCounters,
    IReadOnlyList<string> Warnings);

// ---------------------------------------------------------------------------
// Managed stacks (dotnet-stack reimplementation)
// ---------------------------------------------------------------------------

public sealed record StackFrameInfo(int Depth, string Method, string? Source);

public sealed record ThreadStackInfo(
    int ManagedThreadId,
    uint OsThreadId,
    string State,
    bool IsFinalizer,
    bool IsGc,
    int LockCount,
    string? ExceptionType,
    IReadOnlyList<StackFrameInfo> Frames);

public sealed record MonitorLock(
    string ObjectType,
    string HolderThread,
    int WaitingThreads,
    int Recursion,
    string? HolderStack);

public sealed record StackSnapshot(
    string Source,
    string RuntimeVersion,
    IReadOnlyList<ThreadStackInfo> Threads,
    IReadOnlyList<MonitorLock> ContendedMonitors,
    int ThreadsTruncated);

// ---------------------------------------------------------------------------
// Managed heap (dotnet-gcdump reimplementation)
// ---------------------------------------------------------------------------

public sealed record HeapTypeStat(string TypeName, long Count, long Bytes, double Share);

public sealed record HeapObjectSample(string Address, string TypeName, long Bytes, bool Finalizable);

public sealed record DuplicateString(string Value, int Count, long WastedBytes);

// ---------------------------------------------------------------------------
// Retention graph ("who keeps this alive?")
// ---------------------------------------------------------------------------

/// <summary>
/// One figure of a root-to-target chain. <see cref="Field"/> is the field of the *parent* that
/// holds this object ("System.Collections.Generic.Dictionary`2._entries", "static MyApp.Cache.Root")
/// and is null only for the root frame, which carries <see cref="RetentionPath.Root"/> instead.
/// </summary>
public sealed record RetentionFrame(string TypeName, string? Field, long Bytes);

/// <summary>A concrete chain from a GC root to one instance of the focus type.</summary>
public sealed record RetentionPath(
    string TargetType,
    long TargetBytes,
    string Root,
    IReadOnlyList<RetentionFrame> Frames);

/// <summary>Who points at the focus instances, grouped by the referrer's type.</summary>
public sealed record ReferrerGroup(
    string TypeName,
    long References,
    long ReferrerObjects,
    string? ExamplePath);

/// <summary>
/// The answer to "why is this type still in memory?": the instances that exist, the root chains
/// that reach them, and the types that hold references to them. <see cref="Truncated"/> means a
/// budget stopped the walk, so a missing path is "not found", never "not reachable".
/// </summary>
public sealed record RetentionReport(
    string Source,
    string FocusType,
    bool FocusWasAutoSelected,
    int InstanceCount,
    long InstanceBytes,
    long TotalObjects,
    long TotalBytes,
    IReadOnlyList<ReferrerGroup> Referrers,
    IReadOnlyList<RetentionPath> Paths,
    int VisitedNodes,
    bool Truncated,
    bool PathSearchComplete,
    IReadOnlyList<ProfilingFinding> Findings,
    IReadOnlyList<string> NextSteps,
    IReadOnlyList<string> Warnings);

public sealed record HeapSnapshot(
    string Source,
    string RuntimeVersion,
    bool ServerGc,
    int Threads,
    long TotalBytes,
    long ObjectCount,
    long FreeBytes,
    long LohBytes,
    long PinnedBytes,
    long FinalizableObjects,
    bool Truncated,
    IReadOnlyList<HeapTypeStat> TopTypes,
    IReadOnlyList<HeapObjectSample> Samples,
    IReadOnlyList<DuplicateString> DuplicateStrings);

// ---------------------------------------------------------------------------
// Dumps (dotnet-dump reimplementation)
// ---------------------------------------------------------------------------

public sealed record DumpCollectResult(
    string Path,
    long SizeBytes,
    string DumpType,
    int ProcessId,
    string ProcessName,
    double DurationMs);

public sealed record DumpAnalysis(
    string Path,
    long SizeBytes,
    HeapSnapshot Heap,
    StackSnapshot? Stacks,
    string? Note);

// ---------------------------------------------------------------------------
// Trace analysis
// ---------------------------------------------------------------------------

public sealed record TraceInfo(
    string Path,
    long SizeBytes,
    double DurationMs,
    long EventCount,
    int EventsLost,
    long CpuSamples,
    double TotalCpuMs);

public sealed record CpuHotspot(string Method, string? Source, long Samples, double Share, long EstimatedCpuMs);

/// <summary>
/// Self CPU attributed to one source line, from the sampled instruction pointer through the
/// method's IL→native map to the nearest PDB sequence point. <see cref="Text"/> is the source
/// line when the caller asked for it.
/// </summary>
public sealed record CpuLineHotspot(
    string File,
    int Line,
    string Method,
    long Samples,
    double Share,
    long EstimatedCpuMs,
    string? Text = null);

/// <summary>
/// Answer to "which line inside this method burns the CPU?". The scope is the whole trace or one
/// method; <see cref="Share"/> on each line is of <see cref="ScopeCpuMs"/>, so a line that owns
/// half of its method is reported as 50 even when the method itself is small.
/// </summary>
public sealed record LineCostReport(
    string TracePath,
    string Scope,
    double ScopeCpuMs,
    double TraceCpuMs,
    long Samples,
    long AttributedSamples,
    double AttributedCpuMs,
    IReadOnlyList<CpuLineHotspot> Lines,
    IReadOnlyList<ProfilingFinding> Findings,
    IReadOnlyList<string> NextSteps,
    IReadOnlyList<string> Warnings);

public sealed record CpuStackHotspot(string Stack, double CpuMs, double Share);

public sealed record AllocationHotspot(string TypeName, long SampledBytes, long Samples, double Share);

/// <summary>Sampled allocation bytes attributed to the method that performed the allocation.</summary>
public sealed record AllocationSiteHotspot(
    string Site,
    string? Source,
    string TypeName,
    long SampledBytes,
    long Samples,
    double Share,
    string Kind);

public sealed record AllocationStackHotspot(string Stack, string TypeName, long SampledBytes, double Share);

public sealed record AllocationSiteReport(
    string TracePath,
    int Sites,
    long SampledBytes,
    long Samples,
    bool Truncated,
    IReadOnlyList<AllocationSiteHotspot> TopSites,
    IReadOnlyList<AllocationHotspot> ByType,
    IReadOnlyList<AllocationStackHotspot> TopStacks,
    IReadOnlyList<ProfilingFinding> Findings);

public sealed record TimelineBucket(
    double StartMs,
    double DurationMs,
    double CpuMs,
    long SampledAllocationBytes,
    int Gen0,
    int Gen1,
    int Gen2,
    double GcPauseMs,
    int Exceptions,
    double ContentionMs);

public sealed record CallTreeNode(
    string Method,
    string? Source,
    double InclusiveMs,
    double ExclusiveMs,
    long Samples,
    int Depth,
    string Path);

public sealed record CallTreeReport(
    string TracePath,
    double TotalCpuMs,
    IReadOnlyList<CallTreeNode> TopInclusive,
    IReadOnlyList<CallTreeNode> TopExclusive,
    IReadOnlyList<CallTreeNode> Roots,
    string? FoldedPath,
    long FoldedStacks,
    IReadOnlyList<string> Warnings);

// ---------------------------------------------------------------------------
// Hot paths
// ---------------------------------------------------------------------------

/// <summary>One root-to-leaf path with its cost, and the user frame to act on.</summary>
public sealed record HotPathEntry(
    string Metric,
    string Leaf,
    string? LeafSource,
    string? ActionableMethod,
    string? ActionableSource,
    double Cost,
    double Share,
    double SelfShare,
    double InclusiveShare,
    int Variants,
    IReadOnlyList<string> Path,
    string Recommendation);

/// <summary>A frame where the cost splits between several hot children.</summary>
public sealed record HotBranchEntry(
    string Metric,
    string Frame,
    string? Source,
    double InclusiveCost,
    double Share,
    int HotChildren,
    string TopChild,
    double TopChildShare,
    string Recommendation);

/// <summary>The fix list: one entry per user method/line, aggregated across all paths.</summary>
public sealed record HotMethodEntry(
    string Metric,
    string Method,
    string? Source,
    double Cost,
    double Share,
    double InclusiveShare,
    int Variants,
    string? Leaf,
    string Recommendation);

public sealed record HotPathReport(
    string TracePath,
    string Metric,
    string Unit,
    double TotalCost,
    double DurationMs,
    IReadOnlyList<HotMethodEntry> HotMethods,
    IReadOnlyList<HotPathEntry> TopPaths,
    IReadOnlyList<HotBranchEntry> Branches,
    IReadOnlyList<ProfilingFinding> Findings,
    IReadOnlyList<string> Warnings);

// ---------------------------------------------------------------------------
// I/O blocking
// ---------------------------------------------------------------------------

/// <summary>One blocking call site, aggregated over the intervals spent waiting there.</summary>
public sealed record IoBlockSite(
    string Category,
    string CallSite,
    string? Source,
    string BlockingMethod,
    double BlockedMs,
    double Share,
    int Threads,
    long Samples,
    IReadOnlyList<string> Stack,
    string Recommendation);

public sealed record IoCategorySummary(
    string Category,
    double BlockedMs,
    double Share,
    int Sites,
    int Threads);

public sealed record IoBlockReport(
    string TracePath,
    double DurationMs,
    double TotalBlockedMs,
    double IdleMs,
    double AverageBlockedThreads,
    IReadOnlyList<IoBlockSite> Sites,
    IReadOnlyList<IoCategorySummary> Categories,
    IReadOnlyList<ProfilingFinding> Findings,
    IReadOnlyList<string> Warnings);

public sealed record GcPauseInfo(int Index, double StartMs, int Generation, double DurationMs, string Reason, string Kind);

public sealed record GcSummary(
    int Gen0,
    int Gen1,
    int Gen2,
    double TotalPauseMs,
    double MaxPauseMs,
    double PauseShare,
    long HeapBytes,
    long LohBytes,
    long PromotedBytes,
    long SampledAllocationBytes,
    long FinalizedObjects,
    IReadOnlyList<GcPauseInfo> LongestPauses);

public sealed record ExceptionHotspot(string Type, long Count, string? SampleMessage, string? Source);

public sealed record ContentionLock(string Lock, long Waits, double TotalWaitMs, double MaxWaitMs);

public sealed record ContentionSummary(long Events, double TotalWaitMs, double MaxWaitMs, IReadOnlyList<ContentionLock> TopLocks);

public sealed record ThreadPoolSummary(
    int MaxActiveWorkers,
    int MinActiveWorkers,
    long Enqueued,
    long Dequeued,
    long MaxBacklog,
    int AdjustmentSamples);

public sealed record JitHotspot(string Method, double JitMs, int IlSize);

public sealed record JitSummary(int Methods, double TotalJitMs, IReadOnlyList<JitHotspot> Slowest);

public sealed record TraceAnalysis(
    TraceInfo Trace,
    IReadOnlyList<CpuHotspot> CpuHotspots,
    IReadOnlyList<CpuStackHotspot> CpuStacks,
    IReadOnlyList<AllocationSiteHotspot> AllocationSites,
    IReadOnlyList<AllocationHotspot> Allocations,
    GcSummary Gc,
    IReadOnlyList<ExceptionHotspot> Exceptions,
    ContentionSummary Contention,
    ThreadPoolSummary ThreadPool,
    JitSummary Jit,
    IReadOnlyList<TimelineBucket> Timeline,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ProfilingFinding> Findings,
    IReadOnlyList<string> NextSteps)
{
    /// <summary>
    /// The hottest source lines of the trace (self CPU). Init-only rather than positional so the
    /// record keeps its constructor: every existing builder and test keeps compiling, and a
    /// consumer that does not care about line granularity simply sees an empty list.
    /// </summary>
    public IReadOnlyList<CpuLineHotspot> HotLines { get; init; } = [];
}

// ---------------------------------------------------------------------------
// Findings / report
// ---------------------------------------------------------------------------

public sealed record ProfilingFinding(
    string Id,
    string Severity,
    string Title,
    string Evidence,
    string Recommendation,
    string? SuggestedAction,
    string? Source);

// ---------------------------------------------------------------------------
// Comparison / diagnosis
// ---------------------------------------------------------------------------

public sealed record MetricDelta(
    string Name,
    double Left,
    double Right,
    double Delta,
    double Percent);

public sealed record CpuMethodDelta(
    string Method,
    string? Source,
    double LeftMs,
    double RightMs,
    double DeltaMs,
    double Percent);

public sealed record TypeDelta(
    string TypeName,
    long LeftBytes,
    long RightBytes,
    long DeltaBytes,
    long LeftCount,
    long RightCount,
    long DeltaCount,
    double Percent);

public sealed record ProfileComparison(
    string Kind,
    string LeftPath,
    string RightPath,
    IReadOnlyList<MetricDelta> Metrics,
    IReadOnlyList<CpuMethodDelta> Cpu,
    IReadOnlyList<TypeDelta> Allocations,
    IReadOnlyList<TypeDelta> Heap,
    IReadOnlyList<ProfilingFinding> Findings,
    IReadOnlyList<string> NextSteps);

public sealed record DiagnosisReport(
    string ProcessName,
    int ProcessId,
    DateTime StartedAt,
    double DurationSeconds,
    string? TracePath,
    string? ReportPath,
    IReadOnlyList<CounterSeries> Counters,
    TraceAnalysis Trace,
    HeapSnapshot? Heap,
    StackSnapshot? Stacks,
    IReadOnlyList<ProfilingFinding> Findings,
    IReadOnlyList<string> NextSteps,
    IReadOnlyList<string> Warnings);
