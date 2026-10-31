using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class CreateTaskChecklistTool
{
    [McpServerTool(Name = "create_task_checklist"), Description(
        "At the START of any multi-step operation, put the plan on the board: this creates ONE card whose " +
        "description holds the steps as a checklist (no file is written — a Markdown checklist next to the " +
        "solution is invisible to the next session, a card is not). Mark steps off with update_task_checklist " +
        "by card id. The card appears in board_brief and board_show like every other card, so the plan is " +
        "resumable; status defaults to 'todo' (use 'in_progress' when the work starts now).")]
    public static Task<object> CreateTaskChecklist(
        ITaskChecklistService checklist,
        [Description("Title of the card / the overall task.")] string title,
        [Description("The ordered list of steps to perform.")] string[] steps,
        [Description("Column to create the card in: todo (default), backlog or in_progress.")] string? status = null,
        [Description("Priority: lowest, low, medium (default), high, highest.")] string? priority = null,
        [Description("Free-form labels for the card.")] string[]? labels = null)
        => checklist.CreateAsync(title, steps, status ?? "todo", priority, labels);
}
