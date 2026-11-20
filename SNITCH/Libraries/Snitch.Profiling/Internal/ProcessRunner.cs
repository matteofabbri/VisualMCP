using System.Collections.Concurrent;
using System.Diagnostics;

namespace Snitch.Profiling.Internal;

/// <summary>
/// Runs an external process with a timeout and captures stdout/stderr. The child's stdin is
/// closed immediately so a tool that expects input (or a shell) cannot block on the MCP pipe.
/// </summary>
internal static class ProcessRunner
{
    public static async Task<(int ExitCode, bool TimedOut, string Stdout, string Stderr, TimeSpan Elapsed)>
        RunAsync(string exe, string arguments, string? workingDirectory, int timeoutSeconds,
                 CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };

        // Children must resolve their own SDK: an in-process MSBuild evaluation leaves
        // MSBuildSDKsPath and friends in this process's environment, and a dotnet child that
        // resolves a different SDK via global.json then fails with MSB4237.
        psi.Environment.Remove("MSBuildSDKsPath");
        psi.Environment.Remove("MSBuildExtensionsPath");
        psi.Environment.Remove("MSBuildToolsPath");
        psi.Environment.Remove("MSBUILD_EXE_PATH");

        var stdout = new ConcurrentQueue<string>();
        var stderr = new ConcurrentQueue<string>();

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.Enqueue(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.Enqueue(e.Data); };

        var sw = Stopwatch.StartNew();
        process.Start();
        try { process.StandardInput.Close(); } catch { /* best-effort */ }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600)));
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* best-effort */ }
        }
        sw.Stop();

        var outText = JoinLines(stdout);
        var errText = JoinLines(stderr);

        var exitCode = timedOut ? -1 : SafeExitCode(process);
        return (exitCode, timedOut, outText, errText, sw.Elapsed);
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; } catch { return -1; }
    }

    /// <summary>The lines as the process printed them: one newline per line, trailing newline kept.</summary>
    private static string JoinLines(ConcurrentQueue<string> lines)
    {
        if (lines.IsEmpty) return string.Empty;
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
