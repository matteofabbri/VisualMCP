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

    /// <summary>Evaluate a watch expression in the stopped frame.</summary>
    Task<object> EvaluateAsync(string sessionId, string expression, int? frameId, string? context);

    /// <summary>Break on exceptions: "all", "user-unhandled" (the .NET adapter filters).</summary>
    Task<object> SetExceptionBreakpointsAsync(string sessionId, string[] filters);

    /// <summary>The console output the debuggee produced so far.</summary>
    Task<object> OutputAsync(string sessionId, int lines);

    /// <summary>End a session, terminating the debuggee.</summary>
    Task<object> TerminateAsync(string sessionId);

    /// <summary>Every live session with its state.</summary>
    Task<object> SessionsAsync();
}
