using Snitch.Profiling.Model;

namespace Snitch.Profiling.Analysis;

/// <summary>
/// Turns raw measurements into the answer the caller actually wants: what is slow, what is
/// leaking, what is blocked, and which concrete call to make next. Thresholds are deliberately
/// conservative — a finding is a lead, not a verdict — and every finding carries the number that
/// produced it so it can be checked.
/// </summary>
public interface IProfilingAdvisor
{
    IReadOnlyList<ProfilingFinding> Analyze(TraceAnalysis analysis);

    IReadOnlyList<ProfilingFinding> Analyze(HeapSnapshot heap);

    IReadOnlyList<ProfilingFinding> Analyze(RetentionReport report);

    IReadOnlyList<ProfilingFinding> Analyze(StackSnapshot stacks);

    IReadOnlyList<ProfilingFinding> Analyze(CounterCollectionResult counters);

    IReadOnlyList<string> NextSteps(IEnumerable<ProfilingFinding> findings);
}

public sealed class ProfilingAdvisor : IProfilingAdvisor
{
    public IReadOnlyList<ProfilingFinding> Analyze(TraceAnalysis analysis)
    {
        var findings = new List<ProfilingFinding>();
        var trace = analysis.Trace;

        // ---- CPU -------------------------------------------------------------
        var top = analysis.CpuHotspots.FirstOrDefault();
        if (top is not null && top.Samples >= 50)
        {
            if (top.Share >= 40)
            {
                findings.Add(new ProfilingFinding(
                    "cpu-hotspot", "warning",
                    $"CPU is concentrated in {ShortName(top.Method)}",
                    $"{top.Method} took {top.Samples:N0} samples ({top.Share:0.0}% of {trace.CpuSamples:N0}) ≈ {top.EstimatedCpuMs:N0} ms of one core.",
                    "Optimise or cache the work in this method first; a single hot method is the cheapest place to start.",
                    "Open the method with read_symbol and measure the change with quick_benchmark.",
                    top.Source));
            }
            else if (trace.CpuSamples >= 200)
            {
                var top3 = analysis.CpuHotspots.Take(3).Sum(h => h.Share);
                if (top3 >= 80)
                    findings.Add(new ProfilingFinding(
                        "cpu-concentration", "info",
                        "CPU is spread over a few methods",
                        $"The top 3 methods cover {top3:0.0}% of {trace.CpuSamples:N0} samples.",
                        "No single hotspot dominates; look at the top stacks below the methods to find the shared caller.",
                        null,
                        null));
            }
        }

        // The line is a sharper version of the hotspot above: when one statement owns the method's
        // cost, opening the method is not enough — the fix is on that line.
        var hotLine = analysis.HotLines.FirstOrDefault();
        if (hotLine is not null && hotLine.Share >= 25 && hotLine.Samples >= 50)
            findings.Add(new ProfilingFinding(
                "cpu-hot-line", hotLine.Share >= 50 ? "critical" : "warning",
                $"Line {hotLine.Line} is where the CPU is spent",
                $"{hotLine.File}:{hotLine.Line} in {ShortName(hotLine.Method)}: {hotLine.EstimatedCpuMs:N0} ms " +
                $"({hotLine.Share:0.0}% of all samples, {hotLine.Samples:N0} samples).",
                "A single dominant line means a loop body, an allocation, a lock or a virtual/interface call: hoist, batch, cache or inline it.",
                $"Open {hotLine.File}:{hotLine.Line}",
                $"{hotLine.File}:{hotLine.Line}"));

        // ---- GC --------------------------------------------------------------
        var gc = analysis.Gc;
        if (gc.PauseShare >= 10)
            findings.Add(new ProfilingFinding(
                "gc-pause", gc.PauseShare >= 25 ? "critical" : "warning",
                "Garbage collection is taking a visible share of the time",
                $"{gc.TotalPauseMs:N0} ms paused in {gc.Gen0 + gc.Gen1 + gc.Gen2} collections over {trace.DurationMs:N0} ms ({gc.PauseShare:0.0}%), longest {gc.MaxPauseMs:N0} ms.",
                "Reduce allocation volume or lifetime; if this is a server app, check that Server GC is enabled.",
                null,
                null));

        if (gc.Gen2 >= 5)
            findings.Add(new ProfilingFinding(
                "gen2-pressure", gc.Gen2 >= 20 ? "warning" : "info",
                "Objects are surviving into generation 2",
                $"{gc.Gen2} generation-2 collections; {gc.PromotedBytes / 1024 / 1024:N1} MB promoted in total.",
                "Find the objects that survive: take a heap snapshot and look at the dominant types; a cache or a static collection is the usual cause.",
                null,
                null));

        if (gc.HeapBytes > 0 && gc.LohBytes >= gc.HeapBytes * 0.25)
            findings.Add(new ProfilingFinding(
                "loh-pressure", "warning",
                "The large object heap is a quarter of the managed heap",
                $"LOH {gc.LohBytes / 1024 / 1024:N1} MB of {gc.HeapBytes / 1024 / 1024:N1} MB ({100.0 * gc.LohBytes / gc.HeapBytes:0.0}%).",
                "Objects of 85 KB or more go to the LOH and are not compacted by default. Pool large buffers (ArrayPool<T>) or avoid building large intermediate arrays.",
                null,
                null));

        var allocation = analysis.Allocations.FirstOrDefault();
        if (allocation is not null && allocation.Share >= 40 && allocation.SampledBytes >= 1024 * 1024)
            findings.Add(new ProfilingFinding(
                "allocation-hotspot", "warning",
                $"Most allocations are {ShortName(allocation.TypeName)}",
                $"{allocation.TypeName}: {allocation.SampledBytes / 1024 / 1024:N1} MB across {allocation.Samples:N0} samples ({allocation.Share:0.0}% of sampled bytes).",
                "Reuse or pool this type, or replace it with a struct/value representation on the hot path.",
                null,
                null));

        var allocationSite = analysis.AllocationSites.FirstOrDefault();
        if (allocationSite is not null && allocationSite.Site != "(no allocation stack)" &&
            allocationSite.Share >= 40 && allocationSite.SampledBytes >= 1024 * 1024)
            findings.Add(new ProfilingFinding(
                "allocation-site", "warning",
                $"Most allocations happen in {ShortName(allocationSite.Site)}",
                $"{allocationSite.Site} allocates {ShortName(allocationSite.TypeName)}: {allocationSite.SampledBytes / 1024.0 / 1024.0:N1} MB " +
                $"across {allocationSite.Samples:N0} samples ({allocationSite.Share:0.0}% of sampled bytes).",
                "Fix the allocation at its source: reuse the object, rent the buffer, or return a struct/span instead of a new instance.",
                "profile_allocations with focusType='" + allocationSite.TypeName + "' shows the full stack of that site.",
                allocationSite.Source));

        // ---- CPU shape over time --------------------------------------------
        var cpuBuckets = analysis.Timeline.Where(b => b.CpuMs > 0).ToList();
        if (cpuBuckets.Count >= 3)
        {
            var mean = cpuBuckets.Average(b => b.CpuMs);
            var peak = cpuBuckets.MaxBy(b => b.CpuMs)!;
            if (peak.CpuMs >= 200 && peak.CpuMs >= mean * 2)
                findings.Add(new ProfilingFinding(
                    "cpu-spike", "info",
                    "CPU is concentrated in a time window",
                    $"At {peak.StartMs / 1000.0:0.0}s the process used {peak.CpuMs:N0} ms of CPU against an average of {mean:N0} ms per bucket.",
                    "If the spike matches a request or a batch, profile that unit of work rather than the whole process.",
                    null,
                    null));
        }

        var allocationPeak = analysis.Timeline.MaxBy(b => b.SampledAllocationBytes);
        if (allocationPeak is not null && allocationPeak.SampledAllocationBytes >= 10L * 1024 * 1024 &&
            analysis.Timeline.Count >= 3)
        {
            var mean = analysis.Timeline.Average(b => b.SampledAllocationBytes);
            if (allocationPeak.SampledAllocationBytes >= mean * 3)
                findings.Add(new ProfilingFinding(
                    "allocation-spike", "info",
                    "Allocations spike in a time window",
                    $"At {allocationPeak.StartMs / 1000.0:0.0}s {allocationPeak.SampledAllocationBytes / 1024.0 / 1024.0:N1} MB " +
                    $"was sampled against an average of {mean / 1024.0 / 1024.0:N1} MB per bucket.",
                    "Pair this window with the request or batch that ran then; the allocation site for that path is the target.",
                    null,
                    null));
        }

        var exceptionWithSource = analysis.Exceptions.FirstOrDefault(e => e.Source is not null);
        if (exceptionWithSource is not null && analysis.Exceptions.Sum(e => e.Count) >= 100)
            findings.Add(new ProfilingFinding(
                "exception-site", "info",
                $"Exceptions are thrown from {ShortName(exceptionWithSource.Type)}",
                $"{exceptionWithSource.Count:N0} {exceptionWithSource.Type} exceptions; the throw site is {exceptionWithSource.Source}.",
                "If this is control flow, replace it with a Try* or result type at the throw site.",
                exceptionWithSource.Source,
                exceptionWithSource.Source));

        if (trace.DurationMs > 0 && gc.SampledAllocationBytes / 1024.0 / 1024.0 / (trace.DurationMs / 1000.0) >= 100)
            findings.Add(new ProfilingFinding(
                "allocation-rate", "info",
                "High allocation rate",
                $"≈{gc.SampledAllocationBytes / 1024.0 / 1024.0 / (trace.DurationMs / 1000.0):0} MB/s of sampled allocations.",
                "At this rate the GC runs often regardless of what the code does; look at the allocation hotspot and the top CPU methods that allocate.",
                null,
                null));

        if (gc.FinalizedObjects >= 10_000)
            findings.Add(new ProfilingFinding(
                "finalizer-pressure", "warning",
                "Many objects went through the finalizer",
                $"{gc.FinalizedObjects:N0} finalizable objects were collected.",
                "Objects with a finalizer survive an extra collection. Implement IDisposable and dispose deterministically; suppress the finalizer in Dispose.",
                null,
                null));

        // ---- Exceptions ------------------------------------------------------
        var exceptionCount = analysis.Exceptions.Sum(e => e.Count);
        if (exceptionCount >= 100)
        {
            var worst = analysis.Exceptions.FirstOrDefault();
            findings.Add(new ProfilingFinding(
                "exception-storm", exceptionCount >= 1000 ? "critical" : "warning",
                "Exceptions are being used for control flow",
                $"{exceptionCount:N0} exceptions thrown" +
                (worst is null ? "." : $", mostly {worst.Type} ({worst.Count:N0})."),
                "Throwing is expensive (stack walk, first-chance notification). Return a result type or use TryParse/TryGetValue on the hot path.",
                worst is null ? null : "find_references " + ShortName(worst.Type),
                null));
        }

        // ---- Contention ------------------------------------------------------
        var contention = analysis.Contention;
        if (contention.TotalWaitMs >= 500 || contention.MaxWaitMs >= 100)
            findings.Add(new ProfilingFinding(
                "lock-contention", contention.TotalWaitMs >= 2000 ? "critical" : "warning",
                "Threads are waiting on locks",
                $"{contention.Events:N0} contention events, {contention.TotalWaitMs:N0} ms waited in total, longest {contention.MaxWaitMs:N0} ms.",
                "Shrink the critical section, avoid I/O while holding a lock, or partition the lock.",
                "profile_stacks (the contended monitors section names the holding thread).",
                null));

        // ---- Thread pool -----------------------------------------------------
        var tp = analysis.ThreadPool;
        if (tp.MaxBacklog >= 1000)
            findings.Add(new ProfilingFinding(
                "threadpool-starvation", "warning",
                "The thread pool queue grew without being drained",
                $"At its peak {tp.MaxBacklog:N0} work items were queued ({tp.Enqueued:N0} enqueued, {tp.Dequeued:N0} dequeued), max active workers {tp.MaxActiveWorkers}.",
                "Blocking calls on thread-pool threads cause this: find the blocking stack and make it async.",
                "profile_stacks and look for threads blocked in Task.Wait, .Result, Thread.Sleep or a synchronous I/O call.",
                null));

        // ---- JIT -------------------------------------------------------------
        if (analysis.Jit.TotalJitMs >= 500)
        {
            var slowest = analysis.Jit.Slowest.FirstOrDefault();
            findings.Add(new ProfilingFinding(
                "jit-time", "info",
                "JIT compilation is a measurable startup cost",
                $"{analysis.Jit.Methods:N0} methods JITted for {analysis.Jit.TotalJitMs:N0} ms" +
                (slowest is null ? "." : $"; slowest {ShortName(slowest.Method)} at {slowest.JitMs:N0} ms."),
                "For startup-sensitive apps enable ReadyToRun, or warm the hot paths before serving traffic.",
                null,
                null));
        }

        return findings.OrderByDescending(f => SeverityRank(f.Severity)).ToList();
    }

    public IReadOnlyList<ProfilingFinding> Analyze(CounterCollectionResult counters)
    {
        var findings = new List<ProfilingFinding>();

        CounterSeries? Find(string name) =>
            counters.Series.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        static string Evidence(CounterSeries s) =>
            $"min {s.Min:0.##}, mean {s.Mean:0.##}, max {s.Max:0.##} {(string.IsNullOrWhiteSpace(s.Unit) ? "" : s.Unit)} " +
            $"over {s.Points} sample(s)";

        var cpu = Find("cpu-usage");
        if (cpu is not null && cpu.Max >= 80)
            findings.Add(new ProfilingFinding(
                "cpu-saturated", cpu.Max >= 95 ? "critical" : "warning",
                "CPU usage is saturated",
                $"cpu-usage: {Evidence(cpu)}.",
                "At this level the process is CPU-bound: the trace's top method is the place to start, not the counters.",
                "profile_collect with profile='cpu' then profile_analyze.",
                null));

        var gcTime = Find("time-in-gc");
        if (gcTime is not null && gcTime.Mean >= 10)
            findings.Add(new ProfilingFinding(
                "gc-time-high", gcTime.Mean >= 25 ? "critical" : "warning",
                "A large share of the time is spent in GC",
                $"time-in-gc: {Evidence(gcTime)}.",
                "Reduce allocation volume or lifetime; check Server GC for a multi-core service.",
                "profile_collect with profile='gc' then profile_analyze (or profile_allocations for sites).",
                null));

        var allocationRate = Find("alloc-rate");
        if (allocationRate is not null && allocationRate.Mean >= 100 * 1024 * 1024)
            findings.Add(new ProfilingFinding(
                "allocation-rate-high", "warning",
                "The application allocates at a high rate",
                $"alloc-rate: {allocationRate.Mean / 1024.0 / 1024.0:N0} MB/s mean, peak {allocationRate.Max / 1024.0 / 1024.0:N0} MB/s.",
                "The allocation site is the fix: one type or one method usually dominates.",
                "profile_allocations.",
                null));

        var queue = Find("threadpool-queue-length");
        if (queue is not null && queue.Max >= 1000)
            findings.Add(new ProfilingFinding(
                "threadpool-queue", "warning",
                "The thread pool queue is backing up",
                $"threadpool-queue-length: {Evidence(queue)}.",
                "Blocking calls on pool threads cause this; find the blocking stack and make it async.",
                "profile_stacks.",
                null));

        var contention = Find("monitor-lock-contention-count");
        var contentionTotal = contention?.Samples.Sum(p => p.Value) ?? 0;
        if (contention is not null && contentionTotal >= 100)
            findings.Add(new ProfilingFinding(
                "lock-contention-count", "warning",
                "Monitor contention is frequent",
                $"{contentionTotal:N0} contention events (mean {contention.Mean:0.##} per interval).",
                "Read the contended monitor's holding thread before changing the lock.",
                "profile_stacks (contended monitors).",
                null));

        var gen2 = Find("gen-2-gc-count");
        var gen2Total = gen2?.Samples.Sum(p => p.Value) ?? 0;
        if (gen2 is not null && gen2Total >= 5)
            findings.Add(new ProfilingFinding(
                "gen2-rate", "info",
                "Generation-2 collections are frequent",
                $"{gen2Total:N0} gen-2 collections in the sampled window.",
                "Objects are surviving; a heap snapshot shows which type owns them.",
                "profile_heap.",
                null));

        var exceptionCount = Find("exception-count");
        var exceptionsTotal = exceptionCount?.Samples.Sum(p => p.Value) ?? 0;
        if (exceptionCount is not null && exceptionsTotal >= 100)
            findings.Add(new ProfilingFinding(
                "exception-rate", "warning",
                "Exceptions are frequent",
                $"{exceptionsTotal:N0} exceptions in the sampled window.",
                "Exception-driven control flow is expensive; the throw site is in the trace.",
                "profile_collect with profile='exceptions' then profile_analyze.",
                null));

        return findings.OrderByDescending(f => SeverityRank(f.Severity)).ToList();
    }

    public IReadOnlyList<ProfilingFinding> Analyze(HeapSnapshot heap)
    {
        var findings = new List<ProfilingFinding>();
        var top = heap.TopTypes.FirstOrDefault();

        if (top is not null && top.Share >= 30 && heap.TotalBytes > 0)
            findings.Add(new ProfilingFinding(
                "heap-type-dominance", "warning",
                $"A single type owns {top.Share:0}% of the managed heap",
                $"{top.TypeName}: {top.Bytes / 1024 / 1024:N1} MB in {top.Count:N0} instances of {heap.TotalBytes / 1024 / 1024:N1} MB.",
                "Confirm the instances are expected: a growing count of a DTO/byte[]/string type is usually a cache without eviction or a leak.",
                "profile_retention focusType=" + top.TypeName + " to see the root chains and the types that keep it alive.",
                null));

        if (heap.TotalBytes > 0 && heap.LohBytes >= heap.TotalBytes * 0.25)
            findings.Add(new ProfilingFinding(
                "heap-loh", "warning",
                "Large objects dominate the heap",
                $"LOH {heap.LohBytes / 1024 / 1024:N1} MB of {heap.TotalBytes / 1024 / 1024:N1} MB.",
                "Pool large buffers and avoid resizing large arrays; LOH fragmentation is hard to recover from.",
                null,
                null));

        if (heap.FinalizableObjects >= 50_000)
            findings.Add(new ProfilingFinding(
                "finalizer-queue", "warning",
                "The finalizer queue is large",
                $"{heap.FinalizableObjects:N0} objects are waiting for finalization.",
                "A large finalizer queue delays memory reclamation. Dispose deterministically and suppress finalizers.",
                null,
                null));

        var duplicate = heap.DuplicateStrings.FirstOrDefault();
        if (duplicate is not null && duplicate.WastedBytes >= 50L * 1024 * 1024)
            findings.Add(new ProfilingFinding(
                "duplicate-strings", "warning",
                "Large amounts of duplicated strings",
                $"\"{Truncate(duplicate.Value, 40)}\" appears {duplicate.Count:N0} times, wasting {duplicate.WastedBytes / 1024 / 1024:N1} MB.",
                "Deduplicate with string interning or a shared lookup; repeated strings are often parsed configuration or identifiers.",
                null,
                null));

        if (heap.Truncated)
            findings.Add(new ProfilingFinding(
                "heap-truncated", "info",
                "The heap walk stopped at the safety limit",
                $"Only the first {heap.ObjectCount:N0} objects were read; totals are a lower bound.",
                "Run the analysis on a dump of the process if you need the complete heap.",
                null,
                null));

        return findings.OrderByDescending(f => SeverityRank(f.Severity)).ToList();
    }

    public IReadOnlyList<ProfilingFinding> Analyze(RetentionReport report)
    {
        var findings = new List<ProfilingFinding>();
        var share = report.TotalBytes == 0 ? 0 : report.InstanceBytes * 100.0 / report.TotalBytes;
        var path = report.Paths.FirstOrDefault();

        if (path is not null)
            findings.Add(new ProfilingFinding(
                "retention-path", share >= 10 ? "warning" : "info",
                $"What keeps {ShortName(report.FocusType)} alive",
                $"{report.InstanceCount:N0} instance(s), {report.InstanceBytes / 1024.0 / 1024.0:N1} MB " +
                $"({share:0.0}% of the {report.TotalBytes / 1024.0 / 1024.0:N1} MB heap). " +
                $"Shortest root chain: {RetentionWalker.FormatPath(path)}",
                RootAdvice(path.Root),
                StaticField(path.Root) is { } field
                    ? $"Open {field} and bound or clear it."
                    : "profile_stacks to see which thread or request still holds the reference.",
                null));

        var totalReferences = report.Referrers.Sum(r => r.References);
        var referrer = report.Referrers.FirstOrDefault();
        if (referrer is not null && totalReferences > 0 && referrer.References >= 2 &&
            referrer.References * 1.0 / totalReferences >= 0.6)
            findings.Add(new ProfilingFinding(
                "retention-referrer", "info",
                $"References to {ShortName(report.FocusType)} come from {ShortName(referrer.TypeName)}",
                $"{referrer.ReferrerObjects:N0} {ShortName(referrer.TypeName)} object(s) hold " +
                $"{referrer.References:N0} of the {totalReferences:N0} tracked reference(s)" +
                (referrer.ExamplePath is null ? "." : $"; e.g. {referrer.ExamplePath}."),
                "Fixing the referrer's lifetime fixes the instances: bound or clear the collection, drop the field, or end the scope.",
                "profile_retention focusType=" + referrer.TypeName + " to walk the referrer's own chain.",
                null));

        if (report.InstanceCount == 0)
            findings.Add(new ProfilingFinding(
                "retention-no-instances", "info",
                $"No instance of '{report.FocusType}' in the heap",
                "The focus is matched as a substring of the CLR type name and nothing matched.",
                "Use the exact name from profile_heap (e.g. System.Byte[] or MyApp.Order[]); generic types include their arguments.",
                "profile_heap to list the types by size.",
                null));
        else if (report.Paths.Count == 0)
            findings.Add(new ProfilingFinding(
                "retention-unreachable", report.PathSearchComplete ? "info" : "warning",
                "No managed root reaches these instances",
                $"{report.InstanceCount:N0} instance(s) of {report.FocusType} exist but no root chain was found" +
                (report.PathSearchComplete
                    ? " (the search completed)."
                    : $" (it stopped after {report.VisitedNodes:N0} nodes)."),
                report.PathSearchComplete
                    ? "They are either garbage not yet collected (take the snapshot after a GC) or reachable only through an " +
                      "interior pointer or a handle this walk does not follow."
                    : "The answer was cut short, not negative: raise maxNodes to search further.",
                report.PathSearchComplete
                    ? "profile_heap on a fresh snapshot: a stable count across collections is a real retention."
                    : "profile_retention with a larger maxNodes.",
                null));

        if (report.Truncated)
            findings.Add(new ProfilingFinding(
                "retention-truncated", "info",
                "The retention walk hit a budget",
                $"Visited {report.VisitedNodes:N0} nodes; the instance list or the referrer pass was cut short.",
                "Treat the answer as a lower bound: a larger maxNodes (or a dump) widens it.",
                "profile_retention maxNodes=500000.",
                null));

        return findings.OrderByDescending(f => SeverityRank(f.Severity)).ToList();
    }

    private static string RootAdvice(string root)
    {
        if (root.StartsWith("static", StringComparison.OrdinalIgnoreCase) ||
            root.StartsWith("thread-static", StringComparison.OrdinalIgnoreCase))
            return "A static field holds it: that is a cache or a global with no eviction. Bound it (size/TTL), " +
                   "hold it weakly, or clear it when the owner shuts down.";
        if (root.StartsWith("stack", StringComparison.OrdinalIgnoreCase))
            return "A thread stack still references it: a long-running request, an iterator that was never drained, " +
                   "or a closure that captured more than it needed. profile_stacks names the thread.";
        if (root.Contains("handle", StringComparison.OrdinalIgnoreCase))
            return "A GC handle keeps it alive: look for a GCHandle.Alloc without a matching Free, or a static handle " +
                   "table entry that is never removed.";
        if (root.Contains("finalizer", StringComparison.OrdinalIgnoreCase))
            return "It is queued for finalization: implement IDisposable, dispose deterministically and suppress the finalizer.";
        return "Follow the chain from the root and break the last link: clear the collection, drop the field, or end the scope.";
    }

    private static string? StaticField(string root)
    {
        if (!root.StartsWith("static ", StringComparison.OrdinalIgnoreCase))
            return null;
        var field = root["static ".Length..].Trim();
        return field.Length == 0 ? null : field;
    }

    public IReadOnlyList<ProfilingFinding> Analyze(StackSnapshot stacks)
    {
        var findings = new List<ProfilingFinding>();

        foreach (var monitor in stacks.ContendedMonitors.Take(3))
            findings.Add(new ProfilingFinding(
                "monitor-contention", monitor.WaitingThreads >= 5 ? "critical" : "warning",
                $"Threads are waiting on a {ShortName(monitor.ObjectType)} monitor",
                $"{monitor.WaitingThreads} thread(s) wait on a lock held by thread {monitor.HolderThread}" +
                (monitor.HolderStack is null ? "." : $", whose top frame is {monitor.HolderStack}."),
                "The holder is the thread to inspect: it is either slow inside the critical section or blocked while holding the lock.",
                "Read the holder's stack above; if it is waiting on I/O, move that I/O outside the lock.",
                null));

        var blocked = stacks.Threads
            .Where(t => t.Frames.Count > 0 && IsBlockingFrame(t.Frames[0].Method))
            .ToList();
        if (blocked.Count >= 8)
            findings.Add(new ProfilingFinding(
                "blocked-threads", "warning",
                "Many threads are blocked in synchronous waits",
                $"{blocked.Count} of {stacks.Threads.Count} threads have a blocking call on top " +
                $"({ShortName(blocked[0].Frames[0].Method)} is the most common).",
                "This is the signature of thread-pool starvation: blocking calls consume pool threads instead of releasing them.",
                "Convert the blocking call to await; if it cannot be, isolate it on a dedicated thread.",
                blocked[0].Frames[0].Source));

        var withException = stacks.Threads.Where(t => t.ExceptionType is not null).ToList();
        if (withException.Count > 0)
            findings.Add(new ProfilingFinding(
                "exception-in-flight", "info",
                "A thread has an exception in flight",
                string.Join(", ", withException.Take(3).Select(t => $"thread {t.ManagedThreadId}: {t.ExceptionType}")) + ".",
                "If the process is hung, this exception may be the reason the thread is unwinding or stuck in a catch/finally.",
                null,
                withException[0].Frames.FirstOrDefault()?.Source));

        return findings.OrderByDescending(f => SeverityRank(f.Severity)).ToList();
    }

    public IReadOnlyList<string> NextSteps(IEnumerable<ProfilingFinding> findings)
    {
        var steps = new List<string>();
        foreach (var finding in findings)
        {
            if (!string.IsNullOrWhiteSpace(finding.SuggestedAction) &&
                !steps.Contains(finding.SuggestedAction, StringComparer.OrdinalIgnoreCase))
                steps.Add(finding.SuggestedAction);
        }

        if (steps.Count == 0)
            steps.Add("No rule fired: collect a longer trace (profile_collect duration) or a heap snapshot (profile_heap) to widen the evidence.");

        return steps.Take(8).ToList();
    }

    private static bool IsBlockingFrame(string method) =>
        method.Contains("Monitor.Wait", StringComparison.Ordinal) ||
        method.Contains("Monitor.Enter", StringComparison.Ordinal) ||
        method.Contains("SemaphoreSlim.Wait", StringComparison.Ordinal) ||
        method.Contains("Task.Wait", StringComparison.Ordinal) ||
        method.Contains("Task.Result", StringComparison.Ordinal) ||
        method.Contains("Thread.Sleep", StringComparison.Ordinal) ||
        method.Contains("Thread.Join", StringComparison.Ordinal) ||
        method.Contains("WaitHandle.Wait", StringComparison.Ordinal);

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 3,
        "warning" => 2,
        _ => 1,
    };

    private static string ShortName(string fullName) => FrameNames.ShortName(fullName);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
