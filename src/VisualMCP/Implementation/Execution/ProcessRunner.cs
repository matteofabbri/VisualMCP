using System.Diagnostics;
using System.Text;

namespace VisualMCP.Implementation.Execution;

/// <summary>Shared helper for running external processes with a timeout.</summary>
internal static class ProcessRunner
{
    internal static async Task<(int exitCode, bool timedOut, string stdout, string stderr, TimeSpan elapsed)>
        RunAsync(string exe, string args, string workingDir, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,   // don't let children inherit the MCP stdio pipe
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
        // Give the child an immediate EOF on stdin instead of the inherited
        // server pipe — otherwise shells like bash block waiting for input.
        try { proc.StandardInput.Close(); } catch { /* best-effort */ }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { await proc.WaitForExitAsync();      } catch { /* best-effort */ }
        }
        sw.Stop();

        try { proc.WaitForExit(500); } catch { /* best-effort */ }

        string outStr, errStr;
        lock (stdout) outStr = stdout.ToString();
        lock (stderr) errStr = stderr.ToString();

        var exitCode = timedOut ? -1 : SafeExitCode(proc);
        return (exitCode, timedOut, outStr, errStr, sw.Elapsed);
    }

    internal static string Truncate(string s, int max = 16_000) =>
        s.Length <= max ? s : s[..max] + $"\n…(truncated, {s.Length - max} more chars)";

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }
}
