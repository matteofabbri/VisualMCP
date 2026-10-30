using Snitch.Workspace;

namespace VisualMCP.Tools.Profiling;

/// <summary>Shared plumbing for the profiling tools: the solution directory and error shaping.</summary>
internal static class ProfilingToolHelpers
{
    public static string SolutionDirectory(IRoslynWorkspace workspace)
    {
        var solutionPath = workspace.LoadedSolutionPath;
        return solutionPath is not null
            ? Path.GetDirectoryName(solutionPath)!
            : Directory.GetCurrentDirectory();
    }

    public static object Error(Exception exception) =>
        new
        {
            error = exception is ArgumentException or InvalidOperationException or FileNotFoundException
                ? exception.Message
                : $"{exception.GetType().Name}: {exception.Message}",
        };
}
