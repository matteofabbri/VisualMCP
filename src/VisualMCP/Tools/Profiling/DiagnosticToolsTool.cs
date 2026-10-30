using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Workspace;
using Snitch.Profiling.Tooling;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class DiagnosticToolsTool
{
    [McpServerTool(Name = "profile_tools"), Description(
        "Status and installation of the dotnet diagnostics toolchain: dotnet-trace, dotnet-counters, dotnet-gcdump, " +
        "dotnet-dump, dotnet-stack, dotnet-monitor, dotnet-symbol. For each tool it reports whether it is installed, " +
        "its version, where it was found (local tool manifest > global tool > PATH) and — importantly — what " +
        "VisualMCP already covers natively versus what still needs the external tool. " +
        "Use action='install' or 'update' (with tool='dotnet-trace' etc.) to make sure a tool is present before " +
        "delegating to it; you do NOT need any of them for profile_collect, profile_counters, profile_stacks, " +
        "profile_heap, profile_dump, profile_analyze or profile_symbols, which are native. " +
        "Set checkLatest=true to query NuGet for the newest published version.")]
    public static async Task<object> Tools(
        IDiagnosticToolService tools,
        IRoslynWorkspace workspace,
        [Description("Tool id to inspect or install, e.g. 'dotnet-trace'. Omit to list all seven.")] string? tool = null,
        [Description("'status' (default), 'install' or 'update'.")] string action = "status",
        [Description("Installation scope for install/update: 'auto' (local manifest if the repository has one, otherwise global), 'global' or 'local'.")] string scope = "auto",
        [Description("Optional exact version for install/update, e.g. '9.0.661903'.")] string? version = null,
        [Description("Query NuGet for the latest published version of each tool (network).")] bool checkLatest = false,
        [Description("Timeout in seconds for an install/update.")] int timeoutSeconds = 300)
    {
        try
        {
            var directory = ProfilingToolHelpers.SolutionDirectory(workspace);
            if (tool is not null && DiagnosticToolCatalog.Find(tool) is null)
                return new
                {
                    error = $"Unknown tool '{tool}'.",
                    knownTools = DiagnosticToolCatalog.All.Select(t => t.Id),
                };

            if (!string.Equals(action, "status", StringComparison.OrdinalIgnoreCase))
            {
                if (tool is null)
                    return new { error = "action='install'/'update' needs a tool id, e.g. tool='dotnet-trace'." };

                var result = await tools.EnsureAsync(tool, action, scope, version, directory, timeoutSeconds);
                var refreshed = (await tools.GetStatusAsync(tool, checkLatest, directory)).FirstOrDefault();
                return new { action, result, status = refreshed };
            }

            var statuses = await tools.GetStatusAsync(tool, checkLatest, directory);
            return new
            {
                action = "status",
                installed = statuses.Count(s => s.Installed),
                total = statuses.Count,
                tools = statuses,
            };
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
