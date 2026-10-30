using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Debugging;

namespace VisualMCP.Tools.Debugging;

[McpServerToolType]
public static class DebugControlTools
{
    [McpServerTool(Name = "debug_breakpoints"), Description(
        "Set the breakpoints of ONE source file (the list replaces the file's previous set; clear=true removes " +
        "them). Each breakpoint is {line, condition?, hitCondition?}; a condition is a C# expression that must be " +
        "true to stop, a hitCondition follows the DAP syntax (>5, 5, %3). Returns which breakpoints the runtime " +
        "VERIFIED and which it could not bind, with the adapter's message — an unverified line (a blank line, a " +
        "line with no code) never stops and is reported here. Can be called on a running process.")]
    public static Task<object> DebugBreakpoints(
        IDebuggerService debugger,
        [Description("Session id from debug_launch/debug_attach.")] string sessionId,
        [Description("Source file: absolute, or relative to the solution directory.")] string file,
        [Description("Breakpoints for that file: array of {line, condition?, hitCondition?}. Omit with clear=true to remove them.")] BreakpointSpec[]? breakpoints = null,
        [Description("Remove every breakpoint of the file (default: false).")] bool clear = false)
        => debugger.SetBreakpointsAsync(sessionId, file, breakpoints, clear);

    [McpServerTool(Name = "debug_continue"), Description(
        "Continue a stopped session and wait up to timeoutSeconds for the next stop, breakpoint hit or the " +
        "process exit. Returns the session state: state=stopped comes with stopReason (breakpoint/step/pause/" +
        "exception) and stopLocation=file:line; state=exited means the program finished (debug_output has its " +
        "console output). This is how a debugging turn advances.")]
    public static Task<object> DebugContinue(
        IDebuggerService debugger,
        [Description("Session id.")] string sessionId,
        [Description("Thread to continue (default: the stopped thread).")] int? threadId = null,
        [Description("Seconds to wait for the next stop (default 30).")] int timeoutSeconds = 30)
        => debugger.ContinueAsync(sessionId, threadId, timeoutSeconds);

    [McpServerTool(Name = "debug_step"), Description(
        "Step the stopped thread and wait for the next stop: kind=over (next line), into (enter the call) or " +
        "out (finish the current method). Returns the same session shape as debug_continue, with the new " +
        "stopLocation.")]
    public static Task<object> DebugStep(
        IDebuggerService debugger,
        [Description("Session id.")] string sessionId,
        [Description("over | into | out")] string kind,
        [Description("Thread to step (default: the stopped thread).")] int? threadId = null,
        [Description("Seconds to wait for the next stop (default 15).")] int timeoutSeconds = 15)
        => debugger.StepAsync(sessionId, kind, threadId, timeoutSeconds);

    [McpServerTool(Name = "debug_pause"), Description(
        "Break into a running session (the debugger's pause button). Returns the session with the thread that " +
        "stopped and where.")]
    public static Task<object> DebugPause(
        IDebuggerService debugger,
        [Description("Session id.")] string sessionId,
        [Description("Thread to pause (default: the adapter picks one).")] int? threadId = null)
        => debugger.PauseAsync(sessionId, threadId);

    [McpServerTool(Name = "debug_exceptions"), Description(
        "Set the exception breakpoints of a session: filters 'all' (break on every thrown exception) or " +
        "'user-unhandled' (the .NET adapter's default behaviour). Returns which filters the adapter verified.")]
    public static Task<object> DebugExceptions(
        IDebuggerService debugger,
        [Description("Session id.")] string sessionId,
        [Description("Filters: all, user-unhandled. E.g. [\"all\"].")] string[] filters)
        => debugger.SetExceptionBreakpointsAsync(sessionId, filters);

    [McpServerTool(Name = "debug_output"), Description(
        "The console output the debuggee produced so far (stdout/stderr as the debuggee wrote them), plus the " +
        "adapter's own stderr when the session failed. Read-only.")]
    public static Task<object> DebugOutput(
        IDebuggerService debugger,
        [Description("Session id.")] string sessionId,
        [Description("How many trailing lines (default 100, max 500).")] int lines = 100)
        => debugger.OutputAsync(sessionId, lines);

    [McpServerTool(Name = "debug_terminate"), Description(
        "End a debug session and terminate the debuggee. Returns its last state and output. Use it when the " +
        "process is still running or has exited but the session is no longer needed.")]
    public static Task<object> DebugTerminate(
        IDebuggerService debugger,
        [Description("Session id.")] string sessionId)
        => debugger.TerminateAsync(sessionId);

    [McpServerTool(Name = "debug_sessions"), Description(
        "Every live debug session: id, program, process id, state (running/stopped/exited), the stop location " +
        "and the files with breakpoints. Start here when a session id was lost. Read-only.")]
    public static Task<object> DebugSessions(IDebuggerService debugger) => debugger.SessionsAsync();
}
