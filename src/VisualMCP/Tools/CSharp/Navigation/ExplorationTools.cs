using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Navigation;

namespace VisualMCP.Tools.CSharp.Navigation;

/// <summary>
/// The exploration tools: endpoints, TODO/FIXME, fuzzy file search, stack-trace analysis. All
/// read-only, all answered from the loaded solution in one pass.
/// </summary>
[McpServerToolType]
public static class ExplorationTools
{
    [McpServerTool(Name = "find_endpoints"), Description(
        "Inventory the ASP.NET Core endpoints: controller actions with [HttpGet]/[HttpPost]/… or [Route], " +
        "and minimal-API MapGet/MapPost/MapPut/MapDelete/MapPatch calls with a literal pattern. Each entry has " +
        "the verb, the route, the method, the containing type and file:line. Read-only.")]
    public static Task<object> FindEndpoints(
        IWorkspaceExplorationService exploration,
        [Description("Only this project (exact name).")] string? projectName = null,
        [Description("Maximum endpoints (default 500).")] int maxResults = 500)
        => exploration.FindEndpointsAsync(projectName, maxResults);

    [McpServerTool(Name = "find_todos"), Description(
        "The TODO / FIXME / HACK / XXX comments of the solution: marker, text, file:line, grouped by marker. " +
        "This is the TODO window an IDE shows. Read-only.")]
    public static Task<object> FindTodos(
        IWorkspaceExplorationService exploration,
        [Description("Only this project (exact name).")] string? projectName = null,
        [Description("Maximum comments (default 500).")] int maxResults = 500)
        => exploration.FindTodosAsync(projectName, maxResults);

    [McpServerTool(Name = "find_file"), Description(
        "Find a FILE by name — exact, prefix, CamelHumps ('IOM' finds InlineFieldService) or contains — across " +
        "the loaded solution. find_symbol searches declarations; this searches the file system the solution " +
        "covers. Read-only.")]
    public static Task<object> FindFile(
        IWorkspaceExplorationService exploration,
        [Description("File name or a fragment of it.")] string query,
        [Description("Maximum files (default 50).")] int maxResults = 50)
        => exploration.FindFileAsync(query, maxResults);

    [McpServerTool(Name = "analyze_stack_trace"), Description(
        "Turn a stack trace into a list of frames with file:line: 'at Type.Method(...) in /path/file.cs:line N' " +
        "is taken as given, a frame without a location is looked up by method name in the solution. Returns the " +
        "frames, the ones that resolved and the per-file grouping — the 'open this stack trace' action. Read-only.")]
    public static Task<object> AnalyzeStackTrace(
        IWorkspaceExplorationService exploration,
        [Description("The stack trace text (one frame per line).")] string stackTrace,
        [Description("Maximum frames (default 100).")] int maxResults = 100)
        => exploration.AnalyzeStackTraceAsync(stackTrace, maxResults);
}
