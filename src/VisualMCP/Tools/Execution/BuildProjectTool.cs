using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Execution;
using Snitch.Metacode.Indexing;

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
        "(avoids the DLL-in-use lock). After the build the solution's metacode index is refreshed, so what " +
        "compiled is what metacode_index/complexity read.")]
    public static async Task<object> BuildProject(
        IBuildProjectService build,
        IMetacodeIndexService metacode,
        [Description("Optional: name of the project to build. If omitted, the whole solution is built.")] string? projectName = null,
        [Description("Build configuration (e.g. 'Debug' or 'Release'). Default: Debug.")] string configuration = "Debug",
        [Description("Skip copying output files to bin/ — avoids file-in-use errors when the app is already running (default: true).")] bool noCopyOutput = true,
        [Description("Restore NuGet packages before building (default: true).")] bool restore = true,
        [Description("Build timeout in seconds (default: 120).")] int timeoutSeconds = 120,
        [Description("Also run the project's Roslyn analyzers and include their diagnostics (default: true).")] bool runAnalyzers = true,
        CancellationToken cancellationToken = default)
    {
        var result = await build.RunAsync(projectName, configuration, noCopyOutput, restore, timeoutSeconds, runAnalyzers);

        try
        {
            // Best effort, and deliberately after the answer is computed: the index is derived data, so a
            // stale file must never turn a successful build into a failed tool call.
            await metacode.RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The caller stopped waiting; the background pass picks the changes up anyway.
        }
        catch
        {
            // Derived data only: the build result is the answer.
        }

        return result;
    }
}