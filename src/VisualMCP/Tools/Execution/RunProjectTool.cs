using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class RunProjectTool
{
    [McpServerTool, Description(
        "When you need to actually run a project (launch the app), use this to execute 'dotnet run' " +
        "on a runnable project in the loaded solution and capture its output, exit code and duration. " +
        "It enforces a timeout and then stops the process, so long-running apps (e.g. a web server or a " +
        "service) return their startup output (such as the listening URL) instead of blocking forever. " +
        "If no project name is given it auto-selects the single executable project, or lists the options " +
        "when there is more than one. The working-directory solution auto-loads on first use.")]
    public static async Task<object> RunProject(
        [Description("Optional: name of the project to run. If omitted, the single runnable (executable) project is used.")] string? projectName = null,
        [Description("Optional: arguments to pass to the application itself (forwarded after '--').")] string? appArgs = null,
        [Description("Seconds to let the app run before it is stopped and output is returned (default: 20). For a one-shot console app that exits on its own, it returns as soon as it exits.")] int timeoutSeconds = 20,
        [Description("Build configuration to run (e.g. 'Debug' or 'Release'). Omit for the dotnet default (Debug).")] string? configuration = null,
        [Description("Skip building before running (pass --no-build). Default: false.")] bool noBuild = false)
    {
        var service  = RoslynWorkspaceService.Instance;
        var solution = await service.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        // Resolve the target project.
        Project? target;
        if (projectName is not null)
        {
            target = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (target is null)
                return new { error = $"Project '{projectName}' not found in the solution." };
        }
        else
        {
            var runnable = solution.Projects.Where(IsRunnable).Select(p => p.Name).ToList();
            if (runnable.Count == 0)
                return new { error = "No runnable (executable) project found in the solution. Specify projectName explicitly." };
            if (runnable.Count > 1)
                return new { error = $"Multiple runnable projects found; specify projectName. Candidates: {string.Join(", ", runnable)}" };
            target = solution.Projects.First(p => p.Name == runnable[0]);
        }

        var projectPath = target.FilePath;
        if (projectPath is null || !File.Exists(projectPath))
            return new { error = $"Project file not found on disk for '{target.Name}'." };

        if (timeoutSeconds < 1) timeoutSeconds = 1;
        if (timeoutSeconds > 300) timeoutSeconds = 300;

        // Build the dotnet run command line.
        var parts = new List<string> { "run", "--project", $"\"{projectPath}\"" };
        if (configuration is not null) { parts.Add("-c"); parts.Add(configuration); }
        if (noBuild) parts.Add("--no-build");
        if (!string.IsNullOrWhiteSpace(appArgs)) { parts.Add("--"); parts.Add(appArgs); }
        var args = string.Join(" ", parts);

        var (exitCode, timedOut, stdout, stderr, elapsed) =
            await ExecWithTimeoutAsync("dotnet", args, Path.GetDirectoryName(projectPath)!, timeoutSeconds);

        return new
        {
            project   = target.Name,
            projectPath,
            command   = $"dotnet {args}",
            timedOut,                                   // true => app was still running and was stopped
            exitCode  = timedOut ? (int?)null : exitCode,
            durationMs = (long)elapsed.TotalMilliseconds,
            note = timedOut
                ? $"App was still running after {timeoutSeconds}s and was stopped. Output below is what it produced so far."
                : (exitCode == 0 ? "App exited normally." : $"App exited with code {exitCode}."),
            stdout = Truncate(stdout),
            stderr = Truncate(stderr),
        };
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static bool IsRunnable(Project p) =>
        p.CompilationOptions?.OutputKind is OutputKind.ConsoleApplication or OutputKind.WindowsApplication;

    private static string Truncate(string s, int max = 16000) =>
        s.Length <= max ? s : s[..max] + $"\n…(truncated, {s.Length - max} more chars)";

    private static async Task<(int exitCode, bool timedOut, string stdout, string stderr, TimeSpan elapsed)>
        ExecWithTimeoutAsync(string exe, string args, string workingDir, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = workingDir,
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        var sw = Stopwatch.StartNew();
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { await proc.WaitForExitAsync(); } catch { /* best-effort */ }
        }
        sw.Stop();

        // Ensure buffered async output is flushed.
        try { proc.WaitForExit(500); } catch { /* best-effort */ }

        string outStr, errStr;
        lock (stdout) outStr = stdout.ToString();
        lock (stderr) errStr = stderr.ToString();

        var exitCode = timedOut ? -1 : SafeExitCode(proc);
        return (exitCode, timedOut, outStr, errStr, sw.Elapsed);
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }
}
