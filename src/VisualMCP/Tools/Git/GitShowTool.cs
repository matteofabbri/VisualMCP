using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitShowTool
{
    [McpServerTool(Name = "git_show"), Description(
        "One commit in full: hash, author, date, message and the diffstat (plus the diff of a single file when path " +
        "is given). Use this INSTEAD OF shell 'git show'. Read-only.")]
    public static Task<object> GitShow(
        IGitService git,
        [Description("Revision: 'HEAD', a hash, a tag, 'HEAD~2', ...")] string rev = "HEAD",
        [Description("Show only this file's diff.")] string? path = null,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.ShowAsync(rev, path, repoPath);
}
