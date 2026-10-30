using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ImplementInpcTool
{
    [McpServerTool, Description(
        "Generate INotifyPropertyChanged boilerplate for a class: add the interface to the base list, " +
        "add the PropertyChanged event, add a SetField<T> helper, and convert auto-properties to " +
        "backing-field properties that call SetField. Equivalent to ReSharper's INPC generation. " +
        "Requires LoadSolution first.")]
    public static Task<object> ImplementInpc(
        IImplementInpcService svc,
        [Description("Name of the class to modify")] string className,
        [Description("Names of properties to convert to notifying properties (default: all public auto-properties)")] string[]? propertyNames = null,
        [Description("Dry run — return generated code without writing to disk (default: false)")] bool dryRun = false)
        => svc.ImplementInpc(className, propertyNames, dryRun);
}