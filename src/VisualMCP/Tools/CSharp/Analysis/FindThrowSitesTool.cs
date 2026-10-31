using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Analysis.DataFlow;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class FindThrowSitesTool
{
    [McpServerTool(Name = "find_throw_sites"), Description(
        "The semantic query ReSharper's search cannot express: which methods throw this exception? `exceptionType` " +
        "is a simple or fully-qualified name ('InvalidOperationException', 'System.ArgumentException', or " +
        "'Exception' for every derived type). Direct throws are the methods that create it; with `transitive` " +
        "(default true) a method that calls a thrower is reported too, with the shortest call path between them. " +
        "A method that catches the type is listed separately as a boundary — it is where the exception stops, so " +
        "the search does not continue past it. Throws inside local functions are attributed to the local function. " +
        "Read-only.")]
    public static Task<object> FindThrowSites(
        IFindThrowSitesService svc,
        [Description("Exception type: simple ('ArgumentException') or fully qualified ('System.ArgumentException').")] string exceptionType,
        [Description("Include the callers that reach a thrower, with their path (default true).")] bool transitive = true,
        [Description("Optional: only this project (exact name from the loaded solution).")] string? projectName = null,
        [Description("Maximum throwers to return (default 200).")] int maxResults = 200)
        => svc.Find(exceptionType, transitive, projectName, maxResults);
}
