namespace Snitch.Debugging;

/// <summary>
/// The debugger: sessions over a DAP adapter (netcoredbg by default), with breakpoints, stepping,
/// stack traces, variables and expression evaluation. It is the engine half of "stop here and let
/// me look at this variable" — the client renders the answer.
/// </summary>
public interface IDebuggerService
{
    /// <summary>Whether the adapter is installed, where it is, and how to point at another build.</summary>
    Task<object> StatusAsync();

    /// <summary>Download and extract netcoredbg from the Samsung release into the tools directory.</summary>
    Task<object> InstallAsync(bool force);

    /// <summary>
    /// Start a program under the debugger. <paramref name="program"/> is a built .dll/.exe or a
    /// .csproj (the newest assembly under bin/ is used); when null, <paramref name="projectName"/>
    /// selects the project, or the solution's single runnable project is used.
    /// </summary>
    Task<object> LaunchAsync(string? program, string? projectName, string[]? programArgs,
        string? workingDirectory, string? environmentJson, bool stopAtEntry,
        BreakpointSpec[]? breakpoints, int waitForStopSeconds);

    /// <summary>Attach to an already-running process.</summary>
    Task<object> AttachAsync(int processId, int waitForStopSeconds);

    /// <summary>Replace the breakpoints of one source file (clear: remove them all).</summary>
    Task<object> SetBreakpointsAsync(string sessionId, string file, BreakpointSpec[]? breakpoints, bool clear);

    /// <summary>
    /// Replace the function breakpoints (break on a method by name). The adapter has a single
    /// function-breakpoint set, so this replaces the whole set rather than one name.
    /// </summary>
    Task<object> SetFunctionBreakpointsAsync(string sessionId, FunctionBreakpointSpec[]? breakpoints, bool clear);

    /// <summary>
    /// Break wherever the solution's source writes a member: the write sites are resolved by Roslyn
    /// and installed as line breakpoints (the adapter has no data-breakpoint request).
    /// </summary>
    Task<object> SetDataBreakpointsAsync(string sessionId, string file, int line, string member, bool clear);

    /// <summary>Continue, then wait up to <paramref name="timeoutSeconds"/> for the next stop or exit.</summary>
    Task<object> ContinueAsync(string sessionId, int? threadId, int timeoutSeconds);

    /// <summary>Step over, into or out of the current line, then wait for the next stop.</summary>
    Task<object> StepAsync(string sessionId, string kind, int? threadId, int timeoutSeconds);

    /// <summary>Break into a running process.</summary>
    Task<object> PauseAsync(string sessionId, int? threadId);

    /// <summary>The threads of the debuggee, with the stopped one marked.</summary>
    Task<object> ThreadsAsync(string sessionId);

    /// <summary>The call stack of a thread, innermost frame first, resolved to file:line.</summary>
    Task<object> StackTraceAsync(string sessionId, int? threadId, int startFrame, int levels);

    /// <summary>Locals/Autos/Arguments of a frame, with one level of object expansion.</summary>
    Task<object> VariablesAsync(string sessionId, int? frameId, string? scope, int maxVariables, int depth);

    /// <summary>
    /// Evaluate a watch expression in the stopped frame; with <paramref name="newValue"/> the
    /// expression is assigned that value first (the adapter's <c>setExpression</c>).
    /// </summary>
    Task<object> EvaluateAsync(string sessionId, string expression, int? frameId, string? context,
        string? newValue);

    /// <summary>
    /// Break on exceptions: the adapter filters are "all" (every throw) and "user-unhandled"; the
    /// optional type conditions narrow them to named exception types (<c>!Type</c> excludes one).
    /// <paramref name="details"/> reads the stopped exception instead of changing the settings.
    /// </summary>
    Task<object> SetExceptionBreakpointsAsync(string sessionId, string[]? filters, string[]? exceptionTypes,
        bool details);

    /// <summary>The console output the debuggee produced so far.</summary>
    Task<object> OutputAsync(string sessionId, int lines);

    /// <summary>End a session, terminating the debuggee.</summary>
    Task<object> TerminateAsync(string sessionId);

    /// <summary>
    /// Relaunch a launched session with the same program, arguments, environment and breakpoints —
    /// optionally rebuilding its project first. A new session id replaces the old one.
    /// </summary>
    Task<object> RestartAsync(string sessionId, bool rebuild);

    /// <summary>Every live session with its state.</summary>
    Task<object> SessionsAsync();
}
