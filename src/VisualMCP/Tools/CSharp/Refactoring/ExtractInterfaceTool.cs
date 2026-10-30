using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;
using System.ComponentModel;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ExtractInterfaceTool
{
    [McpServerTool, Description(
        "When you want an interface for a class's public surface, use this INSTEAD OF writing it by hand: it generates the interface from the class's public instance members " +
        "(with correct signatures), writes it to a new file, and optionally adds it to the class's base list (ReSharper 'Extract Interface'). " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> ExtractInterface(
        IExtractInterfaceService svc,
        [Description("Name of the class to extract an interface from")] string className,
        [Description("Name for the new interface (default: 'I' + className)")] string? interfaceName = null,
        [Description("Include public properties (default: true)")] bool includeProperties = true,
        [Description("Include public methods (default: true)")] bool includeMethods = true,
        [Description("Include public events (default: false)")] bool includeEvents = false,
        [Description("Add the interface to the class's base list (default: true)")] bool addToClass = true,
        [Description("Target directory for the new interface file (default: same directory as the class)")] string? targetDirectory = null,
        [Description("Dry run — return generated code without writing to disk (default: false)")] bool dryRun = false)
        => svc.ExtractInterface(className, interfaceName, includeProperties, includeMethods, includeEvents, addToClass, targetDirectory, dryRun);
}