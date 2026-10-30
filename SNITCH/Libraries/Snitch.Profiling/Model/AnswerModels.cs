namespace Snitch.Profiling.Model;

// ---------------------------------------------------------------------------
// The answer layer: every high-level service returns one of these.
//
// The shape is deliberately the same for every question: a one-sentence
// answer, the id of the verdict that produced it, ordered evidence (each item
// carrying the numbers that prove it and the call to make next), and the
// measurements the answer was built from. A caller can read `Answer.Answer`
// alone, or walk `Answer.Evidence` in rank order.
// ---------------------------------------------------------------------------

/// <summary>
/// One piece of evidence behind an answer. <paramref name="Weight"/> is the share of the measured
/// window the item can explain (0..1 for time-based causes, documented per service); it is what the
/// rank is computed from, so a consumer can show the list and say why the first item won.
/// </summary>
public sealed record AnswerEvidence(
    int Rank,
    double Weight,
    string Id,
    string Severity,
    string Statement,
    string Evidence,
    string Recommendation,
    string? NextStep,
    string? Source);

/// <summary>The verdict envelope. Every high-level report contains one.</summary>
public sealed record AnswerReport(
    string Question,
    string Verdict,
    string Answer,
    string Confidence,
    IReadOnlyList<AnswerEvidence> Evidence,
    IReadOnlyList<string> NextSteps,
    IReadOnlyList<string> Measurements,
    IReadOnlyList<string> Warnings);

/// <summary>
/// One candidate cause, scored. The winner becomes the answer; the rest are kept so the caller can
/// see what came close and why it did not win. <c>Weight</c> is measured in the cause's own unit,
/// stated in <c>Detail</c>.
/// </summary>
public sealed record CauseScore(string Id, string Label, double Weight, string Detail);

/// <summary>Answer to "why is this process slow?": the dominant cause, the evidence for it, the runner-ups.</summary>
public sealed record SlowdownReport(
    AnswerReport Answer,
    string? TracePath,
    string? ProcessName,
    int? ProcessId,
    IReadOnlyList<CauseScore> Causes,
    TraceAnalysis? Trace,
    IoBlockReport? Io,
    StackSnapshot? Stacks,
    CounterCollectionResult? Counters);

/// <summary>
/// Answer to "why is memory growing?": either a comparison of two heap snapshots or a single
/// snapshot with the current owner named. The trace parts (GC, allocation sites) are optional and
/// only present when a gc/diagnose trace was supplied.
/// </summary>
public sealed record MemoryGrowthReport(
    AnswerReport Answer,
    string Mode,
    string? BaselinePath,
    string? CurrentPath,
    HeapSnapshot? Current,
    ProfileComparison? Comparison,
    AllocationSiteReport? Allocations,
    GcSummary? Gc);

/// <summary>Answer to "is the thread pool starved?": the state, the queue numbers and the blocking sites.</summary>
public sealed record ThreadPoolReport(
    AnswerReport Answer,
    string State,
    double? PeakQueue,
    double? LastQueue,
    double? QueueGrowth,
    double? MinWorkers,
    double? MaxWorkers,
    int BlockedThreads,
    int TotalThreads,
    long? TraceBacklog,
    IReadOnlyList<IoBlockSite> BlockingSites,
    CounterCollectionResult? Counters,
    StackSnapshot? Stacks,
    TraceAnalysis? Trace,
    IoBlockReport? Io);

/// <summary>
/// One metric of a comparison restated in a unit that is comparable across runs (per second of
/// trace, per heap). <c>Comparable</c> is false when the two runs were too different to compare.
/// </summary>
public sealed record NormalizedMetric(
    string Name,
    string Unit,
    double Baseline,
    double Current,
    double Delta,
    double Percent,
    bool Comparable);

/// <summary>Answer to "what changed since the last measurement?": a verdict on the deltas, not just the deltas.</summary>
public sealed record ChangeReviewReport(
    AnswerReport Answer,
    string Kind,
    string BaselinePath,
    string CurrentPath,
    bool Comparable,
    IReadOnlyList<NormalizedMetric> Normalized,
    IReadOnlyList<string> ComparabilityNotes,
    ProfileComparison Comparison);
