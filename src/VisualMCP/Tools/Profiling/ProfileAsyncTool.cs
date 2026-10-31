using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Profiling.Analysis;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileAsyncTool
{
    [McpServerTool(Name = "profile_async"), Description(
        "The .NET async analysis: read a trace collected with the async preset (System.Threading.Tasks.TplEventSource) " +
        "and report what the tasks did — scheduled/started/completed counts, how many completed with an exception, and " +
        "the EXACT await durations (the runtime's TaskWaitSend/TaskWaitStop pairs, so not a sample), ranked from the " +
        "longest. It also names the async operations the runtime labelled and, crucially, the state machines that were " +
        "STILL ALIVE when the trace ended (IncompleteAsyncMethod): a fire-and-forget task, a deadlock on a " +
        "synchronisation context, or work that outlives the request. Use profile_collect profile=async first; a CPU " +
        "trace has no task events and the answer says so with the command to re-collect.")]
    public static async Task<object> Async(
        IAsyncFlowService asyncFlow,
        [Description("Path to the .nettrace file (collected with profile_collect profile=async).")] string tracePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run(() => asyncFlow.Analyze(tracePath), cancellationToken);
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
