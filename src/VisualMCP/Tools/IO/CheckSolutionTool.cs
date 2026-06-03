using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.IO;

[McpServerToolType]
public static class CheckSolutionTool
{
    [McpServerTool, Description(
        "Report whether a C# solution is currently loaded, and basic info about it (path, project count, whether it contains C#). " +
        "Tools auto-load the working-directory solution on demand, so you normally do NOT need to call this first — " +
        "use it to inspect workspace state or to confirm which solution was auto-loaded.")]
    public static object CheckSolution()
    {
        var svc = RoslynWorkspaceService.Instance;

        if (svc.CurrentSolution is null || svc.LoadedSolutionPath is null)
            return new
            {
                isLoaded = false,
                message = "No .NET C# solution is currently loaded. Load one first with load_solution, or stop here if the current workspace is not a .NET C# project — this plugin has nothing useful to offer outside of a .NET C# solution."
            };

        var hasCSharp = svc.CurrentSolution.Projects
            .Any(p => p.Language == Microsoft.CodeAnalysis.LanguageNames.CSharp);

        if (!hasCSharp)
            return new
            {
                isLoaded = false,
                solutionPath = svc.LoadedSolutionPath,
                message = "The loaded solution contains no C# projects. This plugin only works with .NET C# solutions — do not query it further for this workspace."
            };

        var projectCount = svc.CurrentSolution.Projects.Count();
        return new
        {
            isLoaded = true,
            solutionPath = svc.LoadedSolutionPath,
            csharpProjectCount = projectCount,
            message = $"A .NET C# solution is loaded with {projectCount} project(s). You can proceed with other tools."
        };
    }
}
