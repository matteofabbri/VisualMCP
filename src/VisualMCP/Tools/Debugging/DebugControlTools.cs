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
        "true to stop, a hitCondition is the DAP hit count (>5, >=5, 5 exactly, %3 every third hit) which this " +
        "server counts and enforces — the adapter has no hit counter, so a stop that does not satisfy it is " +
        "resumed transparently and reported as hitConditionSkips. Returns which breakpoints the runtime VERIFIED " +
        "and which it could not bind, with the adapter's message — an unverified line (a blank line, a line with " +
        "no code) never stops and is reported here. Can be called on a running process.")]
    public static Task<object> DebugBreakpoints(
        IDebuggerService debugger,
        [Description("Session id from debug_launch/debug_attach.")] string sessionId,
        [Description("Source file: absolute, or relative to the solution directory.")] string file,
        [Description("Breakpoints for that file: array of {line, condition?, hitCondition?}. Omit with clear=true to remove them.")] BreakpointSpec[]? breakpoints = null,
        [Description("Remove every breakpoint of the file (default: false).")] bool clear = false)
        => debugger.SetBreakpointsAsync(sessionId, file, breakpoints, clear);

    [McpServerTool(Name = "debug_function_breakpoints"), Description(
        "Break on a METHOD by name instead of by file and line — the breakpoint that survives an edited file and " +
        "works in code you did not open. Each item is {name, condition?}, where name is the adapter's function name: " +
        "'Namespace.Type.Method', 'Type.Method', 'Type.Method(int)' or 'Module.dll!Type.Method'. The list REPLACES the " +
        "session's whole function-breakpoint set (clear=true empties it). A method the runtime has not loaded yet " +
        "comes back unverified and binds when its module loads. Use debug_breakpoints for source lines.")]
    public static Task<object> DebugFunctionBreakpoints(
        IDebuggerService debugger,
        [Description("Session id.")] string sessionId,
        [Description("Function breakpoints: array of {name, condition?}.")] FunctionBreakpointSpec[]? breakpoints = null,
        [Description("Remove every function breakpoint (default: false).")] bool clear = false)
        => debugger.SetFunctionBreakpointsAsync(sessionId, breakpoints, clear);

    [McpServerTool(Name = "debug_data_breakpoints"), Description(
        "Break where a FIELD OR PROPERTY IS WRITTEN — the data breakpoint. The adapter cannot watch memory, so this " +
        "resolves the member through Roslyn (at file:line, named as it appears in the source) and turns every write " +
        "site in the solution into a line breakpoint: assignments, compound assignments, ++/--, ref/out arguments " +
        "and an initializer. The answer lists each site with its kind, so it is visible where the stop will happen. " +
        "clear=true removes the data breakpoint by member and restores the file's ordinary breakpoints. A local " +
        "variable is refused (it lives in one frame; break on its line instead) and so is a write from code outside " +
        "the solution's source.")]
    public static Task<object> DebugDataBreakpoints(
        IDebuggerService debugger,
        [Description("Session id.")] string sessionId,
        [Description("Source file where the member is declared or used: absolute, or relative to the solution.")] string file,
        [Description("The 1-based line on which the member name appears.")] int line,
        [Description("The field or property name, exactly as written.")] string member,
        [Description("Remove the data breakpoint on that member instead of installing it (default: false).")] bool clear = false)
        => debugger.SetDataBreakpointsAsync(sessionId, file, line, member, clear);

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
        "Exception breakpoints, per type. exceptionTypes names the types to break on and is evaluated against the " +
        "thrown type's HIERARCHY through the loaded solution, so a base class matches its derived types and a " +
        "leading ! excludes a whole branch (with no positive type, '!System.OperationCanceledException' breaks on " +
        "everything else). To judge them the adapter is set to break on every throw and the stops that do not match " +
        "are resumed transparently (exceptionTypeSkips counts them). filters picks WHEN to break when no type is " +
        "named — 'all' (every throw) or 'user-unhandled' (the .NET adapter's default). Without filters or " +
        "exceptionTypes the call answers what the process is stopped on: the exception id, the stage (throw / " +
        "user-unhandled / unhandled), its type, message and details, including inner exceptions — the same block a " +
        "stop on an exception returns automatically.")]
    public static Task<object> DebugExceptions(
        IDebuggerService debugger,
        [Description("Session id.")] string sessionId,
        [Description("Filters: all, user-unhandled. Omit to only read the stopped exception.")] string[]? filters = null,
        [Description("Optional exception type names, full (System.NullReferenceException) or simple; a base class " +
                     "matches its derived types. Prefix ! to exclude one.")] string[]? exceptionTypes = null,
        [Description("Also read the stopped exception's details (default: false; implied when no setting is given).")] bool details = false)
        => debugger.SetExceptionBreakpointsAsync(sessionId, filters, exceptionTypes, details);

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

    [McpServerTool(Name = "debug_restart"), Description(
        "Relaunch a LAUNCHED session: the same program, arguments and environment, the same line breakpoints, " +
        "function breakpoints and exception settings, with a new session id (the old one is gone). This is the " +
        "edit → build → run loop the adapter can offer instead of edit-and-continue: pass rebuild=true to run " +
        "'dotnet build' on the project that produced the assembly first, and a failed build leaves the session " +
        "running untouched. An attached session cannot be restarted (there is no launcher to repeat).")]
    public static Task<object> DebugRestart(
        IDebuggerService debugger,
        [Description("Session id of a session started by debug_launch.")] string sessionId,
        [Description("Rebuild the project that produced the assembly before relaunching (default: false).")] bool rebuild = false)
        => debugger.RestartAsync(sessionId, rebuild);
}
