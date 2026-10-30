using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class MoveToNamespaceTool
{
    [McpServerTool(Name = "move_to_namespace"), Description(
        "Rider's 'Move to namespace': move the types declared in a file to another namespace, and add the " +
        "`using` to every other file that references them. The file must declare a single namespace (files with " +
        "several are refused) and at least one type directly in it. Compiler-gated: if a reference cannot be " +
        "resolved after the move the operation is refused with the new errors. dryRun defaults to true.")]
    public static Task<object> MoveToNamespace(
        IMoveToNamespaceService svc,
        [Description("File whose namespace should change (absolute or relative).")] string filePath,
        [Description("Target namespace, e.g. 'Probe.Core'.")] string targetNamespace,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.MoveToNamespace(filePath, targetNamespace, dryRun);
}
