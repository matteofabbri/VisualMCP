using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools;

[McpServerToolType]
public static class LoadSolutionTool
{
    [McpServerTool, Description("Load a Visual Studio solution (.sln or .slnx) using Roslyn MSBuildWorkspace — the same way Visual Studio loads it — and return its projects with full metadata.")]
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
