using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.Text;

namespace Snitch.Execution;

/// <summary>
/// Stops other running instances of the host server process (to release a locked
/// build output), optionally including the current process. The process to look
/// for is taken from <see cref="SnitchOptions.EffectiveProcessName"/>. The scan is
/// platform-aware: WMI on Windows, <c>/proc</c> on Linux, <c>ps</c> on macOS — so
/// the tool works wherever the server runs.
/// </summary>
public interface IStopServerService
{
    object Run(bool includeSelf);
}

public sealed class StopServerService : IStopServerService
{
    private readonly SnitchOptions _options;
    public StopServerService(SnitchOptions options) => _options = options;

    public object Run(bool includeSelf)
    {
        int selfPid = Environment.ProcessId;
        var processes = FindServerProcesses();
        var results = new List<object>();
        var killedOthers = 0;
        var selfScheduled = false;

        foreach (var (pid, cmd) in processes)
        {
            if (pid == selfPid)
            {
                if (includeSelf) { selfScheduled = true; results.Add(new { pid, isSelf = true, action = "will exit ~1s after responding", commandLine = cmd }); }
                else results.Add(new { pid, isSelf = true, action = "left running (serving this call)", commandLine = cmd });
                continue;
            }

            string action;
            try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); action = "stopped"; killedOthers++; }
            catch (Exception ex) { action = $"failed: {ex.Message}"; }
            results.Add(new { pid, isSelf = false, action, commandLine = cmd });
        }

        if (selfScheduled)
            _ = Task.Run(async () => { await Task.Delay(900); Environment.Exit(0); });

        return new
        {
            selfPid,
            instancesFound = processes.Count,
            stoppedOthers  = killedOthers,
            selfWillExit   = selfScheduled,
            note = selfScheduled
                ? "Other instances stopped; THIS server will exit shortly. The host relaunches it on the next call — there may be a brief disconnect."
                : (killedOthers > 0
                    ? "Other instances stopped; this server stays up. Pass includeSelf=true if its own file lock must also be released."
                    : "No other instances were running; this server stays up. Pass includeSelf=true to stop it too."),
            results,
        };
    }

    private List<(int pid, string commandLine)> FindServerProcesses()
    {
        var procName = _options.EffectiveProcessName;
        var exeName  = procName + ".exe";
        var dllName  = procName + ".dll";

        if (OperatingSystem.IsWindows())
            return FindWithWmi(procName, exeName, dllName);
        return FindWithProcessTable(procName, dllName);
    }

    /// <summary>
    /// Windows: WMI reports the command line for the apphost and for framework-dependent
    /// <c>dotnet</c> hosts; <c>GetProcessesByName</c> is the fallback when WMI is unavailable.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static List<(int pid, string commandLine)> FindWithWmi(string procName, string exeName, string dllName)
    {
        var list = new List<(int, string)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '{exeName}' OR Name = 'dotnet.exe'");
            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                var cmd = mo["CommandLine"] as string ?? "";
                if (cmd.Contains(dllName, StringComparison.OrdinalIgnoreCase) ||
                    cmd.Contains(exeName, StringComparison.OrdinalIgnoreCase))
                    list.Add((Convert.ToInt32(mo["ProcessId"]), cmd));
            }
        }
        catch
        {
            foreach (var p in Process.GetProcessesByName(procName))
            {
                try { list.Add((p.Id, p.MainModule?.FileName ?? exeName)); } catch { }
                finally { p.Dispose(); }
            }
        }
        return list;
    }

    /// <summary>
    /// Linux/macOS: an apphost runs under its own name, a framework-dependent server runs
    /// as <c>dotnet</c>. The command line decides whether a <c>dotnet</c> process is this
    /// server — the name alone would also match a <c>dotnet build</c> of the solution.
    /// </summary>
    private static List<(int pid, string commandLine)> FindWithProcessTable(string procName, string dllName)
    {
        var list = new List<(int, string)>();
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return list; }

        foreach (var p in processes)
        {
            try
            {
                var name = p.ProcessName;
                if (!name.Equals(procName, StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    continue;

                var cmd = ReadCommandLine(p.Id);
                if (cmd is null) continue;

                if (name.Equals(procName, StringComparison.OrdinalIgnoreCase) ||
                    cmd.Contains(dllName, StringComparison.OrdinalIgnoreCase))
                    list.Add((p.Id, cmd));
            }
            catch { /* a process that vanished between the scan and the read */ }
            finally { p.Dispose(); }
        }
        return list;
    }

    /// <summary>The full command line: <c>/proc/&lt;pid&gt;/cmdline</c> on Linux, <c>ps</c> on macOS.</summary>
    private static string? ReadCommandLine(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var raw = File.ReadAllBytes($"/proc/{pid}/cmdline");
                if (raw.Length == 0) return null;
                var text = Encoding.UTF8.GetString(raw).Replace('\0', ' ').Trim();
                return text.Length == 0 ? null : text;
            }
            catch { return null; }
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var psi = new ProcessStartInfo("ps", $"-p {pid} -o command=")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc is null) return null;
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(3000);
                return output.Length == 0 ? null : output;
            }
            catch { return null; }
        }

        return null;
    }
}
