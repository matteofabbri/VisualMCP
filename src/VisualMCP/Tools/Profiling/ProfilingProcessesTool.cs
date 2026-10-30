using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Profiling.Runtime;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfilingProcessesTool
{
    [McpServerTool(Name = "profile_processes"), Description(
        "List the running .NET processes that can be profiled (the same publication mechanism dotnet-trace ps uses: " +
        "a process appears only if its diagnostics IPC endpoint is alive). Returns pid, process name, executable path, " +
        "working set and start time, and marks the MCP server itself. Use this first to find the processId for " +
        "profile_collect, profile_counters, profile_stacks, profile_heap or profile_dump; those tools also accept " +
        "processName when the name is unambiguous.")]
    public static object Processes(
        IProfiledProcessService processes,
        [Description("Optional case-insensitive substring of the process name or executable path.")] string? nameFilter = null)
    {
        try
        {
            var list = processes.List(nameFilter);
            return new { count = list.Count, processes = list };
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
