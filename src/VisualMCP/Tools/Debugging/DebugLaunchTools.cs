using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Debugging;

namespace VisualMCP.Tools.Debugging;

[McpServerToolType]
public static class DebugLaunchTools
{
    [McpServerTool(Name = "debug_launch"), Description(
        "Start a program under the debugger and return the session: its id, state, stop location and console " +
        "output. program is a BUILT .dll/.exe (or a .csproj: the newest assembly under bin/ is used); omit it and " +
        "pass projectName, or nothing to use the solution's single runnable project. Breakpoints can be set in " +
        "the same call (each {file, line, condition?, hitCondition?}), which is the normal way to stop at a line " +
        "the process reaches immediately; otherwise call debug_breakpoints first. The call waits up to " +
        "waitForStopSeconds for the first stop, so a hit breakpoint comes back as state=stopped with " +
        "stopLocation=file:line. Requires debug_install once. Sessions stay alive across calls: this is a real " +
        "debugger, not a run.")]
    public static Task<object> DebugLaunch(
        IDebuggerService debugger,
        [Description("Built program: path to a .dll/.exe, or to a .csproj (the newest bin/ assembly is used). " +
                     "Omit to resolve from projectName or the single runnable project.")] string? program = null,
        [Description("Solution project to debug, when program is not given.")] string? projectName = null,
        [Description("Command-line arguments for the debuggee.")] string[]? programArgs = null,
        [Description("Working directory of the debuggee (default: the program's folder).")] string? workingDirectory = null,
        [Description("Environment variables as a JSON object of strings, e.g. {\"ASPNETCORE_ENVIRONMENT\":\"Development\"}.")] string? environmentJson = null,
        [Description("Stop at the entry point (default: false).")] bool stopAtEntry = false,
        [Description("Breakpoints to set during startup: array of {file, line, condition?, hitCondition?}. " +
                     "file is absolute or relative to the solution.")] BreakpointSpec[]? breakpoints = null,
        [Description("Seconds to wait for the first stop before returning state=running (default 15; 0 = do not wait).")] int waitForStopSeconds = 15)
        => debugger.LaunchAsync(program, projectName, programArgs, workingDirectory, environmentJson,
            stopAtEntry, breakpoints, waitForStopSeconds);

    [McpServerTool(Name = "debug_attach"), Description(
        "Attach the debugger to an already-running .NET process (profile_processes lists them with their pid). " +
        "Returns the session; set breakpoints afterwards with debug_breakpoints, then debug_continue or " +
        "debug_pause. The call waits up to waitForStopSeconds for a stop.")]
    public static Task<object> DebugAttach(
        IDebuggerService debugger,
        [Description("Process id to attach to.")] int processId,
        [Description("Seconds to wait for the first stop (default 5; 0 = do not wait).")] int waitForStopSeconds = 5)
        => debugger.AttachAsync(processId, waitForStopSeconds);
}
