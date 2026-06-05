using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.IO;

namespace VisualMCP.Tools.IO;

[McpServerToolType]
public static class ListSolutionsTool
{
    [McpServerTool, Description("Scan a directory recursively for Visual Studio solution files (.sln, .slnx).")]
    public static object ListSolutions(
        [Description("Root directory to search (e.g. C:\\REPOSITORY)")] string rootPath,
        [Description("Maximum depth to recurse (default: 3)")] int maxDepth = 3)
        => IoImpl.ListSolutions(rootPath, maxDepth);
}
