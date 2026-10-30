using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.IO;

namespace VisualMCP.Tools.IO;

[McpServerToolType]
public static class CheckSolutionTool
{
    [McpServerTool, Description(
        "Report whether a .NET solution is currently loaded, which languages its projects use and basic info about it " +
        "(path, C# and Visual Basic project counts). Tools auto-load the working-directory solution on demand, so you " +
        "normally do NOT need to call this first — use it to inspect workspace state or to confirm which solution was auto-loaded.")]
    public static CheckSolutionResult CheckSolution(IIoService io) => io.CheckSolution();
}