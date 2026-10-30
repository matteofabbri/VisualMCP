using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Debugging;

namespace VisualMCP.Tools.Debugging;

[McpServerToolType]
public static class DebugInstallTools
{
    [McpServerTool(Name = "debug_status"), Description(
        "Whether the debugger's adapter (netcoredbg, the Samsung DAP implementation for .NET) is installed, " +
        "where it lives, and how to point at another build (VISUALMCP_NETCOREDBG). Read-only — the first call " +
        "of a debugging task. debug_launch needs it.")]
    public static Task<object> DebugStatus(IDebuggerService debugger) => debugger.StatusAsync();

    [McpServerTool(Name = "debug_install"), Description(
        "Download netcoredbg from the Samsung GitHub release (a few MB) and extract it under the shared tools " +
        "directory, then report the executable path. Idempotent: an installed adapter is reported, not replaced, " +
        "unless force=true. This is what makes debug_launch work on a machine that has never debugged with this " +
        "server; no other tool needs an install.")]
    public static Task<object> DebugInstall(
        IDebuggerService debugger,
        [Description("Reinstall even when an adapter is already present (default: false).")] bool force = false)
        => debugger.InstallAsync(force);
}
