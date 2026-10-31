using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitSubmoduleTool
{
    [McpServerTool(Name = "git_submodule"), Description(
        "Manage submodules of the repository: status (structured: commit sha, path, state per submodule), " +
        "update (--init --recursive: clones the submodules and checks out the recorded commits), init, " +
        "sync (aligns the remote URLs with .gitmodules) or deinit. Use this INSTEAD OF shell " +
        "'git submodule ...'.")]
    public static Task<object> GitSubmodule(
        IGitService git,
        [Description("status (default) | update | init | sync | deinit.")] string action = "status",
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.SubmoduleAsync(action, repoPath);
}
