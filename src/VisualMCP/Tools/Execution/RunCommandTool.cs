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
        "spawns nested PowerShell, bash with grep/sed pipelines, '&' call operators, redirection, or very " +
        "long commands — that a sandboxed shell refuses to validate or rejects as too long. The command " +
        "text is written to a temporary script file and executed by the chosen shell (PowerShell by " +
        "default), so multi-line input, quotes, escapes and nested processes work as-is, with no length limit. " +
        "Runs locally with the server's privileges; it enforces a timeout and then kills the whole process " +
        "tree, returning whatever output was produced.")]
    public static async Task<object> RunCommand(
        [Description("The command or script text to execute. May span multiple lines; no length limit.")] string command,
        [Description("Shell to run it in: 'powershell' (Windows PowerShell, default), 'pwsh' (PowerShell 7+), 'cmd', or 'bash'.")] string shell = "powershell",
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
        if (kind is not ("powershell" or "pwsh" or "cmd" or "bash"))
            return new { error = $"Unsupported shell '{shell}'. Use 'powershell', 'pwsh', 'cmd' or 'bash'." };

        // bash needs UTF-8 WITHOUT a BOM (a BOM breaks the shebang/first line);
        // Windows PowerShell 5.1 needs the BOM to read non-ASCII correctly.
        var ext = kind switch { "cmd" => ".cmd", "bash" => ".sh", _ => ".ps1" };
        var scriptPath = Path.Combine(Path.GetTempPath(), $"visualmcp-cmd-{Guid.NewGuid():N}{ext}");
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: kind != "bash");
        await File.WriteAllTextAsync(scriptPath, command, encoding);

        try
        {
            string exe, args;
            switch (kind)
            {
                case "cmd":
                    exe = "cmd.exe"; args = $"/c \"{scriptPath}\""; break;
                case "pwsh":
                    exe = "pwsh.exe"; args = $"-ExecutionPolicy Bypass -NoProfile -NonInteractive -File \"{scriptPath}\""; break;
                case "bash":
                    var bash = ResolveBash();
                    if (bash is null) return new { error = "bash was not found. Install Git for Windows or add bash to PATH." };
                    exe = bash;
                    // Forward slashes so git-bash accepts the Windows temp path.
                    args = $"\"{scriptPath.Replace('\\', '/')}\""; break;
                default:
                    exe = "powershell.exe"; args = $"-ExecutionPolicy Bypass -NoProfile -NonInteractive -File \"{scriptPath}\""; break;
            }

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

    /// <summary>Locates a bash executable: common Git-for-Windows paths, then PATH.</summary>
    private static string? ResolveBash()
    {
        string[] known =
        {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files\Git\usr\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
        };
        foreach (var p in known)
            if (File.Exists(p)) return p;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "bash.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }
}
