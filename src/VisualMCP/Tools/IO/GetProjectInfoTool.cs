using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Parsing;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.IO;

[McpServerToolType]
public static class GetProjectInfoTool
{
    [McpServerTool, Description("Return detailed info about a project in the loaded solution: source files, project references, NuGet packages, and diagnostics. Requires LoadSolution to have been called first.")]
    public static async Task<object> GetProjectInfo(
        [Description("Project name (as returned by load_solution) or absolute path to the .csproj file")] string nameOrPath)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var project = FindProject(solution, nameOrPath);
        if (project is null)
            return new { error = $"Project not found: {nameOrPath}" };

        var sourceFiles = project.Documents
            .Where(d => d.SourceCodeKind == SourceCodeKind.Regular)
            .Select(d => d.FilePath)
            .OrderBy(f => f)
            .ToList();

        var projectRefs = project.ProjectReferences
            .Select(r => solution.GetProject(r.ProjectId)?.Name ?? r.ProjectId.ToString())
            .OrderBy(n => n)
            .ToList();

        var metaRefs = project.MetadataReferences
            .OfType<PortableExecutableReference>()
            .Select(r => Path.GetFileNameWithoutExtension(r.FilePath))
            .Where(n => n is not null)
            .OrderBy(n => n)
            .ToList();

        // NuGet packages are not exposed by Roslyn — fall back to csproj XML
        var packages = new List<object>();
        if (project.FilePath is not null && File.Exists(project.FilePath))
        {
            try
            {
                var csprojInfo = CsprojParser.Parse(project.FilePath);
                packages = csprojInfo.PackageReferences
                    .Select(p => (object)new { p.Name, p.Version })
                    .ToList();
            }
            catch { /* best-effort */ }
        }

        return new
        {
            Name = project.Name,
            AssemblyName = project.AssemblyName,
            Language = project.Language,
            FilePath = project.FilePath,
            OutputType = project.CompilationOptions?.OutputKind.ToString(),
            DocumentCount = sourceFiles.Count,
            sourceFiles,
            projectReferences = projectRefs,
            assemblyReferences = metaRefs,
            packageReferences = packages,
        };
    }

    private static Project? FindProject(Solution solution, string nameOrPath)
    {
        // Try by exact name first
        var byName = solution.Projects.FirstOrDefault(p =>
            string.Equals(p.Name, nameOrPath, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;

        // Try by file path
        var absPath = Path.IsPathRooted(nameOrPath) ? nameOrPath : Path.GetFullPath(nameOrPath);
        return solution.Projects.FirstOrDefault(p =>
            string.Equals(p.FilePath, absPath, StringComparison.OrdinalIgnoreCase));
    }
}
