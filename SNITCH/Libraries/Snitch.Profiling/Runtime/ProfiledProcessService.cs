using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Snitch.Profiling.Model;

namespace Snitch.Profiling.Runtime;

public interface IProfiledProcessService
{
    IReadOnlyList<ProfiledProcess> List(string? nameFilter = null);

    (ProfiledProcess? Target, string? Error) Resolve(int? pid, string? name);
}

/// <summary>
/// Finds the .NET processes that can be profiled. <see cref="DiagnosticsClient.GetPublishedProcesses"/>
/// asks the runtime itself (every .NET process publishes a diagnostics IPC endpoint), so the list
/// never contains a process that would make the collection fail with "not a .NET process".
/// </summary>
public sealed class ProfiledProcessService : IProfiledProcessService
{
    public IReadOnlyList<ProfiledProcess> List(string? nameFilter = null)
    {
        IEnumerable<int> ids;
        try
        {
            ids = DiagnosticsClient.GetPublishedProcesses();
        }
        catch
        {
            ids = Array.Empty<int>();
        }

        var processes = new List<ProfiledProcess>();
        foreach (var id in ids.Distinct())
        {
            var info = Describe(id);
            if (info is not null)
                processes.Add(info);
        }

        if (processes.Count == 0)
        {
            // Fallback for environments where the diagnostics IPC socket is not discoverable:
            // list the dotnet host processes, which is what the user is usually after.
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (!process.ProcessName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                        !process.ProcessName.Equals("VisualMCP", StringComparison.OrdinalIgnoreCase))
                        continue;

                    processes.Add(new ProfiledProcess(
                        process.Id,
                        process.ProcessName,
                        TryGetMainModule(process),
                        SafeWorkingSet(process),
                        SafeStartTime(process),
                        process.Id == Environment.ProcessId));
                }
                catch
                {
                    // The process exited while enumerating.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            processes = processes.Where(p =>
                p.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ||
                (p.ExecutablePath?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }

        return processes.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.ProcessId).ToList();
    }

    public (ProfiledProcess? Target, string? Error) Resolve(int? pid, string? name)
    {
        if (pid is null && string.IsNullOrWhiteSpace(name))
            return (null, "Provide either processId or processName. Call profile_processes to see the running .NET processes.");

        if (pid is not null)
        {
            var described = Describe(pid.Value);
            return described is not null
                ? (described, null)
                : (null, $"Process {pid} is not running or is not a .NET process with a diagnostics endpoint.");
        }

        var matches = List().Where(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains(name!, StringComparison.OrdinalIgnoreCase) ||
            (p.ExecutablePath?.Contains(name!, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        return matches.Count switch
        {
            0 => (null, $"No running .NET process matches '{name}'. Call profile_processes to list them."),
            1 => (matches[0], null),
            _ => (null, $"Multiple processes match '{name}': " +
                       string.Join(", ", matches.Select(m => $"{m.Name} ({m.ProcessId})")) +
                       ". Pass processId to choose one."),
        };
    }

    private static ProfiledProcess? Describe(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new ProfiledProcess(
                pid,
                process.ProcessName,
                TryGetMainModule(process),
                SafeWorkingSet(process),
                SafeStartTime(process),
                pid == Environment.ProcessId);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetMainModule(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }

    private static long SafeWorkingSet(Process process)
    {
        try { return process.WorkingSet64; }
        catch { return 0; }
    }

    private static DateTime? SafeStartTime(Process process)
    {
        try { return process.StartTime; }
        catch { return null; }
    }
}
