using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Analysis;
using Snitch.Languages.CSharp.Diagrams;
using Snitch.Languages.CSharp.Navigation;

namespace VisualMCP.Tools.CSharp.Diagrams;

[McpServerToolType]
public static class DiagramTools
{
    [McpServerTool(Name = "call_graph"), Description(
        "The call graph of one method as a diagram artifact: a Mermaid flowchart (or Graphviz DOT) of the " +
        "methods that call it, that it calls, or both, walked to `depth`. Use it when a text list of callers " +
        "is not enough to see the shape; call_hierarchy remains the JSON tree.")]
    public static Task<object> CallGraph(
        ICallDiagramService diagrams,
        [Description("The method name (simple or qualified).")] string symbolName,
        [Description("Optional: the containing type, to disambiguate an overload or a same-named method.")] string? containingType = null,
        [Description("outgoing (default), incoming or both.")] string direction = "outgoing",
        [Description("Levels to walk (1-5, default 2).")] int depth = 2,
        [Description("mermaid (default) or dot.")] string format = "mermaid",
        [Description("Maximum nodes in the graph (default 120, max 1000).")] int maxNodes = 120)
        => diagrams.CallGraphAsync(symbolName, containingType, direction, depth, format, maxNodes);

    [McpServerTool(Name = "sequence_diagram"), Description(
        "A Mermaid sequence diagram of the calls a method makes, walked to `depth`: one participant per type, " +
        "one message per call site, with the depth in the label. It is the STATIC call order the source " +
        "writes, not the order a run takes, and a call in a loop appears once.")]
    public static Task<object> SequenceDiagram(
        ICallDiagramService diagrams,
        [Description("The method name (simple or qualified).")] string symbolName,
        [Description("Optional: the containing type, to disambiguate.")] string? containingType = null,
        [Description("Levels to walk (1-4, default 2).")] int depth = 2,
        [Description("Maximum messages (default 60, max 300).")] int maxMessages = 60)
        => diagrams.SequenceAsync(symbolName, containingType, depth, maxMessages);

    [McpServerTool(Name = "control_flow_graph"), Description(
        "The control-flow graph of one method body as Mermaid (or DOT): Roslyn's basic blocks, their branch " +
        "conditions and their try/catch regions. Identifies the method by file+line or by name. Accessors and " +
        "field initializers have no body operation of their own and are refused with that reason.")]
    public static Task<object> ControlFlowGraph(
        IControlFlowGraphService graphs,
        [Description("Optional: a file of the loaded solution; with `line`, the method containing it is used.")] string? filePath = null,
        [Description("1-based line inside the method, when filePath is given.")] int line = 0,
        [Description("Optional: the method name, when filePath is not given.")] string? methodName = null,
        [Description("Optional: the containing type, with methodName.")] string? containingType = null,
        [Description("mermaid (default) or dot.")] string format = "mermaid")
        => graphs.ExportAsync(filePath, line, methodName, containingType, format);

    [McpServerTool(Name = "track_value"), Description(
        "Where a value comes from and where it goes: the definitions (writes, including the declaration) and the " +
        "uses (reads) of the local, parameter, field or property named on a line, each with file:line, the member " +
        "it happens in and a snippet. The data behind the IDE's value tracking.")]
    public static Task<object> TrackValue(
        IValueTrackingService values,
        [Description("File of the loaded solution.")] string filePath,
        [Description("1-based line with the identifier.")] int line,
        [Description("Optional: the identifier name, when the line has several.")] string? name = null)
        => values.TrackAsync(filePath, line, name);

    [McpServerTool(Name = "localization_report"), Description(
        "The localization view: the .resx files with their key counts, the neutral keys nothing references " +
        "(GetString/GetObject literals and the generated Resources properties are the references), the keys a " +
        "translation is missing (and extra keys it has), and the user-facing string literals still in code as " +
        "candidates. The first two are exact for those access patterns; the third is a heuristic.")]
    public static Task<object> LocalizationReport(
        ILocalizationService localization,
        [Description("Optional: restrict the reference scan to one project by name.")] string? projectName = null,
        [Description("Maximum rows per list (default 100, max 2000).")] int maxResults = 100)
        => localization.ReportAsync(projectName, maxResults);
}
