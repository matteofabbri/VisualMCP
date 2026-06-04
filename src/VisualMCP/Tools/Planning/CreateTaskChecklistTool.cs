using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class CreateTaskChecklistTool
{
    [McpServerTool(Name = "create_task_checklist"), Description(
        "At the START of any multi-step operation, create a Markdown checklist file with a two-column " +
        "table (Task | Done) listing what needs to be done and whether each step is done. Mark steps off " +
        "as you complete them with update_task_checklist. Use this to plan and track multi-step work so " +
        "progress is visible and resumable. The file is written next to the solution by default.")]
    public static object CreateTaskChecklist(
        [Description("Title of the checklist / the overall task.")] string title,
        [Description("The ordered list of steps to perform.")] string[] steps,
        [Description("Optional: file name (a '.md' extension is added if missing). Defaults to a name derived from the title.")] string? fileName = null,
        [Description("Optional: target directory. Defaults to the loaded solution's directory, or the current directory.")] string? directory = null)
        => CreateTaskChecklistImpl.Run(title, steps, fileName, directory);
}
