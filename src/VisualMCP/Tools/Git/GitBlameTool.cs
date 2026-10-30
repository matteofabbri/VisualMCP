using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitBlameTool
{
    [McpServerTool(Name = "git_blame"), Description(
        "Who last touched each line, and in which commit (author, date, hash, line). Give a line range to keep the " +
        "output small on a long file. Use this INSTEAD OF shell 'git blame'. Read-only.")]
    public static Task<object> GitBlame(
        IGitService git,
        [Description("File path relative to the repository root.")] string path,
        [Description("First line of the range (1-based). Omit for the whole file.")] int? fromLine = null,
        [Description("Last line of the range.")] int? toLine = null,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.BlameAsync(path, fromLine, toLine, repoPath);
}
