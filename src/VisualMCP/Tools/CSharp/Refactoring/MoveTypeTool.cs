using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class MoveTypeTool
{
    [McpServerTool, Description(
        "When you want a type in its own file named after it (e.g. MyClass -> MyClass.cs), use this INSTEAD OF cut-and-paste: " +
        "it moves the declaration and preserves the namespace and required usings (ReSharper 'Move to File'). " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> MoveType(
        IMoveTypeService svc,
        [Description("Name of the type to move (class, struct, interface, enum, record)")] string typeName,
        [Description("Optional: target directory for the new file. Defaults to the same directory as the source file.")] string? targetDirectory = null,
        [Description("Dry run — show what would happen without writing to disk (default: false)")] bool dryRun = false)
        => svc.MoveType(typeName, targetDirectory, dryRun);
}
