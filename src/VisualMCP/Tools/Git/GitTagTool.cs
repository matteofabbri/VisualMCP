using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitTagTool
{
    [McpServerTool(Name = "git_tag"), Description(
        "Manage tags: list (default, with the commit each tag points at), create (annotated when message is given, " +
        "optionally at a specific revision), delete. Local only — publishing tags is git_sync(action=push, tags=true). " +
        "Use this INSTEAD OF shell 'git tag'.")]
    public static Task<object> GitTag(
        IGitService git,
        [Description("list | create | delete")] string action = "list",
        [Description("Tag name (for create/delete).")] string? name = null,
        [Description("Annotation message: makes it an annotated tag.")] string? message = null,
        [Description("For create: the revision to tag (default HEAD).")] string? reference = null,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.TagAsync(action, name, message, reference, repoPath);
}
