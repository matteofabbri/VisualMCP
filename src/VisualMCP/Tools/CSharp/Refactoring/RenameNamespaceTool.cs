using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class RenameNamespaceTool
{
    [McpServerTool(Name = "rename_namespace"), Description(
        "Rider's 'Rename namespace': rename the LAST segment of a namespace (e.g. `Probe.Core` -> newName `Kernel` " +
        "gives `Probe.Kernel`) and update every declaration, using directive and qualified reference across the " +
        "solution through Roslyn's renamer. Compiler-gated: a rename that would leave a file unable to see a type is " +
        "refused with the new errors. dryRun defaults to true.")]
    public static Task<object> RenameNamespace(
        IRenameNamespaceService svc,
        [Description("Full name of the namespace to rename, e.g. 'Probe.Core'.")] string oldNamespace,
        [Description("New simple name for the last segment, e.g. 'Kernel'.")] string newName,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.RenameNamespace(oldNamespace, newName, dryRun);
}
