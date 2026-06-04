using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Execution;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class BuildProjectTool
{
    [McpServerTool(Name = "build_project"), Description(
        "Runs 'dotnet build' on a project (or the whole solution) and returns a structured list of " +
        "compiler errors and warnings, plus the overall build result. " +
        "By default compiles without copying output files so it works even while the app is running " +
        "(avoids the DLL-in-use lock). Use this to get the real MSBuild verdict, which catches " +
        "source-generator and target-file issues that Roslyn's in-memory model may miss.")]
    public static Task<object> BuildProject(
        [Description("Optional: name of the project to build. If omitted, the whole solution is built.")] string? projectName = null,
        [Description("Build configuration (e.g. 'Debug' or 'Release'). Default: Debug.")] string configuration = "Debug",
        [Description("Skip copying output files to bin/ — avoids file-in-use errors when the app is already running (default: true).")] bool noCopyOutput = true,
        [Description("Restore NuGet packages before building (default: true).")] bool restore = true,
        [Description("Build timeout in seconds (default: 120).")] int timeoutSeconds = 120)
        => BuildProjectImpl.RunAsync(projectName, configuration, noCopyOutput, restore, timeoutSeconds);
}
