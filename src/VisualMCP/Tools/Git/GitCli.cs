using VisualMCP.Tools.Execution;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Git;

/// <summary>Shared helper for the read-only git tools.</summary>
internal static class GitCli
{
    /// <summary>
    /// Resolves the git repository root to operate on: the loaded solution's
    /// directory (if any) otherwise the server's working directory, then the
    /// enclosing git top-level. Does not force a solution load.
    /// </summary>
    internal static async Task<(string? repoDir, object? error)> ResolveRepoAsync()
    {
        var sln = RoslynWorkspaceService.Instance.LoadedSolutionPath;
        var startDir = sln is not null ? Path.GetDirectoryName(sln)! : Directory.GetCurrentDirectory();

        var (exitCode, timedOut, stdout, stderr, _) =
            await ProcessRunner.RunAsync("git", $"-C \"{startDir}\" rev-parse --show-toplevel", startDir, 20);

        if (timedOut)
            return (null, new { error = "git timed out resolving the repository root." });
        if (exitCode != 0)
            return (null, new { error = $"Not inside a git repository (start dir: {startDir}). {stderr.Trim()}".Trim() });

        var top = stdout.Trim();
        return (string.IsNullOrEmpty(top) ? startDir : top, null);
    }

    internal static Task<(int exitCode, bool timedOut, string stdout, string stderr, TimeSpan elapsed)>
        RunAsync(string repoDir, string gitArgs, int timeoutSeconds = 30) =>
        ProcessRunner.RunAsync("git", $"-C \"{repoDir}\" {gitArgs}", repoDir, timeoutSeconds);
}
