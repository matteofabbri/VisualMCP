using Snitch.Execution;

namespace Snitch.Git;

/// <summary>Shared helper for the git tools.</summary>
public static class GitCli
{
    /// <summary>
    /// Resolves the git repository root: from <paramref name="repoPath"/> if given, else from
    /// the active solution's directory (<paramref name="activeSolutionPath"/>), else the cwd.
    /// </summary>
    public static async Task<(string? repoDir, object? error)> ResolveRepoAsync(
        string? repoPath = null, string? activeSolutionPath = null)
    {
        string startDir;
        if (!string.IsNullOrWhiteSpace(repoPath))
        {
            if (!Directory.Exists(repoPath))
                return (null, new { error = $"Repository path not found: {repoPath}" });
            startDir = Path.GetFullPath(repoPath);
        }
        else
        {
            startDir = activeSolutionPath is not null
                ? Path.GetDirectoryName(activeSolutionPath)!
                : Directory.GetCurrentDirectory();
        }

        var (exitCode, timedOut, stdout, stderr, _) =
            await ProcessRunner.RunAsync("git", $"-C \"{startDir}\" rev-parse --show-toplevel", startDir, 20);

        if (timedOut)
            return (null, new { error = "git timed out resolving the repository root." });
        if (exitCode != 0)
            return (null, new { error = $"Not inside a git repository (start dir: {startDir}). {stderr.Trim()}".Trim() });

        var top = stdout.Trim();
        return (string.IsNullOrEmpty(top) ? startDir : top, null);
    }

    public static Task<(int exitCode, bool timedOut, string stdout, string stderr, TimeSpan elapsed)>
        RunAsync(string repoDir, string gitArgs, int timeoutSeconds = 30,
            IReadOnlyDictionary<string, string>? environment = null) =>
        ProcessRunner.RunAsync("git", $"-C \"{repoDir}\" {gitArgs}", repoDir, timeoutSeconds, environment);
}
