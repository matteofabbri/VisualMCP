using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Execution;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class ExtractBuildLogErrorsTool
{
    [McpServerTool(Name = "extract_build_log_errors"), Description(
        "Read a native or MSBuild build-log file (auto-detecting UTF-16/UTF-8/ANSI encoding) and extract the " +
        "error lines — C/C++ compiler errors (Cxxxx), linker errors (LNKxxxx), unresolved externals, and " +
        "generic ': error' — optionally warnings too. Use this INSTEAD OF a shell 'iconv + grep' pipeline " +
        "on a build log. Read-only.")]
    public static object ExtractBuildLogErrors(
        [Description("Path to the build log file.")] string logPath,
        [Description("Also include warning lines (default: false).")] bool includeWarnings = false,
        [Description("Maximum lines to return per category (default: 200).")] int maxLines = 200)
        => ExtractBuildLogErrorsImpl.Run(logPath, includeWarnings, maxLines);
}
