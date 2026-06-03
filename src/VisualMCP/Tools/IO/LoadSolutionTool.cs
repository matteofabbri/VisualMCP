using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.IO;

[McpServerToolType]
public static class LoadSolutionTool
{
    [McpServerTool, Description(
        "Explicitly load a specific solution (.sln/.slnx) by path via Roslyn MSBuildWorkspace, the same way Visual Studio loads it, returning its projects with full metadata. " +
        "Usually UNNECESSARY: every other tool auto-discovers and loads the solution in the working directory on first use. " +
        "Call this only to target a specific solution by path, or when a tool reports that none could be located.")]
    public static async Task<object> LoadSolution(
        [Description("Absolute path to the .sln or .slnx file")] string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            return new { error = $"File not found: {path}" };

        var result = await RoslynWorkspaceService.Instance.LoadSolutionAsync(path);
        var solution = result.Solution;

        var projects = solution.Projects.Select(p => new
        {
            p.Name,
            Path = p.FilePath,
            Language = p.Language,
            AssemblyName = p.AssemblyName,
            DocumentCount = p.Documents.Count(),
            ProjectReferenceCount = p.ProjectReferences.Count(),
            MetadataReferenceCount = p.MetadataReferences.Count,
        }).ToList();

        return new
        {
            solution = Path.GetFileName(path),
            directory = Path.GetDirectoryName(path),
            projectCount = projects.Count,
            workspaceDiagnostics = result.Diagnostics,
            projects
        };
    }
}
