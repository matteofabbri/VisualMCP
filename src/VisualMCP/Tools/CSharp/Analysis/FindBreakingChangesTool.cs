using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class FindBreakingChangesTool
{
    [McpServerTool(Name = "find_breaking_changes"), Description(
        "What changed in the public API between a git revision and the working tree: removed and " +
        "re-signed public/protected members are breaking, added ones are not. Compares only the files " +
        "that differ, and compares signatures — a body edit is not a breaking change, a changed return " +
        "type is. Use it before a merge or a release; use git_diff when you want the full patch.")]
    public static Task<object> FindBreakingChanges(
        IFindBreakingChangesService svc,
        [Description("The baseline revision: a branch, a tag, 'HEAD~5' or a commit hash")] string gitRef,
        [Description("How many entries per list to return (default 100)")] int top = 100)
        => svc.Find(gitRef, top);
}
