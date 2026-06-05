using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.IO;

namespace VisualMCP.Tools.IO;

[McpServerToolType]
public static class CheckSolutionTool
{
    [McpServerTool, Description(
        "Report whether a C# solution is currently loaded, and basic info about it (path, project count, whether it contains C#). " +
        "Tools auto-load the working-directory solution on demand, so you normally do NOT need to call this first — " +
        "use it to inspect workspace state or to confirm which solution was auto-loaded.")]
    public static object CheckSolution() => IoImpl.CheckSolution();
}
