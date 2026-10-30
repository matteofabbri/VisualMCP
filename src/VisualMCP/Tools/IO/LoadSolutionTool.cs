using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.IO;
using Snitch.Metacode.Indexing;

namespace VisualMCP.Tools.IO;

[McpServerToolType]
public static class LoadSolutionTool
{
    [McpServerTool, Description(
        "Explicitly load a specific solution (.sln/.slnx) by path via Roslyn MSBuildWorkspace, the same way Visual Studio loads it, returning its projects with full metadata. " +
        "Usually UNNECESSARY: every other tool auto-discovers and loads the solution in the working directory on first use. " +
        "Call this only to target a specific solution by path, or when a tool reports that none could be located. " +
        "Loading a solution also generates its metacode index in the background (see metacode_index).")]
    public static async Task<LoadSolutionResult> LoadSolution(
        IIoService io,
        IMetacodeIndexService metacode,
        [Description("Absolute path to the .sln or .slnx file")] string path)
    {
        var result = await io.LoadAsync(path);

        // "Generated the first time the solution is opened" includes an explicit open. It runs in the
        // background so a big solution does not delay the load result, and it never fails the call.
        _ = Task.Run(async () =>
        {
            try
            {
                await metacode.BuildAsync();
                metacode.StartWatching();
            }
            catch
            {
                // Derived data: the tools report an empty index and `metacode_index build` retries.
            }
        });

        return result;
    }
}