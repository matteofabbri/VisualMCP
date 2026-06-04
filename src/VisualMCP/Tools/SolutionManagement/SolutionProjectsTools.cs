using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.SolutionManagement;

namespace VisualMCP.Tools.SolutionManagement;

[McpServerToolType]
public static class AddProjectsToSolutionTool
{
    [McpServerTool(Name = "add_projects_to_solution"), Description(
        "Add one or more .csproj projects to a solution (.sln/.slnx) via 'dotnet sln add', then reload the " +
        "workspace so later tools see them. Use this INSTEAD OF a shell 'dotnet sln add'. Targets the loaded " +
        "solution unless solutionPath is given.")]
    public static Task<object> AddProjectsToSolution(
        [Description("Paths to the .csproj files to add.")] string[] projectPaths,
        [Description("Optional: the .sln/.slnx to modify. Defaults to the loaded solution.")] string? solutionPath = null,
        [Description("Timeout in seconds (default: 60).")] int timeoutSeconds = 60)
        => SolutionProjectsImpl.AddAsync(projectPaths, solutionPath, timeoutSeconds);
}

[McpServerToolType]
public static class RemoveProjectsFromSolutionTool
{
    [McpServerTool(Name = "remove_projects_from_solution"), Description(
        "Remove one or more projects from a solution (.sln/.slnx) via 'dotnet sln remove' (this only " +
        "detaches them from the solution; it does NOT delete the project files), then reload the workspace. " +
        "Targets the loaded solution unless solutionPath is given.")]
    public static Task<object> RemoveProjectsFromSolution(
        [Description("Paths to the .csproj files to remove from the solution.")] string[] projectPaths,
        [Description("Optional: the .sln/.slnx to modify. Defaults to the loaded solution.")] string? solutionPath = null,
        [Description("Timeout in seconds (default: 60).")] int timeoutSeconds = 60)
        => SolutionProjectsImpl.RemoveAsync(projectPaths, solutionPath, timeoutSeconds);
}
