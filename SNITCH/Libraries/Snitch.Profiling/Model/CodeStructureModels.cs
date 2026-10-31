namespace Snitch.Profiling.Model;

// ---------------------------------------------------------------------------
// The code model a profiler answer is joined with
// ---------------------------------------------------------------------------

/// <summary>
/// One loop of a declaration as the language-neutral AST models it: the kind of loop, the span it
/// covers, how many loops enclose it (<see cref="Depth"/> 1 is the outermost) and the source header
/// (<c>foreach (var order in orders)</c>) so a report can quote the line the reader has to open.
/// </summary>
public sealed record CodeLoopInfo(
    string Kind,
    int Line,
    int EndLine,
    int Depth,
    string? Header);

/// <summary>
/// One construct that a profiler measurement can be attributed to. <see cref="Kind"/> is the model's
/// vocabulary, not an analyzer id: <c>loop</c>, <c>linq</c>, <c>allocation</c>, <c>string-concat</c>,
/// <c>await</c>, <c>lock</c>, <c>sync-over-async</c>, <c>throw</c>. <see cref="LoopDepth"/> is 0 when
/// the construct is outside every loop, which is what tells a per-call cost from a per-iteration one.
/// </summary>
public sealed record CodeConstructInfo(
    string Kind,
    string Detail,
    int Line,
    int EndLine,
    int LoopDepth,
    string? LoopHeader)
{
    public bool IsInsideLoop => LoopDepth > 0;
}

/// <summary>
/// One callable of a source file as the AST sees it: its span, the complexity the language-neutral
/// analysis gives it, its loops and the constructs that cost something at run time.
/// </summary>
public sealed record CodeDeclarationInfo(
    string Name,
    string Kind,
    string File,
    int Line,
    int EndLine,
    int Cyclomatic,
    int Cognitive,
    int MaxNesting,
    int Statements,
    IReadOnlyList<CodeLoopInfo> Loops,
    IReadOnlyList<CodeConstructInfo> Constructs)
{
    public bool Contains(int line) => Line > 0 && EndLine >= line && line >= Line;
}

/// <summary>
/// A source file as the code-structure index describes it. <see cref="Error"/> is set when the file
/// could not be read (a deleted file, a language no frontend reads): the profiling answer then keeps
/// the measured line and says the structure is missing instead of inventing one.
/// </summary>
public sealed record CodeFileStructure(
    string File,
    IReadOnlyList<CodeDeclarationInfo> Declarations,
    string? Error = null);

/// <summary>
/// The join the profiler needs: given a file, the declarations the AST holds for it. Implemented
/// over the solution's metacode (on demand or from its persisted index); a test can supply a fake.
/// </summary>
public interface ICodeStructureIndex
{
    CodeFileStructure Describe(string file);
}

// ---------------------------------------------------------------------------
// Root-cause report
// ---------------------------------------------------------------------------

/// <summary>
/// One measured site joined with the code that produced it. <see cref="Metric"/> is
/// <c>cpu</c>, <c>allocations</c>, <c>blocked</c> or <c>exceptions</c>; <see cref="Subject"/> names
/// what was measured (the allocated type, the exception type) when that is not the site itself.
/// <see cref="Declaration"/> is the AST declaration that contains the site, if one was found, and
/// <see cref="Constructs"/> are the parts of that declaration the measurement lands on — the loop
/// chain around the line, the LINQ call materialised per iteration, the allocation inside a loop.
/// </summary>
public sealed record CodeHotSite(
    string Metric,
    string Method,
    string? Source,
    string? File,
    int? Line,
    string? Subject,
    double Cost,
    string Unit,
    double Share,
    CodeDeclarationInfo? Declaration,
    IReadOnlyList<CodeConstructInfo> Constructs,
    string Explanation);

/// <summary>
/// The answer to "which code creates the problem?": the measured sites of a trace, each joined with
/// the metacode AST of the declaration that contains it, so a sampled line becomes a construct —
/// an allocation inside two nested loops, a LINQ materialiser per iteration, a synchronous wait in a
/// loop. <see cref="StructureAvailable"/> is false when no AST could be read for any hot file; the
/// measured half is still reported, with a warning that says why the static half is missing.
/// </summary>
public sealed record RootCauseReport(
    string TracePath,
    double DurationMs,
    double CpuMs,
    double BlockedMs,
    long SampledAllocationBytes,
    long ExceptionCount,
    bool StructureAvailable,
    IReadOnlyList<CodeHotSite> Sites,
    IReadOnlyList<ProfilingFinding> Findings,
    IReadOnlyList<string> NextSteps,
    IReadOnlyList<string> Warnings);
