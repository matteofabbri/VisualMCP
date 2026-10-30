namespace Snitch.Debugging;

/// <summary>
/// One breakpoint as a caller describes it: a file and a line, optionally guarded by a condition
/// (a C# expression the debuggee must satisfy) or a hit count (<c>&gt;5</c>, <c>5</c>, <c>%3</c> —
/// the DAP hit-condition syntax).
/// </summary>
public sealed class BreakpointSpec
{
    /// <summary>The source file; required on a per-item basis for <c>debug_launch</c>, optional when the tool takes a file.</summary>
    public string? File { get; set; }

    /// <summary>The 1-based line.</summary>
    public int Line { get; set; }

    /// <summary>An expression that must be true for the breakpoint to stop the process.</summary>
    public string? Condition { get; set; }

    /// <summary>A DAP hit condition, e.g. <c>&gt;5</c>, <c>5</c>, <c>%3</c>.</summary>
    public string? HitCondition { get; set; }
}
