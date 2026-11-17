using System.Diagnostics;
using System.Text;

namespace Snitch.Execution;

/// <summary>Shared helper for running external processes with a timeout.</summary>
public static class ProcessRunner
{
    public static async Task<(int exitCode, bool timedOut, string stdout, string stderr, TimeSpan elapsed)>
        RunAsync(string exe, string args, string workingDir, int timeoutSeconds,
            IReadOnlyDictionary<string, string>? environment = null)
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

        // Children must not import the SDK targets an in-process MSBuild evaluation pinned into
        // this process's environment: with MSBuildSDKsPath inherited, a dotnet build whose CLI
        // resolves a different SDK via global.json loads that SDK's resolver against the wrong
        // runtime and dies with MSB4237. See RemoveInheritedMsBuildEnv.
        RemoveInheritedMsBuildEnv(psi);

        // The disk policy first, so an explicit per-call value still wins: a build started after
        // disk_usage action=guard writes its temp and restore caches on the drive the policy names.
        Platform.DiskPolicy.ApplyTo(psi);

        if (environment is not null)
            foreach (var (name, value) in environment) psi.Environment[name] = value;

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

    /// <summary>
    /// Removes the MSBuild-internal variables an in-process evaluation leaves in the server's
    /// environment for its whole lifetime (MSBuildSDKsPath, MSBuildExtensionsPath, MSBUILD_EXE_PATH),
    /// so the child resolves its own SDK — global.json honored — instead of importing the pinned
    /// SDK's targets and resolvers (MSB4237 on an SDK/runtime mismatch). Every child starts clean.
    /// </summary>
    public static void RemoveInheritedMsBuildEnv(ProcessStartInfo psi)
    {
        psi.Environment.Remove("MSBuildSDKsPath");
        psi.Environment.Remove("MSBuildExtensionsPath");
        psi.Environment.Remove("MSBuildToolsPath");
        psi.Environment.Remove("MSBUILD_EXE_PATH");
    }

    public static string Truncate(string s, int max = 16_000) =>
        s.Length <= max ? s : s[..max] + $"\n…(truncated, {s.Length - max} more chars)";

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }
}
