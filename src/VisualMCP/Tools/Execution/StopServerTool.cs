using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using ModelContextProtocol.Server;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class StopServerTool
{
    [McpServerTool, Description(
        "Stop running VisualMCP server process(es) to release locks on the built DLL/exe so a rebuild or " +
        "re-publish can overwrite the files — without needing a shell 'Stop-Process' command. " +
        "By default it stops every OTHER instance and leaves the process serving THIS call alive so the " +
        "result can be returned. Set includeSelf=true to also stop the current process: it returns the " +
        "result first, then exits about a second later, and Claude Code relaunches it on the next call.")]
    public static object StopServer(
        [Description("Also stop the current server process handling this call (it replies first, then exits; Claude Code relaunches it). Default: false.")] bool includeSelf = false)
    {
        int selfPid = Environment.ProcessId;
        var processes = FindVisualMcpProcesses();
        var results = new List<object>();
        var killedOthers = 0;
        var selfScheduled = false;

        foreach (var (pid, cmd) in processes)
        {
            if (pid == selfPid)
            {
                if (includeSelf)
                {
                    selfScheduled = true;
                    results.Add(new { pid, isSelf = true, action = "will exit ~1s after responding", commandLine = cmd });
                }
                else
                {
                    results.Add(new { pid, isSelf = true, action = "left running (serving this call)", commandLine = cmd });
                }
                continue;
            }

            string action;
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                action = "stopped";
                killedOthers++;
            }
            catch (Exception ex)
            {
                action = $"failed: {ex.Message}";
            }
            results.Add(new { pid, isSelf = false, action, commandLine = cmd });
        }

        if (selfScheduled)
        {
            // Reply first, then exit so the build/publish can overwrite our files.
            _ = Task.Run(async () => { await Task.Delay(900); Environment.Exit(0); });
        }

        return new
        {
            selfPid,
            instancesFound = processes.Count,
            stoppedOthers  = killedOthers,
            selfWillExit   = selfScheduled,
            note = selfScheduled
                ? "Other instances stopped; THIS server will exit shortly. Claude Code relaunches it on the next tool call — there may be a brief disconnect."
                : (killedOthers > 0
                    ? "Other instances stopped; this server stays up. Pass includeSelf=true if its own file lock must also be released."
                    : "No other instances were running; this server stays up. Pass includeSelf=true to stop it too."),
            results,
        };
    }

    /// <summary>
    /// Finds VisualMCP server processes — both the published 'VisualMCP.exe' and
    /// 'dotnet' hosting 'VisualMCP.dll' — by inspecting each process's command line
    /// via WMI. Build/test invocations (dotnet build/run of the .csproj) are
    /// excluded because they reference the project file, not the built assembly.
    /// </summary>
    private static List<(int pid, string commandLine)> FindVisualMcpProcesses()
    {
        var list = new List<(int, string)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'VisualMCP.exe' OR Name = 'dotnet.exe'");
            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                var cmd = mo["CommandLine"] as string ?? "";
                // Match only the actual server assembly, not 'dotnet build VisualMCP.csproj' etc.
                if (cmd.Contains("VisualMCP.dll", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Contains("VisualMCP.exe", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add((Convert.ToInt32(mo["ProcessId"]), cmd));
                }
            }
        }
        catch
        {
            // WMI unavailable — fall back to process-name matching (covers the exe only).
            foreach (var p in Process.GetProcessesByName("VisualMCP"))
            {
                try { list.Add((p.Id, p.MainModule?.FileName ?? "VisualMCP.exe")); } catch { }
                finally { p.Dispose(); }
            }
        }
        return list;
    }
}
