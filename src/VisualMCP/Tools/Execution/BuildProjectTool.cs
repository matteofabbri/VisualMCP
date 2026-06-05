using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Execution;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class BuildProjectTool
{
    [McpServerTool(Name = "build_project"), Description(
        "Runs 'dotnet build' on a project (or the whole solution) and returns a structured list of " +
        "compiler errors and warnings, plus the overall build result. By default it ALSO runs the configured " +
        "Roslyn analyzers and includes their findings (so a build automatically checks for code-style/analyzer " +
        "issues, not just compile errors) — set runAnalyzers=false to skip. " +
        "Compiles without copying output files by default so it works even while the app is running " +
        "(avoids the DLL-in-use lock).")]
    public static Task<object> BuildProject(
        [Description("Optional: name of the project to build. If omitted, the whole solution is built.")] string? projectName = null,
        [Description("Build configuration (e.g. 'Debug' or 'Release'). Default: Debug.")] string configuration = "Debug",
        [Description("Skip copying output files to bin/ — avoids file-in-use errors when the app is already running (default: true).")] bool noCopyOutput = true,
        [Description("Restore NuGet packages before building (default: true).")] bool restore = true,
        [Description("Build timeout in seconds (default: 120).")] int timeoutSeconds = 120,
        [Description("Also run the project's Roslyn analyzers and include their diagnostics (default: true).")] bool runAnalyzers = true)
        => BuildProjectImpl.RunAsync(projectName, configuration, noCopyOutput, restore, timeoutSeconds, runAnalyzers);
}
