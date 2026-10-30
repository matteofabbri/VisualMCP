using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class RemoveParamsModifierTool
{
    [McpServerTool(Name = "remove_params"), Description(
        "Remove the 'params' modifier from a method's last array parameter — the inverse of add_params. " +
        "Compiler-gated: if any call site passes the elements individually (the signature would stop matching) " +
        "nothing is written and the errors are reported. Previewed unless dryRun=false.")]
    public static Task<object> RemoveParams(
        IRemoveParamsModifierService svc,
        [Description("File that declares the method (absolute or relative to the solution).")] string filePath,
        [Description("Method name.")] string methodName,
        [Description("Containing type name, when the file declares several types with that method.")] string? containingType = null,
        [Description("Preview only (default: true).")] bool dryRun = true)
        => svc.RemoveParams(filePath, methodName, containingType, dryRun, CancellationToken.None);
}
