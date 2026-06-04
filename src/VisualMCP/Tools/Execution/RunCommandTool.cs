using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Execution;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class RunCommandTool
{
    [McpServerTool(Name = "run_command"), Description(
        "Run a shell command or script and return its stdout, stderr, exit code and duration. " +
        "Use this for build/export/packaging scripts and multi-step commands — including PowerShell that " +
        "spawns nested PowerShell, bash with grep/sed pipelines, '&' call operators, redirection, or very " +
        "long commands — that a sandboxed shell refuses to validate or rejects as too long. The command " +
        "text is written to a temporary script file and executed by the chosen shell (PowerShell by " +
        "default), so multi-line input, quotes, escapes and nested processes work as-is, with no length limit. " +
        "Runs locally with the server's privileges; it enforces a timeout and then kills the whole process " +
        "tree, returning whatever output was produced.")]
    public static Task<object> RunCommand(
        [Description("The command or script text to execute. May span multiple lines; no length limit.")] string command,
        [Description("Shell to run it in: 'powershell' (Windows PowerShell, default), 'pwsh' (PowerShell 7+), 'cmd', or 'bash'.")] string shell = "powershell",
        [Description("Working directory. Defaults to the loaded solution's directory, or the server's current directory.")] string? workingDirectory = null,
        [Description("Timeout in seconds before the process tree is killed (default: 120, max: 3600).")] int timeoutSeconds = 120)
        => RunCommandImpl.RunAsync(command, shell, workingDirectory, timeoutSeconds);
}
