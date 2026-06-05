using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.IO;

namespace VisualMCP.Tools.IO;

[McpServerToolType]
public static class GetProjectInfoTool
{
    [McpServerTool, Description("Return detailed info about a project in the loaded solution: source files, project references, NuGet packages, and diagnostics. Requires LoadSolution to have been called first.")]
    public static Task<object> GetProjectInfo(
        [Description("Project name (as returned by load_solution) or absolute path to the .csproj file")] string nameOrPath)
        => IoImpl.GetProjectInfoAsync(nameOrPath);
}
