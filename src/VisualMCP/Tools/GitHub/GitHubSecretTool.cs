using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.GitHub;

namespace VisualMCP.Tools.GitHub;

[McpServerToolType]
public static class GitHubSecretTool
{
    [McpServerTool(Name = "gh_secret"), Description(
        "Manage repository Actions secrets: list their names and update times, set one (the value is sent to GitHub " +
        "and never echoed back), or delete one. set and delete require confirm=true. Use this INSTEAD OF shell " +
        "'gh secret ...'.")]
    public static Task<object> GitHubSecret(
        IGitHubService github,
        [Description("list | set | delete")] string action = "list",
        [Description("Secret name, e.g. 'NUGET_API_KEY'.")] string? name = null,
        [Description("Secret value (set only).")] string? value = null,
        [Description("Must be true for set/delete.")] bool confirm = false,
        [Description("Repository as owner/name (default: the current one).")] string? repo = null)
        => github.SecretsAsync(action, name, value, confirm, repo);
}
