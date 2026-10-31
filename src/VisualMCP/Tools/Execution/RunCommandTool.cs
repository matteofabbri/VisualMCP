using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Execution;
using Snitch.Platform;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class RunCommandTool
{
    [McpServerTool(Name = "run_command"), Description(
        "Run a shell command or script and return its stdout, stderr, exit code and duration. " +
        "Use this for build/export/packaging scripts and multi-step commands — including PowerShell that " +
        "spawns nested PowerShell, bash with grep/sed pipelines, '&' call operators, redirection, or very " +
        "long commands — that a sandboxed shell refuses to validate or rejects as too long. The command " +
        "text is written to a temporary script file and executed by the chosen shell (PowerShell by default " +
        "on Windows, bash elsewhere), so multi-line input, quotes, escapes and nested processes work as-is, " +
        "with no length limit. Runs locally with the server's privileges; it enforces a timeout and then " +
        "kills the whole process tree, returning whatever output was produced.")]
    public static Task<object> RunCommand(
        IRunCommandService cmd,
        [Description("The command or script text to execute. May span multiple lines; no length limit.")] string command,
        [Description("Shell to run it in: 'powershell' (Windows PowerShell), 'pwsh' (PowerShell 7+), 'cmd' (Windows only), or 'bash'. Default: 'powershell' on Windows, 'bash' elsewhere.")] string? shell = null,
        [Description("Working directory. Defaults to the loaded solution's directory, or the server's current directory.")] string? workingDirectory = null,
        [Description("Timeout in seconds before the process tree is killed (default: 120, max: 3600).")] int timeoutSeconds = 120)
        => cmd.RunAsync(command, shell ?? HostSystem.DefaultShell, workingDirectory, timeoutSeconds);
}