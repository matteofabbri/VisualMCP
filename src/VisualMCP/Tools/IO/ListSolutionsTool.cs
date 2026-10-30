using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.IO;

namespace VisualMCP.Tools.IO;

[McpServerToolType]
public static class ListSolutionsTool
{
    [McpServerTool, Description("Scan a directory recursively for Visual Studio solution files (.sln, .slnx).")]
    public static ListSolutionsResult ListSolutions(
        IIoService io,
        [Description("Root directory to search (e.g. C:\\REPOSITORY)")] string rootPath,
        [Description("Maximum depth to recurse (default: 3)")] int maxDepth = 3)
        => io.ListSolutions(rootPath, maxDepth);
}