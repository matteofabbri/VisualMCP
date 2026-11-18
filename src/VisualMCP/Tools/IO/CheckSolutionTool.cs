using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.IO;
using Snitch.Storage;
using Snitch.Workspace;

namespace VisualMCP.Tools.IO;

[McpServerToolType]
public static class CheckSolutionTool
{
    [McpServerTool, Description(
        "Report whether a .NET solution is currently loaded, which languages its projects use and basic info about it " +
        "(path, C# and Visual Basic project counts), plus the workspace root, the SQLite store the server is using and " +
        "its process id. Tools auto-load the working-directory solution on demand, so you normally do NOT need to call " +
        "this first — use it to inspect workspace state, confirm which solution was auto-loaded, or tell two sessions " +
        "on the same workspace apart.")]
    public static CheckSolutionResult CheckSolution(
        IIoService io, IRoslynWorkspace workspace, IVisualMcpStore store) =>
        io.CheckSolution() with
        {
            WorkspaceRoot = workspace.WorkspaceRoot,
            DatabasePath = store.EnsureDatabase(),
            ProcessId = Environment.ProcessId,
        };
}