using System.Diagnostics;
using System.Management;

namespace Snitch.Execution;

/// <summary>
/// Stops other running instances of the host server process (to release a locked
/// build output), optionally including the current process. The process to look
/// for is taken from <see cref="SnitchOptions.EffectiveProcessName"/>.
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
}
