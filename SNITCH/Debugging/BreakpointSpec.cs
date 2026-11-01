namespace Snitch.Debugging;

/// <summary>
/// One breakpoint as a caller describes it: a file and a line, optionally guarded by a condition
/// (a C# expression the debuggee must satisfy) or a hit count (<c>&gt;5</c>, <c>5</c>, <c>%3</c> —
/// the DAP hit-condition syntax, counted by this server because the adapter has no hit counter).
/// </summary>
public sealed class BreakpointSpec
{
    /// <summary>The source file; required on a per-item basis for <c>debug_launch</c>, optional when the tool takes a file.</summary>
    public string? File { get; set; }

    /// <summary>The 1-based line.</summary>
    public int Line { get; set; }

    /// <summary>An expression that must be true for the breakpoint to stop the process.</summary>
    public string? Condition { get; set; }

    /// <summary>A DAP hit condition, e.g. <c>&gt;5</c>, <c>5</c>, <c>%3</c>, enforced here per hit count.</summary>
    public string? HitCondition { get; set; }
}

/// <summary>
/// A breakpoint on a method by name rather than by file and line: the DAP function-breakpoint
/// shape. <see cref="Name"/> accepts the adapter's forms — <c>Namespace.Type.Method</c>,
/// <c>Type.Method</c>, optionally <c>Module.dll!Type.Method</c> and with a parameter list.
/// </summary>
public sealed class FunctionBreakpointSpec
{
    /// <summary>The method name; <c>Module.dll!Ns.Type.Method(int)</c> is the fully qualified form.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>An expression that must be true for the breakpoint to stop the process.</summary>
    public string? Condition { get; set; }
}
