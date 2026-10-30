using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class InvertIfTool
{
    [McpServerTool(Name = "invert_if"), Description(
        "Swap the branches of an if/else and negate the condition (Rider's 'Invert if'), which is the first step " +
        "before flattening a conditional: `if (a && b) X else Y` becomes `if (!(a && b)) Y else X`. Requires an else " +
        "branch (without one the control flow would change) and refuses an 'else if' chain. dryRun defaults to true.")]
    public static Task<object> InvertIf(
        IInvertIfService svc,
        [Description("File containing the if statement (absolute or relative).")] string filePath,
        [Description("1-based line where the if starts.")] int line,
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
        => svc.InvertIf(filePath, line, dryRun);
}
