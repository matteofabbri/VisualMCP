using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.GitHub;

namespace VisualMCP.Tools.GitHub;

[McpServerToolType]
public static class GitHubCliTool
{
    [McpServerTool(Name = "github"), Description(
        "Interact with GitHub via the authenticated GitHub CLI ('gh'): create repos, manage pull requests, " +
        "issues, releases, runs, etc. Pass the gh arguments WITHOUT the leading 'gh' (e.g. 'repo create my-app " +
        "--public --source . --push', 'pr list', 'issue view 12', 'run list'). Returns stdout/stderr and exit " +
        "code. Requires the gh CLI to be installed and authenticated ('gh auth login').")]
    public static Task<object> GitHub(
        [Description("Arguments passed to 'gh' (without the leading 'gh'). E.g. 'repo view', 'pr create --fill'.")] string args,
        [Description("Working directory (defaults to the loaded solution's repo / current directory).")] string? workingDirectory = null,
        [Description("Timeout in seconds (default: 120).")] int timeoutSeconds = 120)
        => GitHubCliImpl.RunAsync(args, workingDirectory, timeoutSeconds);
}
