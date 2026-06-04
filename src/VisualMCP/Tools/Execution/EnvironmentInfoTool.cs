using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Execution;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class EnvironmentInfoTool
{
    [McpServerTool(Name = "get_environment_info"), Description(
        "Report the local build environment / toolchain in one call: OS and architecture, installed .NET " +
        "SDKs and runtimes (and the active 'dotnet --version'), Visual Studio installations (via vswhere), " +
        "the MSVC 'cl.exe' compiler location, and — for any file/directory paths you pass — whether they " +
        "exist and their size. Use this INSTEAD OF ad-hoc shell commands (dotnet --version, where cl, " +
        "listing Program Files, ls -la a .lib) to discover what's installed and how big a file/library is.")]
    public static Task<object> GetEnvironmentInfo(
        [Description("Optional: file or directory paths to report existence and size for (e.g. a built .lib/.dll/.exe).")] string[]? paths = null)
        => EnvironmentInfoImpl.RunAsync(paths);
}
