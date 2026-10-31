using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class UpdateTaskChecklistTool
{
    [McpServerTool(Name = "update_task_checklist"), Description(
        "Mark steps done (or not-done) in the checklist a board card carries — the one create_task_checklist " +
        "wrote, or the one board_add{steps} put in a card's description. Identify steps by their 1-based " +
        "position in the list. Call it as you finish each step so the card stays current; when every step is " +
        "done, board_move{status: done, note: <evidence>} closes the card. Returns the counts and the card body.")]
    public static Task<object> UpdateTaskChecklist(
        ITaskChecklistService checklist,
        [Description("Board card id (as returned by create_task_checklist or board_add).")] int id,
        [Description("1-based positions of the steps to mark as DONE.")] int[]? done = null,
        [Description("1-based positions of the steps to mark as NOT done (to undo).")] int[]? notDone = null)
        => checklist.UpdateAsync(id, done, notDone);
}
