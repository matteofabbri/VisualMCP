using ModelContextProtocol.Server;
using System.ComponentModel;

namespace VsSolutionServer.Tools;

[McpServerToolType]
public static class ListSolutionsTool
{
    [McpServerTool, Description("Scan a directory recursively for Visual Studio solution files (.sln, .slnx).")]
    public static object ListSolutions(
        [Description("Root directory to search (e.g. C:\\REPOSITORY)")] string rootPath,
        [Description("Maximum depth to recurse (default: 3)")] int maxDepth = 3)
    {
        rootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(rootPath))
            return new { error = $"Directory not found: {rootPath}" };

        var found = new List<object>();
        ScanDir(rootPath, rootPath, 0, maxDepth, found);

        return new { rootPath, solutionCount = found.Count, solutions = found };
    }

    private static void ScanDir(string dir, string root, int depth, int maxDepth, List<object> found)
    {
        if (depth > maxDepth) return;

        foreach (var f in Directory.GetFiles(dir, "*.sln").Concat(Directory.GetFiles(dir, "*.slnx")))
            found.Add(new { path = f, relativePath = Path.GetRelativePath(root, f) });

        try
        {
            foreach (var sub in Directory.GetDirectories(dir))
                ScanDir(sub, root, depth + 1, maxDepth, found);
        }
        catch (UnauthorizedAccessException) { }
    }
}
