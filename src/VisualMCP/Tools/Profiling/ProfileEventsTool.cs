using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Profiling.Analysis;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileEventsTool
{
    [McpServerTool(Name = "profile_events"), Description(
        "The Events Viewer for EventPipe: inventory a .nettrace — every provider/event pair with how many times it " +
        "fired, its rate, the payload FIELD NAMES and one real sample event — filtered by provider and/or event name. " +
        "It needs no provider-specific parser, so a trace collected with profile_collect profile=custom is readable " +
        "here before any specialized tool claims to understand it. Use it to discover what a provider exposes (the " +
        "payload fields are the schema), to confirm a provider was actually enabled, and as the first step when " +
        "profile_async/profile_database say the trace has no event for them.")]
    public static async Task<object> Events(
        IEventInventoryService inventory,
        [Description("Path to the .nettrace file.")] string tracePath,
        [Description("Optional provider filter (substring, case-insensitive), e.g. 'EntityFramework' or 'TplEventSource'.")] string? provider = null,
        [Description("Optional event-name filter (substring, case-insensitive), e.g. 'Command' or 'Connection'.")] string? @event = null,
        [Description("How many provider/event rows to return (1-500), most frequent first.")] int top = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run(() => inventory.Analyze(tracePath, provider, @event, top), cancellationToken);
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
