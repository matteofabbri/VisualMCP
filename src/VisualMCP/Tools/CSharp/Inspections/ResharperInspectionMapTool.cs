using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Inspections;

namespace VisualMCP.Tools.CSharp.Inspections;

[McpServerToolType]
public static class ResharperInspectionMapTool
{
    [McpServerTool(Name = "resharper_inspection_map"), Description(
        "Where ReSharper's inspection ids stand here, in two layers. The CATALOGUE is read from the local Rider " +
        "installation's metadata: every ConfigurableSeverityHighlighting id, its language group, the " +
        "ElementProblemAnalyzer that produces it and the description its tooltip resource carries — the real " +
        "count, not an estimate. The COVERAGE is the curated table on top: each id with the capability here that " +
        "answers it (the opt-in 'rider' inspection pack, a simplify_code IDE id, a Roslyn pack id, a dedicated " +
        "tool, a context action) or 'none'. It also reports the mapped ids this Rider version no longer declares " +
        "and the catalogue ids with no capability here. Use it before claiming an id is covered. Pass outputPath " +
        "to write the WHOLE uncovered catalogue to a Markdown page — grouped by language, every id with its " +
        "description — instead of only the first maxUncovered of them in the answer.")]
    public static Task<object> ResharperInspectionMap(
        IResharperInspectionMapService map,
        [Description("Optional: a Rider installation directory. Default: RIDER_HOME, or the newest one under Program Files/JetBrains.")] string? riderPath = null,
        [Description("Read the inspection catalogue out of the local Rider installation (default true).")] bool includeInstall = true,
        [Description("Maximum uncovered ids to list (default 100, max 1000).")] int maxUncovered = 100,
        [Description("Optional: write the full uncovered catalogue to this Markdown file (every id, grouped by language, with its description) and report the path.")] string? outputPath = null)
        => map.ReportAsync(riderPath, includeInstall, maxUncovered, outputPath);
}
