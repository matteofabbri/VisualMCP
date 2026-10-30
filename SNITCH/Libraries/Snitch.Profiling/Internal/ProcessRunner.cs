using System.Diagnostics;
using System.Text;

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

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

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

        string outText, errText;
        lock (stdout) outText = stdout.ToString();
        lock (stderr) errText = stderr.ToString();

        var exitCode = timedOut ? -1 : SafeExitCode(process);
        return (exitCode, timedOut, outText, errText, sw.Elapsed);
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; } catch { return -1; }
    }
}
