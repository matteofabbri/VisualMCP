using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ConvertToNonGlobalUsingTool
{
    [McpServerTool(Name = "convert_to_non_global_using"), Description(
        "Rider's 'Convert to non-global using': removes one `global using X;` from a file and writes `using X;` into " +
        "the files that stop compiling without it. Compiler-driven, so only the files that actually need it change; " +
        "the result is refused when the conversion would leave errors. dryRun defaults to true.")]
    public static Task<object> ConvertToNonGlobalUsing(
        IConvertToNonGlobalUsingService svc,
        [Description("File that declares the global using (absolute or relative).")] string filePath,
        [Description("The namespace of the global using to convert, e.g. 'System.Text'.")] string usingNamespace,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.ConvertToNonGlobalUsing(filePath, usingNamespace, dryRun);
}
