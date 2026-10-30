using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ConvertToRecordTool
{
    [McpServerTool(Name = "convert_to_record"), Description(
        "Rider's 'Convert to record'. The plain form changes the declaration to `record` (deliberate equality/" +
        "ToString change). positional=true additionally turns `class C { public int X { get; } ... ctor assigns }` " +
        "into `record C(int X)` — accepted only when one constructor assigns every parameter to an auto-property " +
        "of the same name. Compiler-gated. dryRun defaults to true.")]
    public static Task<object> ConvertToRecord(
        IConvertToRecordService svc,
        [Description("File that declares the class (absolute or relative).")] string filePath,
        [Description("Class to convert.")] string typeName,
        [Description("true also rewrites the constructor and its auto-properties into a positional record; false only flips the keyword.")] bool positional = false,
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
        => svc.ConvertToRecord(filePath, typeName, positional, dryRun);
}
