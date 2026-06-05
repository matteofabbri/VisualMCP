using VisualMCP.Implementation.Execution;
using VisualMCP.Workspace;

namespace VisualMCP.Implementation.GitHub;

internal static class GitHubCliImpl
{
    internal static async Task<object> RunAsync(string args, string? workingDirectory, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(args))
            return new { error = "Provide gh arguments, e.g. 'repo view' or 'pr list'." };

        if (timeoutSeconds < 1) timeoutSeconds = 1;
        if (timeoutSeconds > 600) timeoutSeconds = 600;

        string dir;
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            if (!Directory.Exists(workingDirectory)) return new { error = $"Working directory not found: {workingDirectory}" };
            dir = Path.GetFullPath(workingDirectory);
        }
        else
        {
            var sln = RoslynWorkspaceService.Instance.LoadedSolutionPath;
            dir = sln is not null ? Path.GetDirectoryName(sln)! : Directory.GetCurrentDirectory();
        }

        var (exitCode, timedOut, stdout, stderr, elapsed) = await ProcessRunner.RunAsync("gh", args, dir, timeoutSeconds);

        if (timedOut) return new { error = $"gh timed out after {timeoutSeconds}s.", command = $"gh {args}" };

        return new
        {
            command = $"gh {args}",
            workingDirectory = dir,
            exitCode,
            succeeded = exitCode == 0,
            stdout = ProcessRunner.Truncate(stdout, 24_000),
            stderr = ProcessRunner.Truncate(stderr, 8_000),
            note = exitCode != 0 && (stderr + stdout).Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? "If gh is not installed or not authenticated, run 'gh auth login' once in a terminal."
                : null,
        };
    }
}
