using System.Text;
using Snitch.Platform;
using Snitch.Workspace;

namespace Snitch.Execution;

public interface IRunCommandService
{
    Task<object> RunAsync(string command, string shell, string? workingDirectory, int timeoutSeconds);
}

public sealed class RunCommandService : IRunCommandService
{
    private readonly IRoslynWorkspace _workspace;
    private readonly SnitchOptions _options;

    public RunCommandService(IRoslynWorkspace workspace, SnitchOptions options)
    {
        _workspace = workspace;
        _options = options;
    }

    public async Task<object> RunAsync(string command, string shell, string? workingDirectory, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new { error = "No command provided." };

        if (timeoutSeconds < 1) timeoutSeconds = 1;
        if (timeoutSeconds > 3600) timeoutSeconds = 3600;

        string workDir;
        if (workingDirectory is not null)
        {
            if (!Directory.Exists(workingDirectory))
                return new { error = $"Working directory not found: {workingDirectory}" };
            workDir = Path.GetFullPath(workingDirectory);
        }
        else
        {
            var slnPath = _workspace.LoadedSolutionPath;
            workDir = slnPath is not null ? Path.GetDirectoryName(slnPath)! : Directory.GetCurrentDirectory();
        }

        var kind = string.IsNullOrWhiteSpace(shell) ? HostSystem.DefaultShell : shell.Trim().ToLowerInvariant();
        if (kind is not ("powershell" or "pwsh" or "cmd" or "bash"))
            return new { error = $"Unsupported shell '{shell}'. Use 'powershell', 'pwsh', 'cmd' or 'bash'." };

        // cmd.exe and Windows PowerShell exist only on Windows; refuse them up front
        // instead of failing at spawn time with an opaque "file not found".
        if (kind is "cmd" or "powershell" && !HostSystem.IsWindows)
            return new { error = $"The '{kind}' shell exists only on Windows. On this platform use 'bash' (the default) or 'pwsh'." };

        var ext = kind switch { "cmd" => ".cmd", "bash" => ".sh", _ => ".ps1" };
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{_options.TempFilePrefix}-cmd-{Guid.NewGuid():N}{ext}");
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
                    exe = HostSystem.IsWindows ? "pwsh.exe" : "pwsh";
                    args = $"-ExecutionPolicy Bypass -NoProfile -NonInteractive -File \"{scriptPath}\""; break;
                case "bash":
                    var bash = ResolveBash();
                    if (bash is null)
                        return new { error = HostSystem.IsWindows
                            ? "bash was not found. Install Git for Windows or add bash to PATH."
                            : "bash was not found. Install bash or add its directory to PATH." };
                    exe = bash;
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

    private static string? ResolveBash()
    {
        // Git for Windows installs bash in a well-known place; on the other
        // platforms it is a system binary with a fixed location.
        string[] known = HostSystem.IsWindows
            ?
            [
                @"C:\Program Files\Git\bin\bash.exe",
                @"C:\Program Files\Git\usr\bin\bash.exe",
                @"C:\Program Files (x86)\Git\bin\bash.exe",
            ]
            :
            [
                "/bin/bash",
                "/usr/bin/bash",
                "/usr/local/bin/bash",
            ];
        foreach (var p in known)
            if (File.Exists(p)) return p;

        var fileName = HostSystem.IsWindows ? "bash.exe" : "bash";
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var candidate = Path.Combine(dir.Trim(), fileName); if (File.Exists(candidate)) return candidate; }
            catch { }
        }
        return null;
    }
}
