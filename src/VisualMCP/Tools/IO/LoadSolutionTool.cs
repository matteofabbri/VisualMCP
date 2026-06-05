using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.IO;

namespace VisualMCP.Tools.IO;

[McpServerToolType]
public static class LoadSolutionTool
{
    [McpServerTool, Description(
        "Explicitly load a specific solution (.sln/.slnx) by path via Roslyn MSBuildWorkspace, the same way Visual Studio loads it, returning its projects with full metadata. " +
        "Usually UNNECESSARY: every other tool auto-discovers and loads the solution in the working directory on first use. " +
        "Call this only to target a specific solution by path, or when a tool reports that none could be located.")]
    public static Task<object> LoadSolution(
        [Description("Absolute path to the .sln or .slnx file")] string path)
        => IoImpl.LoadAsync(path);
}
