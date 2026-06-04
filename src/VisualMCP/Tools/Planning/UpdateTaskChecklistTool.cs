using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class UpdateTaskChecklistTool
{
    [McpServerTool(Name = "update_task_checklist"), Description(
        "Mark steps done (or not-done) in a checklist created by create_task_checklist. Identify steps by " +
        "their 1-based position in the table. Call this as you finish each step of a multi-step operation " +
        "so the Task|Done table stays current. Returns the updated counts and the new file content.")]
    public static object UpdateTaskChecklist(
        [Description("Path to the checklist .md file (as returned by create_task_checklist).")] string path,
        [Description("1-based positions of the steps to mark as DONE.")] int[]? done = null,
        [Description("1-based positions of the steps to mark as NOT done (to undo).")] int[]? notDone = null)
        => UpdateTaskChecklistImpl.Run(path, done, notDone);
}
