using ModelContextProtocol.Server;
using System.ComponentModel;
using VsSolutionServer.Parsing;

namespace VsSolutionServer.Tools;

[McpServerToolType]
public static class LoadSolutionTool
{
    [McpServerTool, Description("Load a Visual Studio solution (.sln or .slnx) and return its projects with metadata.")]
    public static object LoadSolution(
        [Description("Absolute or relative path to the .sln or .slnx file")] string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            return new { error = $"File not found: {path}" };

        var ext = Path.GetExtension(path).ToLowerInvariant();
        SolutionInfo solution = ext switch
        {
            ".slnx" => SlnxParser.Parse(path),
            ".sln"  => SlnParser.Parse(path),
            _       => throw new ArgumentException($"Unsupported extension: {ext}")
        };

        var solutionDir = Path.GetDirectoryName(path)!;
        var projects = solution.Projects
            .Select(p =>
            {
                var absPath = Path.IsPathRooted(p.Path)
                    ? p.Path
                    : Path.GetFullPath(Path.Combine(solutionDir, p.Path));
                return new
                {
                    p.Name,
                    Path = absPath,
                    Exists = File.Exists(absPath)
                };
            })
            .ToList();

        return new
        {
            solution = Path.GetFileName(path),
            directory = solutionDir,
            projectCount = projects.Count,
            projects
        };
    }
}
