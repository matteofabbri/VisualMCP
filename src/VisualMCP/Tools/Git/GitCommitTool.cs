using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitCommitTool
{
    [McpServerTool(Name = "git_commit"), Description(
        "Create a git commit from the staged changes in a repository, using the given message (multi-line " +
        "allowed, no length limit — it is written via a temp file). Set stageAll=true to stage every change " +
        "first. With conventional=true the message must follow Conventional Commits ('<type>(<scope>): " +
        "<description>'; the same rules as commit_lint) or the commit is refused with the violations. " +
        "Targets the loaded solution's repo unless repoPath is given. Returns the new commit hash. " +
        "Does NOT push and does NOT amend. Fails if nothing is staged unless allowEmpty=true.")]
    public static Task<object> GitCommit(
        IGitService git,
        ICommitConventionService conventions,
        [Description("The commit message. May span multiple lines; no length limit.")] string message,
        [Description("Stage all changes (git add -A) before committing. Default: false.")] bool stageAll = false,
        [Description("Allow creating a commit with no changes. Default: false.")] bool allowEmpty = false,
        [Description("Conventional Commits rule for the message: refuses the commit with the violations instead of writing it.")] bool conventional = false,
        [Description("Optional: path to the git repository. Defaults to the loaded solution's repo.")] string? repoPath = null)
    {
        if (conventional)
        {
            var lint = conventions.Check(message);
            if (!lint.Conforming)
                return Task.FromResult<object>(new
                {
                    error = "The commit message does not follow Conventional Commits; nothing was committed.",
                    violations = lint.Violations,
                    types = CommitConvention.DefaultTypes,
                    hint = "Fix the message, or pass conventional=false to commit anyway.",
                });
        }

        return git.CommitAsync(message, stageAll, allowEmpty, repoPath);
    }
}
