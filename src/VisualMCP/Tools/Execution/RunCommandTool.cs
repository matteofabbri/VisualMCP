using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class RunCommandTool
{
    [McpServerTool, Description(
        "Run a shell command or script and return its stdout, stderr, exit code and duration. " +
        "Use this for build/export/packaging scripts and multi-step commands — including PowerShell that " +
        "spawns nested PowerShell, uses pipes, '&' call operators or redirection — that a sandboxed shell " +
        "refuses to validate. The command text is written to a temporary script file and executed by the " +
        "chosen shell (PowerShell by default), so multi-line input, quotes and nested processes work as-is. " +
        "Runs locally with the server's privileges; it enforces a timeout and then kills the whole process " +
        "tree, returning whatever output was produced.")]
    public static async Task<object> RunCommand(
        [Description("The command or script text to execute. May span multiple lines.")] string command,
        [Description("Shell to run it in: 'powershell' (Windows PowerShell, default), 'pwsh' (PowerShell 7+), or 'cmd'.")] string shell = "powershell",
        [Description("Working directory. Defaults to the loaded solution's directory, or the server's current directory.")] string? workingDirectory = null,
        [Description("Timeout in seconds before the process tree is killed (default: 120, max: 3600).")] int timeoutSeconds = 120)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new { error = "No command provided." };

        if (timeoutSeconds < 1) timeoutSeconds = 1;
        if (timeoutSeconds > 3600) timeoutSeconds = 3600;

        // Resolve working directory: explicit > loaded solution dir > current dir.
        string workDir;
        if (workingDirectory is not null)
        {
            if (!Directory.Exists(workingDirectory))
                return new { error = $"Working directory not found: {workingDirectory}" };
            workDir = Path.GetFullPath(workingDirectory);
        }
        else
        {
            var slnPath = RoslynWorkspaceService.Instance.LoadedSolutionPath;
            workDir = slnPath is not null
                ? Path.GetDirectoryName(slnPath)!
                : Directory.GetCurrentDirectory();
        }

        var kind = shell.Trim().ToLowerInvariant();
        if (kind is not ("powershell" or "pwsh" or "cmd"))
            return new { error = $"Unsupported shell '{shell}'. Use 'powershell', 'pwsh' or 'cmd'." };

        // Write the command to a temp script so quoting / multi-line / nested
        // processes are passed through verbatim, then invoke it by file path.
        var ext = kind == "cmd" ? ".cmd" : ".ps1";
        var scriptPath = Path.Combine(Path.GetTempPath(), $"visualmcp-cmd-{Guid.NewGuid():N}{ext}");
        // UTF-8 with BOM so Windows PowerShell 5.1 reads non-ASCII correctly.
        await File.WriteAllTextAsync(scriptPath, command, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        try
        {
            var (exe, args) = kind switch
            {
                "cmd"  => ("cmd.exe", $"/c \"{scriptPath}\""),
                "pwsh" => ("pwsh.exe", $"-ExecutionPolicy Bypass -NoProfile -NonInteractive -File \"{scriptPath}\""),
                _      => ("powershell.exe", $"-ExecutionPolicy Bypass -NoProfile -NonInteractive -File \"{scriptPath}\""),
            };

            var (exitCode, timedOut, stdout, stderr, elapsed) =
                await ProcessRunner.RunAsync(exe, args, workDir, timeoutSeconds);

            return new
            {
                shell = kind,
                workingDirectory = workDir,
                timedOut,
                exitCode = timedOut ? (int?)null : exitCode,
                durationMs = (long)elapsed.TotalMilliseconds,
                note = timedOut
                    ? $"Command was still running after {timeoutSeconds}s and was stopped. Output below is what it produced so far."
                    : (exitCode == 0 ? "Command completed successfully." : $"Command exited with code {exitCode}."),
                stdout = ProcessRunner.Truncate(stdout),
                stderr = ProcessRunner.Truncate(stderr),
            };
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best-effort */ }
        }
    }
}
